package handlers

import (
	"fmt"
	"log/slog"
	"net/http"
	"os/exec"

	"github.com/go-chi/chi/v5"
)

func (h *HandlerContext) HandleGetSubtitles(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	trackIndexStr := chi.URLParam(r, "trackIndex")

	streamURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))

	w.Header().Set("Content-Type", "text/vtt; charset=utf-8")
	w.Header().Set("Access-Control-Allow-Origin", "*")

	if _, err := exec.LookPath("ffmpeg"); err != nil {
		http.Error(w, "ffmpeg not found in PATH", http.StatusInternalServerError)
		return
	}

	cmd := exec.CommandContext(r.Context(), "ffmpeg",
		"-i", streamURL,
		"-map", fmt.Sprintf("0:s:%s", trackIndexStr),
		"-f", "webvtt",
		"-",
	)

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
