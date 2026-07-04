package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"runtime/debug"
	"strings"
	"syscall"
	"time"

	// "Potok.Backend.TorrentGo/auth"
	"Potok.Backend.TorrentGo/bt"
	"Potok.Backend.TorrentGo/config"
	"Potok.Backend.TorrentGo/handlers"
	"Potok.Backend.TorrentGo/speed"
	"Potok.Backend.TorrentGo/storage"
	"github.com/go-chi/chi/v5"
	"github.com/go-chi/chi/v5/middleware"
)

func raiseRlimit() {
	var rLimit syscall.Rlimit
	err := syscall.Getrlimit(syscall.RLIMIT_NOFILE, &rLimit)
	if err != nil {
		slog.Warn("Failed to get rlimit", "error", err)
		return
	}
	slog.Info("Current rlimit", "cur", rLimit.Cur, "max", rLimit.Max)
	rLimit.Cur = rLimit.Max
	err = syscall.Setrlimit(syscall.RLIMIT_NOFILE, &rLimit)
	if err != nil {
		slog.Warn("Failed to set rlimit", "error", err)
		return
	}
	slog.Info("Increased rlimit to max", "cur", rLimit.Cur)
}

func CORSMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		origin := r.Header.Get("Origin")
		if origin != "" {
			w.Header().Set("Access-Control-Allow-Origin", origin)
			w.Header().Set("Access-Control-Allow-Credentials", "true")
		} else {
			w.Header().Set("Access-Control-Allow-Origin", "*")
		}
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS")
		
		reqHeaders := r.Header.Get("Access-Control-Request-Headers")
		if reqHeaders != "" {
			w.Header().Set("Access-Control-Allow-Headers", reqHeaders)
		} else {
			w.Header().Set("Access-Control-Allow-Headers", "*")
		}
		
		w.Header().Set("Access-Control-Allow-Private-Network", "true")
		w.Header().Set("Access-Control-Expose-Headers", "Content-Range, Accept-Ranges, Content-Length, Content-Type")
		if r.Method == "OPTIONS" {
			w.WriteHeader(http.StatusOK)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func main() {
	// 1. Load config
	cfg := config.LoadConfig()

	// 2. Setup slog
	var level slog.Level
	switch strings.ToLower(cfg.LogLevel) {
	case "debug":
		level = slog.LevelDebug
	case "warn":
		level = slog.LevelWarn
	case "error":
		level = slog.LevelError
	default:
		level = slog.LevelInfo
	}
	slog.SetDefault(slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: level})))

	slog.Info("Starting Potok Go Torrent Engine v2...")
	raiseRlimit()

	// Soft Go-heap ceiling (POTOK_MEM_LIMIT_MB): the runtime GCs harder as it approaches this,
	// avoiding OOM. 0 = leave it to any external GOMEMLIMIT / no limit.
	if cfg.MemLimitBytes > 0 {
		debug.SetMemoryLimit(cfg.MemLimitBytes)
		slog.Info("Go memory soft limit set", "bytes", cfg.MemLimitBytes)
	}

	// 3. Setup custom storage and BT engine
	store := storage.NewStorage(cfg)
	engine, err := bt.NewEngine(cfg, store)
	if err != nil {
		slog.Error("Failed to initialize BT engine", "error", err)
		os.Exit(1)
	}

	// Setup speed monitor
	sm := speed.NewMonitor(engine.Client)
	monitorCtx, monitorCancel := context.WithCancel(context.Background())
	defer monitorCancel()
	go sm.Start(monitorCtx)

	// Setup thumbnail service
	ts := handlers.NewThumbnailService(cfg.ThumbCacheSize, cfg.ThumbCacheTTL)

	// Handler Context
	hCtx := handlers.NewHandlerContext(engine, sm, cfg, ts)

	// Reap idle torrents (no status-poll heartbeat past the timeout) → frees their RAM automatically.
	go hCtx.ReapIdleTorrents()

	// 4. Setup router
	r := chi.NewRouter()
	
    r.Use(CORSMiddleware)
	r.Use(middleware.Recoverer)
	r.Use(func(next http.Handler) http.Handler {
    	return http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
    		if req.URL.Path == "/health" || req.URL.Path == "/health/" {
    			next.ServeHTTP(w, req)
    			return
    		}
    		middleware.Logger(next).ServeHTTP(w, req)
    	})
    })
	

	// Ensure CORS is set on 404 and 405 responses
	r.NotFound(func(w http.ResponseWriter, r *http.Request) {
		CORSMiddleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			http.Error(w, "Not Found", http.StatusNotFound)
		})).ServeHTTP(w, r)
	})

	r.MethodNotAllowed(func(w http.ResponseWriter, r *http.Request) {
		CORSMiddleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			http.Error(w, "Method Not Allowed", http.StatusMethodNotAllowed)
		})).ServeHTTP(w, r)
	})

	// Health check (unauthenticated)
	r.Get("/health", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("OK"))
	})

	// Protected routes
	r.Group(func(r chi.Router) {
		// r.Use(auth.BasicAuth(cfg)) // Temporarily disabled by user request

		// API routes
		r.Route("/api/torrents", func(r chi.Router) {
			r.Post("/", hCtx.HandleGetFiles)
			r.Get("/{hash}", hCtx.HandleGetStatus)
			r.Get("/{hash}/diagnostics", hCtx.HandleGetDiagnostics)
			r.Delete("/{hash}", hCtx.HandleDeleteTorrent)
			r.Get("/{hash}/files/{fileIndex}/metadata", hCtx.HandleGetMediaMetadata)
			r.Get("/{hash}/files/{fileIndex}/thumbnail", hCtx.HandleGetThumbnail)
			r.Get("/{hash}/files/{fileIndex}/subtitles/{trackIndex}", hCtx.HandleGetSubtitles)
			r.Get("/{hash}/files/{fileIndex}/hls/{res}", hCtx.HandleHls)

			// RESTful Streaming sub-routes
			r.Get("/{hash}/files/{fileIndex}/stream", hCtx.HandleStream)
			r.Get("/{hash}/files/{fileIndex}/stream/{filename}", hCtx.HandleStream)
			r.Head("/{hash}/files/{fileIndex}/stream", hCtx.HandleStream)
			r.Head("/{hash}/files/{fileIndex}/stream/{filename}", hCtx.HandleStream)
		})

		// Backward compatibility for /stream routes
		r.Get("/stream/{hash}/{fileIndex}", hCtx.HandleStream)
		r.Get("/stream/{hash}/{fileIndex}/{filename}", hCtx.HandleStream)
		r.Get("/stream/{hash}/{fileIndex}/subtitles/{trackIndex}", hCtx.HandleGetSubtitles)
		r.Head("/stream/{hash}/{fileIndex}", hCtx.HandleStream)
		r.Head("/stream/{hash}/{fileIndex}/{filename}", hCtx.HandleStream)
	})

	// 5. Start Server
	server := &http.Server{
		Addr:    fmt.Sprintf(":%d", cfg.Port),
		Handler: r,
	}

	// Graceful shutdown context
	idleConnsClosed := make(chan struct{})
	go func() {
		sigChan := make(chan os.Signal, 1)
		signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)
		<-sigChan

		slog.Info("Shutting down gracefully...")
		monitorCancel()

		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()

		if err := server.Shutdown(ctx); err != nil {
			slog.Error("HTTP server shutdown error", "error", err)
		}

		if err := engine.Close(); err != nil {
			slog.Error("BT engine close error", "error", err)
		}

		close(idleConnsClosed)
	}()

	slog.Info("Server is running (HTTP)", "url", fmt.Sprintf("http://localhost:%d", cfg.Port))
	if err := server.ListenAndServe(); !errors.Is(err, http.ErrServerClosed) {
		slog.Error("HTTP server failed", "error", err)
		os.Exit(1)
	}

	<-idleConnsClosed
	slog.Info("Server stopped cleanly.")
}
