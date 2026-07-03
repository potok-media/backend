package handlers

import (
	"fmt"
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

	cmd.Stdout = w
	cmd.Stderr = nil

	if err := cmd.Start(); err != nil {
		slog.Error("Failed to spawn ffmpeg for subtitles", "error", err)
		http.Error(w, "ffmpeg spawn failed", http.StatusInternalServerError)
		return
	}

	if err := cmd.Wait(); err != nil {
		if r.Context().Err() == nil {
			slog.Warn("ffmpeg subtitle extraction completed with error", "error", err)
		}
	}
}
