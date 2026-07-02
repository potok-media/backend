// Package bundler turns a multi-file plugin (entry URL + its relative imports,
// fetched over HTTP) into a single self-contained IIFE bundle using esbuild's Go
// API. The plugin source is only *transformed* — never executed — so bundling is
// not a code-execution surface.
package bundler

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"sync/atomic"
	"time"

	"github.com/evanw/esbuild/pkg/api"
)

// Namespaces keep esbuild's virtual modules apart from on-disk resolution.
const (
	nsHTTP = "potok-http"
	nsSDK  = "potok-sdk"
)

// sdkSpecifiers are bare imports remapped to a virtual module that re-exports the
// host-injected global. The plugin bundle never carries the SDK itself — the
// runtime (iframe / QuickJS) provides `globalThis.PotokSDK`.
var sdkSpecifiers = map[string]bool{
	"potok-sdk":        true,
	"@potok/sdk":       true,
	"@potok/sdk-types": true,
}

// Limits bound a single bundle so a malicious or broken plugin can't exhaust
// memory or hang a worker. All are hard caps enforced during fetch.
type Limits struct {
	MaxModuleBytes int64         // per fetched file
	MaxTotalBytes  int64         // summed across the whole import graph
	MaxModules     int           // number of files in the graph
	FetchTimeout   time.Duration // per individual HTTP fetch
	Minify         bool
}

// Bundler is safe for concurrent use; it holds only immutable config plus a
// shared, pooled HTTP client. All per-build mutable state lives in buildState.
type Bundler struct {
	client *http.Client
	limits Limits
}

func New(client *http.Client, limits Limits) *Bundler {
	return &Bundler{client: client, limits: limits}
}

// buildState carries the per-build counters. A fresh one is created for every
// Bundle call, so nothing leaks across requests.
type buildState struct {
	modules    int32
	totalBytes int64
}

// Bundle fetches entryURL and its transitive imports over HTTP and returns one
// IIFE. ctx governs every network fetch; esbuild's own work is CPU-bound and
// bounded by the limits above.
func (b *Bundler) Bundle(ctx context.Context, entryURL string) ([]byte, error) {
	if _, err := url.ParseRequestURI(entryURL); err != nil {
		return nil, fmt.Errorf("invalid entry url: %w", err)
	}

	st := &buildState{}
	result := api.Build(api.BuildOptions{
		EntryPoints: []string{entryURL},
		Bundle: true,
		Write:  false,
		// ESM (not IIFE) + esnext so plugins that use top-level await bundle and run. Loaded as a
		// module on both clients (web: blob + import(); native: QuickJS evaluate asModule=true).
		Format:   api.FormatESModule,
		Platform: api.PlatformBrowser,
		Target:   api.ESNext,
		LogLevel: api.LogLevelSilent,
		MinifyWhitespace:  b.limits.Minify,
		MinifyIdentifiers: b.limits.Minify,
		MinifySyntax:      b.limits.Minify,
		Plugins: []api.Plugin{b.httpPlugin(ctx, st)},
	})

	if len(result.Errors) > 0 {
		msgs := make([]string, 0, len(result.Errors))
		for _, e := range result.Errors {
			loc := ""
			if e.Location != nil {
				loc = fmt.Sprintf(" (%s:%d)", e.Location.File, e.Location.Line)
			}
			msgs = append(msgs, e.Text+loc)
		}
		return nil, fmt.Errorf("bundle failed: %s", strings.Join(msgs, "; "))
	}
	if len(result.OutputFiles) == 0 {
		return nil, fmt.Errorf("bundle produced no output")
	}
	return result.OutputFiles[0].Contents, nil
}

