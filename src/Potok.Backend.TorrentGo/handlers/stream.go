package handlers

import (
	"encoding/hex"
	"fmt"
	"log/slog"
	"mime"
	"net/http"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"Potok.Backend.TorrentGo/storage"
	"github.com/anacrolix/torrent/metainfo"
	"github.com/go-chi/chi/v5"
)

func (h *HandlerContext) HandleStream(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	slog.Debug("HandleStream request received", "hash", hashHex, "fileIndex", fileIndexStr, "raw", r.URL.Query().Get("raw"), "userAgent", r.Header.Get("User-Agent"))

	var infoHash metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		http.Error(w, "Invalid torrent hash format", http.StatusBadRequest)
		return
	}
	copy(infoHash[:], hexBytes)

	t, ok := h.Engine.Client.Torrent(infoHash)
	if !ok {
		http.Error(w, "Torrent not active. Please add it first.", http.StatusNotFound)
		return
	}

	fileIndex, err := strconv.Atoi(fileIndexStr)
	if err != nil || fileIndex < 1 {
		http.Error(w, "Invalid file index. Must be 1-based.", http.StatusBadRequest)
		return
	}

	if t.Info() == nil {
		slog.Info("Stream waiting for torrent info...", "hash", hashHex)
		select {
		case <-t.GotInfo():
			slog.Info("Stream: Torrent info resolved", "hash", hashHex)
		case <-r.Context().Done():
			return
		case <-time.After(30 * time.Second):
			http.Error(w, "Timeout waiting for torrent info", http.StatusGatewayTimeout)
			return
		}
	}

	files := t.Files()
	idx := fileIndex - 1
	if idx < 0 || idx >= len(files) {
		http.Error(w, fmt.Sprintf("File index out of bounds. Must be between 1 and %d.", len(files)), http.StatusBadRequest)
		return
	}

	file := files[idx]
	isRaw := r.URL.Query().Get("raw") == "true"
	isFFmpeg := strings.HasPrefix(r.Header.Get("User-Agent"), "Lavf/")

	cache, ok := h.Engine.Storage.GetCache(infoHash)
	if !ok {
		http.Error(w, "Storage cache not found for this torrent", http.StatusInternalServerError)
		return
	}

	// 1. If it's a raw stream or read by ffmpeg direct analyzer, serve progressive bytes
	if isRaw || isFFmpeg {
		slog.Debug("Serving raw/ffmpeg stream", "path", file.Path(), "offset", file.Offset(), "length", file.Length())
		reader := storage.NewReader(r.Context(), t, cache, file.Offset(), file.Length())
		defer reader.Close()

		contentType := getMimeType(file.Path())
		w.Header().Set("Content-Type", contentType)
		w.Header().Set("Accept-Ranges", "bytes")
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Cache-Control", "no-cache, no-store, must-revalidate")
		w.Header().Set("Pragma", "no-cache")
		w.Header().Set("Expires", "0")

		http.ServeContent(w, r, filepath.Base(file.Path()), time.Time{}, reader)
		return
	}

	// 2. Perform Preload (only on initial player requests - range starts at 0 or empty range)
	rangeHeader := r.Header.Get("Range")
	shouldPreload := rangeHeader == "" || strings.HasPrefix(rangeHeader, "bytes=0-")
	if shouldPreload {
		go func() {
			err := cache.Preload(t, file, h.Config.PreloadBytes)
			if err != nil {
				slog.Warn("Preload failed", "error", err)
			}
		}()
	}

	// 3. Check if dynamic fMP4 remuxing is requested or required
	audioParam := r.URL.Query().Get("audio")
	startParam := r.URL.Query().Get("start")
	remuxParam := r.URL.Query().Get("remux") == "true"

	if audioParam != "" || startParam != "" || remuxParam {
		if _, err := exec.LookPath("ffmpeg"); err == nil {
			localStreamURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))

			w.Header().Set("Content-Type", "video/mp4")
			w.Header().Set("Access-Control-Allow-Origin", "*")
			w.Header().Set("Access-Control-Expose-Headers", "Content-Range, Accept-Ranges, Content-Length, Content-Type")
			w.Header().Set("Cache-Control", "no-cache, no-store, must-revalidate")
			w.Header().Set("Pragma", "no-cache")
			w.Header().Set("Expires", "0")

			args := []string{"-nostdin"}
			if startParam != "" {
				args = append(args, "-noaccurate_seek", "-ss", startParam)
			}
			args = append(args, "-i", localStreamURL)
			args = append(args, "-map", "0:v:0")

			if audioParam != "" && audioParam != "0" && audioParam != "default" {
				args = append(args, "-map", fmt.Sprintf("0:%s", audioParam))
			} else {
				args = append(args, "-map", "0:a:0?")
			}

			fileExt := strings.ToLower(filepath.Ext(file.Path()))
			if fileExt == ".avi" {
				args = append(args,
					"-c:v", "libx264",
					"-preset", "ultrafast",
					"-profile:v", "baseline",
					"-level", "3.0",
					"-pix_fmt", "yuv420p",
				)
			} else {
				args = append(args, "-c:v", "copy")
			}

			args = append(args,
				"-c:a", "aac",
				"-af", "aresample=async=1",
				"-avoid_negative_ts", "make_zero",
				"-f", "mp4",
				"-movflags", "frag_keyframe+empty_moov",
				"-",
			)

			cmd := exec.CommandContext(r.Context(), "ffmpeg", args...)
			cmd.Stdout = w
			cmd.Stderr = nil

			if err := cmd.Start(); err != nil {
				slog.Error("Failed to spawn ffmpeg remuxer", "error", err)
				http.Error(w, "ffmpeg spawn failed", http.StatusInternalServerError)
				return
			}

			if err := cmd.Wait(); err != nil {
				if r.Context().Err() == nil {
					slog.Warn("ffmpeg remuxer completed with error", "error", err)
				}
			}
			return
		}
	}

	// 4. Fallback: serve progressive bytes using custom storage reader
	reader := storage.NewReader(r.Context(), t, cache, file.Offset(), file.Length())
	defer reader.Close()

	contentType := getMimeType(file.Path())
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Accept-Ranges", "bytes")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "no-cache, no-store, must-revalidate")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")

	slog.Info("Streaming file directly", "path", file.Path(), "mime", contentType, "size", file.Length())
	http.ServeContent(w, r, filepath.Base(file.Path()), time.Time{}, reader)
}

func getMimeType(path string) string {
	ext := strings.ToLower(filepath.Ext(path))
	switch ext {
	case ".mkv":
		return "video/x-matroska"
	case ".mp4":
		return "video/mp4"
	case ".avi":
		return "video/x-msvideo"
	case ".ts":
		return "video/MP2T"
	case ".mov":
		return "video/quicktime"
	default:
		t := mime.TypeByExtension(ext)
		if t != "" {
			return t
		}
		return "application/octet-stream"
	}
}
