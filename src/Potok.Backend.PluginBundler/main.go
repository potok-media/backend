// Command potok-plugin-bundler is an INTERNAL, hidden co-process of the gateway.
// It compiles a multi-file plugin (entry URL + its HTTP imports) into a single
// IIFE bundle on demand. It is never exposed: it binds loopback only, ships
// inside the gateway image, is launched/owned by the gateway, and requires a
// hardcoded internal header. Nothing here is configurable — every knob is a
// constant on purpose.
//
// Production properties (high RPS, no leaks):
//   - one pooled HTTP client (bounded conns, dial-time SSRF guard),
//   - bounded build concurrency with backpressure (503 when saturated),
//   - singleflight: concurrent identical requests share one build,
//   - hard per-bundle size/module/time limits,
//   - server timeouts + graceful shutdown + soft memory limit.
//
// No artifacts are persisted: the client caches the bundle only for its session.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"runtime/debug"
	"strconv"
	"syscall"
	"time"

	"Potok.Backend.PluginBundler/bundler"

	"github.com/go-chi/chi/v5"
	"github.com/go-chi/chi/v5/middleware"
	"golang.org/x/sync/singleflight"
)

// --- Hardcoded config (no env, no external configuration by design) ---
const (
	listenAddr = "127.0.0.1:8787" // loopback ONLY — unreachable from outside the container

	// Shared secret the gateway (and only the gateway) sends. Defence-in-depth on
	// top of loopback binding. Same constant lives in the gateway client.
	internalHeader = "X-Potok-Bundler-Key"
	internalKey    = "p0t0k-bundler-internal-7f3a9c2e1b8d4056-do-not-expose"

	maxConcurrency = 8
	acquireTimeout = 2 * time.Second
	buildTimeout   = 20 * time.Second
	fetchTimeout   = 8 * time.Second
	maxModuleBytes = 5 << 20  // 5 MiB per file
	maxTotalBytes  = 32 << 20 // 32 MiB per bundle
	maxModules     = 200
	minify         = true

	softMemLimit = 512 << 20 // GC pressure ceiling
)

type server struct {
	bundler *bundler.Bundler
	sf      singleflight.Group
	sem     chan struct{}
}

func main() {
	slog.SetDefault(slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})))
	debug.SetMemoryLimit(softMemLimit)

	srv := &server{
		bundler: bundler.New(newHTTPClient(), bundler.Limits{
			MaxModuleBytes: maxModuleBytes,
			MaxTotalBytes:  maxTotalBytes,
			MaxModules:     maxModules,
			FetchTimeout:   fetchTimeout,
			Minify:         minify,
		}),
		sem: make(chan struct{}, maxConcurrency),
	}

	r := chi.NewRouter()
	r.Use(middleware.Recoverer)
	r.Get("/health", func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) })
	r.Get("/bundle", srv.handleBundle)

	httpServer := &http.Server{
		Addr:              listenAddr,
		Handler:           r,
		ReadHeaderTimeout: 5 * time.Second,
		WriteTimeout:      buildTimeout + 10*time.Second,
		IdleTimeout:       60 * time.Second,
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	go func() {
		slog.Info("plugin-bundler listening (loopback)", "addr", listenAddr)
		if err := httpServer.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			slog.Error("server error", "error", err)
			stop()
		}
	}()

	<-ctx.Done()
	slog.Info("shutting down")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	if err := httpServer.Shutdown(shutdownCtx); err != nil {
		slog.Error("graceful shutdown failed", "error", err)
	}
}

// handleBundle compiles the plugin at ?entry= (alias ?manifest=) into an IIFE.
// Concurrency is bounded and identical concurrent builds collapse into one.
func (s *server) handleBundle(w http.ResponseWriter, r *http.Request) {
	if r.Header.Get(internalHeader) != internalKey {
		http.Error(w, "forbidden", http.StatusForbidden)
		return
	}

	entry := r.URL.Query().Get("entry")
	if entry == "" {
		entry = r.URL.Query().Get("manifest")
	}
	if !validEntry(entry) {
		http.Error(w, "missing or invalid 'entry' (http/https url)", http.StatusBadRequest)
		return
	}

	ch := s.sf.DoChan(entry, func() (any, error) {
		// Build slot (leader only). Backpressure: fail fast when saturated.
		select {
		case s.sem <- struct{}{}:
			defer func() { <-s.sem }()
		case <-time.After(acquireTimeout):
			return nil, errBusy
		}
		// Independent deadline so a single caller's disconnect can't abort a build
		// that other waiters share.
		bctx, cancel := context.WithTimeout(context.Background(), buildTimeout)
		defer cancel()
		return s.bundler.Bundle(bctx, entry)
	})

	select {
	case <-r.Context().Done():
		return // caller went away; shared build continues for other waiters
	case res := <-ch:
		if res.Err != nil {
			if errors.Is(res.Err, errBusy) {
				http.Error(w, "bundler saturated, retry", http.StatusServiceUnavailable)
				return
			}
			slog.Warn("bundle failed", "entry", entry, "error", res.Err)
			http.Error(w, "bundle failed: "+res.Err.Error(), http.StatusBadGateway)
			return
		}
		out := res.Val.([]byte)
		w.Header().Set("Content-Type", "application/javascript; charset=utf-8")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Content-Length", strconv.Itoa(len(out)))
		_, _ = w.Write(out)
	}
}

var errBusy = errors.New("bundler saturated")

func validEntry(s string) bool {
	if s == "" {
		return false
	}
	u, err := url.ParseRequestURI(s)
	return err == nil && (u.Scheme == "http" || u.Scheme == "https") && u.Host != ""
}

// newHTTPClient is the single shared client for all module fetches. Connection
// pooling is bounded; a dial-time guard rejects private/loopback targets
// (resolved at connect time, closing the DNS-rebinding hole).
func newHTTPClient() *http.Client {
	dialer := &net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}
	dialer.Control = func(_, address string, _ syscall.RawConn) error {
		host, _, err := net.SplitHostPort(address)
		if err != nil {
			return err
		}
		ip := net.ParseIP(host)
		if ip == nil {
			return errors.New("unresolvable address")
		}
		if ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast() ||
			ip.IsLinkLocalMulticast() || ip.IsUnspecified() {
			return errors.New("blocked non-public address")
		}
		return nil
	}
	return &http.Client{
		Transport: &http.Transport{
			DialContext:           dialer.DialContext,
			MaxIdleConns:          100,
			MaxIdleConnsPerHost:   8,
			MaxConnsPerHost:       16,
			IdleConnTimeout:       60 * time.Second,
			TLSHandshakeTimeout:   5 * time.Second,
			ResponseHeaderTimeout: 8 * time.Second,
			ForceAttemptHTTP2:     true,
		},
	}
}