func (b *Bundler) httpPlugin(ctx context.Context, st *buildState) api.Plugin {
	return api.Plugin{
		Name: "potok-http",
		Setup: func(pb api.PluginBuild) {
			pb.OnResolve(api.OnResolveOptions{Filter: `.*`}, func(args api.OnResolveArgs) (api.OnResolveResult, error) {
				// Bare SDK import -> virtual module exposing the global.
				if sdkSpecifiers[args.Path] {
					return api.OnResolveResult{Path: args.Path, Namespace: nsSDK}, nil
				}
				// Entry point or any absolute http(s) import.
				if args.Kind == api.ResolveEntryPoint ||
					strings.HasPrefix(args.Path, "http://") || strings.HasPrefix(args.Path, "https://") {
					u, err := url.Parse(args.Path)
					if err != nil {
						return api.OnResolveResult{}, err
					}
					return api.OnResolveResult{Path: u.String(), Namespace: nsHTTP}, nil
				}
				// Relative import resolved against the importing http URL.
				if args.Namespace == nsHTTP {
					base, err := url.Parse(args.Importer)
					if err != nil {
						return api.OnResolveResult{}, err
					}
					ref, err := url.Parse(args.Path)
					if err != nil {
						return api.OnResolveResult{}, err
					}
					return api.OnResolveResult{Path: base.ResolveReference(ref).String(), Namespace: nsHTTP}, nil
				}
				// Anything else (bare npm specifier we don't know) is unsupported —
				// plugins must vendor their deps or use the SDK.
				return api.OnResolveResult{}, fmt.Errorf("cannot resolve %q from %q", args.Path, args.Importer)
			})

			pb.OnLoad(api.OnLoadOptions{Filter: `.*`, Namespace: nsSDK}, func(api.OnLoadArgs) (api.OnLoadResult, error) {
				contents := "const __sdk = globalThis.PotokSDK;\nexport const PotokSDK = __sdk;\nexport default __sdk;"
				loader := api.LoaderJS
				return api.OnLoadResult{Contents: &contents, Loader: loader}, nil
			})

			pb.OnLoad(api.OnLoadOptions{Filter: `.*`, Namespace: nsHTTP}, func(args api.OnLoadArgs) (api.OnLoadResult, error) {
				body, err := b.fetch(ctx, args.Path, st)
				if err != nil {
					return api.OnLoadResult{}, err
				}
				loader := loaderForURL(args.Path)
				return api.OnLoadResult{Contents: &body, Loader: loader}, nil
			})
		},
	}
}

// fetch pulls one module over HTTP, enforcing per-file, total-size and
// module-count caps. The body is always closed and read through a LimitReader so
// a giant or never-ending response can't blow up memory.
func (b *Bundler) fetch(ctx context.Context, rawURL string, st *buildState) (string, error) {
	if n := atomic.AddInt32(&st.modules, 1); int(n) > b.limits.MaxModules {
		return "", fmt.Errorf("module count limit (%d) exceeded", b.limits.MaxModules)
	}

	fctx, cancel := context.WithTimeout(ctx, b.limits.FetchTimeout)
	defer cancel()

	req, err := http.NewRequestWithContext(fctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return "", err
	}
	resp, err := b.client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("fetch %s: HTTP %d", rawURL, resp.StatusCode)
	}

	// +1 so we can detect "exactly at limit means there might be more".
	data, err := io.ReadAll(io.LimitReader(resp.Body, b.limits.MaxModuleBytes+1))
	if err != nil {
		return "", fmt.Errorf("read %s: %w", rawURL, err)
	}
	if int64(len(data)) > b.limits.MaxModuleBytes {
		return "", fmt.Errorf("module %s exceeds %d bytes", rawURL, b.limits.MaxModuleBytes)
	}
	if total := atomic.AddInt64(&st.totalBytes, int64(len(data))); total > b.limits.MaxTotalBytes {
		return "", fmt.Errorf("bundle exceeds total byte limit (%d)", b.limits.MaxTotalBytes)
	}
	if strings.HasPrefix(strings.TrimLeft(string(data), " \t\r\n"), "<") {
		return "", fmt.Errorf("fetch %s: received HTML, not JavaScript", rawURL)
	}
	return string(data), nil
}

func loaderForURL(rawURL string) api.Loader {
	path := rawURL
	if i := strings.IndexAny(path, "?#"); i >= 0 {
		path = path[:i]
	}
	switch {
	case strings.HasSuffix(path, ".json"):
		return api.LoaderJSON
	case strings.HasSuffix(path, ".ts"):
		return api.LoaderTS
	case strings.HasSuffix(path, ".tsx"):
		return api.LoaderTSX
	case strings.HasSuffix(path, ".jsx"):
		return api.LoaderJSX
	case strings.HasSuffix(path, ".mjs"):
		return api.LoaderJS
	default:
		return api.LoaderJS
	}
}
