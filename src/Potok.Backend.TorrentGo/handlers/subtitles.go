package handlers

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"hash/fnv"
	"log/slog"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"github.com/go-chi/chi/v5"
)

// Wall-clock cap for a subtitle extraction (a full-file demux over the loopback torrent stream).
// Generous because a still-downloading torrent must read the whole file, but bounded so a stuck
// ffmpeg can't hold the extraction semaphore forever.
const subtitleExtractTimeout = 240 * time.Second

// resolveSubtitleFormat validates the ?format= query and returns the ffmpeg format token plus the
// HTTP Content-Type to serve. Defaults to webvtt.
func resolveSubtitleFormat(format string) (ffmpegFormat, contentType string) {
	ffmpegFormat = "webvtt"
	contentType = "text/vtt; charset=utf-8"
	if format == "" || len(format) >= 10 {
		return
	}
	for _, r := range format {
		if !((r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9')) {
			return
		}
	}
	ffmpegFormat = format
	switch strings.ToLower(format) {
	case "ass", "ssa":
		contentType = "text/x-ssa; charset=utf-8"
	case "srt", "subrip":
		contentType = "text/srt; charset=utf-8"
	default:
		contentType = "text/plain; charset=utf-8"
	}
	return
}

// isTextSubtitleCodec reports whether a subtitle codec is text-based (extractable to ass/webvtt).
// Image-based subs (PGS/VobSub/DVB) can't be converted to text — they'd fail the batch extraction
// and the player can't render them anyway — so they're skipped.
func isTextSubtitleCodec(codec string) bool {
	switch strings.ToLower(codec) {
	case "ass", "ssa", "subrip", "srt", "webvtt", "vtt", "mov_text", "text",
		"microdvd", "stl", "subviewer", "subviewer1", "jacosub", "sami", "realtext", "mpl2", "pjs", "vplayer":
		return true
	default:
		return false
	}
}

func (h *HandlerContext) HandleGetSubtitles(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	trackIndexStr := chi.URLParam(r, "trackIndex")

	ffmpegFormat, contentType := resolveSubtitleFormat(r.URL.Query().Get("format"))

	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Access-Control-Allow-Origin", "*")

	if _, err := exec.LookPath(h.ffmpegPath); err != nil {
		http.Error(w, "ffmpeg not found", http.StatusInternalServerError)
		return
	}

	cacheKey := fmt.Sprintf("%s_%s_%s_%s", hashHex, fileIndexStr, trackIndexStr, ffmpegFormat)

	data, err := h.getOrExtractSubtitle(r.Context(), hashHex, fileIndexStr, trackIndexStr, ffmpegFormat, cacheKey)
	if err != nil {
		if r.Context().Err() != nil {
			return // client disconnected, nothing to write
		}
		slog.Warn("subtitle extraction failed", "error", err)
		http.Error(w, "subtitle extraction failed", http.StatusInternalServerError)
		return
	}

	// The subtitle content for a given (hash, file, track, format) tuple is immutable, so attach a
	// content ETag and serve conditional requests — seeks/replays become instant.
	hsh := fnv.New64a()
	_, _ = hsh.Write(data)
	etag := fmt.Sprintf("\"%x\"", hsh.Sum64())

	w.Header().Set("Cache-Control", "public, max-age=86400, immutable")
	w.Header().Set("ETag", etag)

	if match := r.Header.Get("If-None-Match"); match != "" && strings.Contains(match, etag) {
		w.WriteHeader(http.StatusNotModified)
		return
	}

	_, _ = w.Write(data)
}

// getOrExtractSubtitle returns the requested track+format bytes, serving from the RAM cache whenever
// possible. The FIRST request for a file triggers ONE ffmpeg pass that extracts ALL text subtitle
// tracks at once (shared across concurrent requests via singleflight); every later request — any
// track, seek or replay — is a cache hit. A track/format the batch didn't produce falls back to a
// single-track extraction (also cached). This turns "N parallel full-file demuxes on every play"
// into "one demux per file, ever".
func (h *HandlerContext) getOrExtractSubtitle(ctx context.Context, hashHex, fileIndexStr, trackIndexStr, ffmpegFormat, cacheKey string) ([]byte, error) {
	if v, ok := h.subtitleCache.Load(cacheKey); ok {
		return v.([]byte), nil
	}

	fileKey := fmt.Sprintf("%s_%s", hashHex, fileIndexStr)
	if _, done := h.subtitleExtracted.Load(fileKey); !done {
		_, err, _ := h.subtitleSFG.Do(fileKey, func() (interface{}, error) {
			if _, done := h.subtitleExtracted.Load(fileKey); done {
				return nil, nil
			}
			if err := h.extractAllSubtitles(ctx, hashHex, fileIndexStr); err != nil {
				return nil, err
			}
			// Mark done even on best-effort partial success so the full-file demux never repeats.
			h.subtitleExtracted.Store(fileKey, true)
			return nil, nil
		})
		if err != nil {
			return nil, err
		}
	}

	if v, ok := h.subtitleCache.Load(cacheKey); ok {
		return v.([]byte), nil
	}

	// The batch pass didn't yield this exact (track, format) — extract just this one and cache it.
	return h.extractSingleSubtitle(ctx, hashHex, fileIndexStr, trackIndexStr, ffmpegFormat, cacheKey)
}

// extractAllSubtitles runs ONE ffmpeg pass that demuxes the file a single time and writes every text
// subtitle track to a temp file, then loads the results into the RAM cache. Best-effort: a partial
// ffmpeg failure still caches whatever tracks were produced; anything missing falls back to a
// single-track extraction later.
func (h *HandlerContext) extractAllSubtitles(ctx context.Context, hashHex, fileIndexStr string) error {
	metaBytes, err := h.probeAndCacheMetadata(ctx, hashHex, fileIndexStr)
	if err != nil {
		return fmt.Errorf("subtitle metadata probe failed: %w", err)
	}
	var meta ClientMetadata
	if err := json.Unmarshal(metaBytes, &meta); err != nil {
		return fmt.Errorf("parse metadata for subtitles: %w", err)
	}

	tmpDir, err := os.MkdirTemp("", "potok-subs-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tmpDir)

	type subOut struct {
		relIndex int
		format   string
		path     string
	}
	var outs []subOut

	streamURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
	args := []string{}
	if strings.HasPrefix(streamURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args, "-i", streamURL)

	for _, tr := range meta.Tracks {
		if tr.Type != "subtitle" || !isTextSubtitleCodec(tr.Codec) {
			continue
		}
		format, ext := "webvtt", "vtt"
		if c := strings.ToLower(tr.Codec); c == "ass" || c == "ssa" {
			format, ext = "ass", "ass"
		}
		path := filepath.Join(tmpDir, fmt.Sprintf("%d.%s", tr.RelIndex, ext))
		args = append(args, "-map", fmt.Sprintf("0:s:%d", tr.RelIndex), "-f", format, path)
		outs = append(outs, subOut{relIndex: tr.RelIndex, format: format, path: path})
	}

	if len(outs) == 0 {
		return nil // no text subtitle tracks to extract
	}

	// One heavy demux at a time — several at once is what pegged the CPU.
	select {
	case h.subtitleSem <- struct{}{}:
		defer func() { <-h.subtitleSem }()
	case <-ctx.Done():
		return ctx.Err()
	}

	extractCtx, cancel := context.WithTimeout(ctx, subtitleExtractTimeout)
	defer cancel()

	var stderr bytes.Buffer
	cmd := exec.CommandContext(extractCtx, h.ffmpegPath, args...)
	cmd.Stderr = &stderr
	if err := cmd.Run(); err != nil {
		if extractCtx.Err() != nil {
			return extractCtx.Err()
		}
		// Best-effort: keep whatever tracks ffmpeg managed to write before erroring.
		slog.Warn("batch subtitle extraction ffmpeg error (keeping partial results)",
			"hash", hashHex, "file", fileIndexStr, "error", err)
	}

	stored := 0
	for _, o := range outs {
		data, rerr := os.ReadFile(o.path)
		if rerr != nil || len(data) == 0 {
			continue
		}
		key := fmt.Sprintf("%s_%s_%d_%s", hashHex, fileIndexStr, o.relIndex, o.format)
		h.subtitleCache.Store(key, data)
		stored++
	}
	slog.Info("batch subtitle extraction done", "hash", hashHex, "file", fileIndexStr, "tracks", len(outs), "stored", stored)
	return nil
}

// extractSingleSubtitle extracts one subtitle track in the requested format (the fallback path for a
// track/format the batch pass didn't cover) and caches the result.
func (h *HandlerContext) extractSingleSubtitle(ctx context.Context, hashHex, fileIndexStr, trackIndexStr, ffmpegFormat, cacheKey string) ([]byte, error) {
	select {
	case h.subtitleSem <- struct{}{}:
		defer func() { <-h.subtitleSem }()
	case <-ctx.Done():
		return nil, ctx.Err()
	}

	streamURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
	args := []string{}
	if strings.HasPrefix(streamURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args, "-i", streamURL, "-map", fmt.Sprintf("0:s:%s", trackIndexStr), "-f", ffmpegFormat, "-")

	extractCtx, cancel := context.WithTimeout(ctx, subtitleExtractTimeout)
	defer cancel()

	var out bytes.Buffer
	cmd := exec.CommandContext(extractCtx, h.ffmpegPath, args...)
	cmd.Stdout = &out
	cmd.Stderr = nil
	if err := cmd.Run(); err != nil {
		if extractCtx.Err() != nil {
			return nil, extractCtx.Err()
		}
		return nil, fmt.Errorf("single subtitle extraction failed: %w", err)
	}

	data := out.Bytes()
	h.subtitleCache.Store(cacheKey, data)
	return data, nil
}
