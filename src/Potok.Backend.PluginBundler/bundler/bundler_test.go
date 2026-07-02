package bundler

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func testLimits() Limits {
	return Limits{
		MaxModuleBytes: 1 << 20,
		MaxTotalBytes:  4 << 20,
		MaxModules:     50,
		FetchTimeout:   5 * time.Second,
		Minify:         false,
	}
}

// Serves a small multi-file plugin: entry imports a relative util and the bare
// `potok-sdk`, exercising the HTTP resolver + the SDK shim.
func pluginServer() *httptest.Server {
	files := map[string]string{
		"/index.js": `import { PotokSDK } from 'potok-sdk';
import { greet } from './utils/x.js';
PotokSDK.ui.render(greet("world"));`,
		"/utils/x.js": `export function greet(name) { return "hello " + name; }`,
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		body, ok := files[r.URL.Path]
		if !ok {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/javascript")
		_, _ = w.Write([]byte(body))
	})
	return httptest.NewServer(mux)
}

func TestBundle_MultiFileWithSDK(t *testing.T) {
	srv := pluginServer()
	defer srv.Close()

	// Permissive client: the SSRF guard lives in main's client, not here.
	b := New(srv.Client(), testLimits())
	out, err := b.Bundle(context.Background(), srv.URL+"/index.js")
	if err != nil {
		t.Fatalf("Bundle returned error: %v", err)
	}
	js := string(out)

	if !strings.Contains(js, "globalThis.PotokSDK") {
		t.Errorf("expected SDK shim to reference globalThis.PotokSDK; got:\n%s", js)
	}
	if !strings.Contains(js, "hello ") {
		t.Errorf("expected inlined util code ('hello '); got:\n%s", js)
	}
	if len(strings.TrimSpace(js)) == 0 {
		t.Errorf("expected non-empty ESM bundle")
	}
}

func TestBundle_RejectsBadEntry(t *testing.T) {
	b := New(http.DefaultClient, testLimits())
	if _, err := b.Bundle(context.Background(), "not-a-url"); err == nil {
		t.Fatal("expected error for invalid entry url")
	}
}

func TestBundle_ModuleLimit(t *testing.T) {
	srv := pluginServer()
	defer srv.Close()

	lim := testLimits()
	lim.MaxModules = 1 // entry alone is 1; the util import pushes over
	b := New(srv.Client(), lim)
	if _, err := b.Bundle(context.Background(), srv.URL+"/index.js"); err == nil {
		t.Fatal("expected module-count limit to be exceeded")
	}
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
