package handlers

import (
	"bytes"
	"fmt"
	"hash/fnv"
	"log/slog"
	"net/http"
	"os/exec"
	"strings"

	"github.com/go-chi/chi/v5"
)

func (h *HandlerContext) HandleGetSubtitles(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	trackIndexStr := chi.URLParam(r, "trackIndex")

	streamURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))

	format := r.URL.Query().Get("format")
	ffmpegFormat := "webvtt"
	contentType := "text/vtt; charset=utf-8"

	if format != "" {
		isAlpha := true
		for _, r := range format {
			if !((r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9')) {
				isAlpha = false
				break
			}
		}
		if isAlpha && len(format) < 10 {
			ffmpegFormat = format
			switch strings.ToLower(format) {
			case "ass", "ssa":
				contentType = "text/x-ssa; charset=utf-8"
			case "srt", "subrip":
				contentType = "text/srt; charset=utf-8"
			default:
				contentType = "text/plain; charset=utf-8"
			}
		}
	}

	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Access-Control-Allow-Origin", "*")

	if _, err := exec.LookPath("ffmpeg"); err != nil {
		http.Error(w, "ffmpeg not found in PATH", http.StatusInternalServerError)
		return
	}

	args := []string{}
	if strings.HasPrefix(streamURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args,
		"-i", streamURL,
		"-map", fmt.Sprintf("0:s:%s", trackIndexStr),
		"-f", ffmpegFormat,
		"-",
	)
	cmd := exec.CommandContext(r.Context(), "ffmpeg", args...)

	// Buffer the output so we can attach a content-based ETag and serve conditional
	// requests. The subtitle content for a given (hash, file, track, format) tuple is
	// immutable, so aggressive client-side caching makes seeks/replays instant.
	var out bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = nil

	if err := cmd.Run(); err != nil {
		if r.Context().Err() != nil {
			return // client disconnected, nothing to write
		}
		slog.Warn("ffmpeg subtitle extraction completed with error", "error", err)
		http.Error(w, "subtitle extraction failed", http.StatusInternalServerError)
		return
	}

	data := out.Bytes()

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
