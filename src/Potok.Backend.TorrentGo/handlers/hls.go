package handlers

import (
	"context"
	"encoding/hex"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strconv"
	"strings"
	"time"

	"Potok.Backend.TorrentGo/stream/hls"
	"github.com/anacrolix/torrent/metainfo"
	"github.com/go-chi/chi/v5"
)

func (h *HandlerContext) HandleHLSUpload(w http.ResponseWriter, r *http.Request) {
	sessionKey := chi.URLParam(r, "sessionKey")
	filename := chi.URLParam(r, "filename")

	s, ok := h.HLSSessionManager.Get(sessionKey)
	if !ok {
		http.Error(w, "Session not found", http.StatusNotFound)
		return
	}

	body, err := io.ReadAll(r.Body)
	if err != nil {
		http.Error(w, "Failed to read body", http.StatusInternalServerError)
		return
	}

	if strings.HasSuffix(filename, ".m3u8") {
		s.SetPlaylistData(string(body))
	} else if strings.HasSuffix(filename, ".ts") {
		slog.Debug("HLS uploaded segment", "filename", filename, "size", len(body), "session", sessionKey)
		s.AddSegment(filename, body)
	}

	w.WriteHeader(http.StatusOK)
}

func (h *HandlerContext) HandleHLSMaster(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	audioParam := r.URL.Query().Get("audio")
	if audioParam == "" {
		audioParam = "0"
	}
	startParam := r.URL.Query().Get("start")

	sessionKey := fmt.Sprintf("%s_%s_%s", hashHex, fileIndexStr, audioParam)
	
	_, err := h.getOrCreateHLSSession(r.Context(), hashHex, fileIndexStr, audioParam, startParam, sessionKey)
	if err != nil {
		slog.Error("Failed to initiate HLS session on master request", "error", err)
		http.Error(w, fmt.Sprintf("failed to init session: %v", err), http.StatusInternalServerError)
		return
	}

	masterPlaylist := hls.GenerateMasterPlaylist(audioParam, startParam)
	w.Header().Set("Content-Type", "application/x-mpegURL")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "no-cache, no-store, must-revalidate")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	_, _ = w.Write([]byte(masterPlaylist))
}

func (h *HandlerContext) HandleHLSMedia(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	audioParam := r.URL.Query().Get("audio")
	if audioParam == "" {
		audioParam = "0"
	}
	startParam := r.URL.Query().Get("start")

	sessionKey := fmt.Sprintf("%s_%s_%s", hashHex, fileIndexStr, audioParam)

	s, err := h.getOrCreateHLSSession(r.Context(), hashHex, fileIndexStr, audioParam, startParam, sessionKey)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	s.Touch()

	// Get duration of the video
	duration, err := h.getOrProbeDuration(r.Context(), hashHex, fileIndexStr)
	if err != nil || duration <= 0 {
		// Fallback to ffmpeg's playlist if duration probe fails
		for i := 0; i < 160; i++ {
			data := s.GetPlaylistData()
			if data != "" {
				w.Header().Set("Content-Type", "application/x-mpegURL")
				w.Header().Set("Access-Control-Allow-Origin", "*")
				w.Header().Set("Cache-Control", "no-cache, no-store, must-revalidate")
				w.Header().Set("Pragma", "no-cache")
				w.Header().Set("Expires", "0")
				_, _ = w.Write([]byte(data))
				return
			}
			if s.HasFailed() {
				http.Error(w, "ffmpeg HLS process failed or exited early", http.StatusInternalServerError)
				return
			}
			time.Sleep(250 * time.Millisecond)
		}
		http.Error(w, "Timeout waiting for playlist data", http.StatusGatewayTimeout)
		return
	}

	// Generate static VOD playlist
	segDur := h.Config.HLSSegmentDuration
	if segDur <= 0 {
		segDur = 6
	}
	totalSegs := int(duration / float64(segDur))
	if duration-float64(totalSegs*segDur) > 0.1 {
		totalSegs++
	}

	var sb strings.Builder
	sb.WriteString("#EXTM3U\n")
	sb.WriteString("#EXT-X-VERSION:3\n")
	sb.WriteString("#EXT-X-PLAYLIST-TYPE:VOD\n")
	sb.WriteString(fmt.Sprintf("#EXT-X-TARGETDURATION:%d\n", segDur))
	sb.WriteString("#EXT-X-MEDIA-SEQUENCE:0\n")

	for i := 0; i < totalSegs; i++ {
		dur := float64(segDur)
		if i == totalSegs-1 {
			dur = duration - float64(i*segDur)
			if dur <= 0 {
				dur = float64(segDur)
			}
		}
		sb.WriteString(fmt.Sprintf("#EXTINF:%f,\n", dur))
		sb.WriteString(fmt.Sprintf("segment_%05d.ts\n", i))
	}
	sb.WriteString("#EXT-X-ENDLIST\n")

	w.Header().Set("Content-Type", "application/x-mpegURL")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "no-cache, no-store, must-revalidate")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	_, _ = w.Write([]byte(sb.String()))
}

func (h *HandlerContext) HandleHLSSegment(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	segmentName := chi.URLParam(r, "segment") + ".ts"

	audioParam := r.URL.Query().Get("audio")
	if audioParam == "" {
		audioParam = "0"
	}
	startParam := r.URL.Query().Get("start")

	sessionKey := fmt.Sprintf("%s_%s_%s", hashHex, fileIndexStr, audioParam)

	s, err := h.getOrCreateHLSSession(r.Context(), hashHex, fileIndexStr, audioParam, startParam, sessionKey)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	s.Touch()

	reqIdx := parseSegmentIdx(segmentName)

	seekNeeded, newStartParam, startNum := s.CheckAndSeek(reqIdx, h.Config.HLSSegmentDuration)
	if seekNeeded {
		inputURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
		uploadURL := h.getLoopbackURL(fmt.Sprintf("/api/hls/upload/%s", sessionKey))

		err = s.StartFFmpeg(context.Background(), inputURL, audioParam, newStartParam, startNum, uploadURL)
		if err != nil {
			http.Error(w, "Failed to seek: "+err.Error(), http.StatusInternalServerError)
			return
		}
	}

	seg := s.GetOrCreateSegmentPlaceholder(segmentName, reqIdx)

	timeout := time.After(45 * time.Second)
	ticker := time.NewTicker(250 * time.Millisecond)
	defer ticker.Stop()

	for {
		select {
		case <-seg.Ready:
			if len(seg.Data) == 0 {
				http.Error(w, "Segment not available (seeked)", http.StatusNotFound)
				return
			}
			slog.Debug("Serving HLS segment", "segment", segmentName, "size", len(seg.Data))
			w.Header().Set("Content-Type", "video/MP2T")
			w.Header().Set("Content-Length", strconv.Itoa(len(seg.Data)))
			w.Header().Set("Access-Control-Allow-Origin", "*")
			_, _ = w.Write(seg.Data)
			return
		case <-r.Context().Done():
			slog.Info("HLS segment request cancelled by client", "segment", segmentName)
			return
		case <-ticker.C:
			if s.HasFailed() {
				http.Error(w, "ffmpeg HLS process failed or exited early", http.StatusInternalServerError)
				return
			}
			if s.IsDone() {
				select {
				case <-seg.Ready:
					if len(seg.Data) == 0 {
						http.Error(w, "Segment not available (seeked)", http.StatusNotFound)
						return
					}
					w.Header().Set("Content-Type", "video/MP2T")
					w.Header().Set("Content-Length", strconv.Itoa(len(seg.Data)))
					w.Header().Set("Access-Control-Allow-Origin", "*")
					_, _ = w.Write(seg.Data)
					return
				default:
					http.Error(w, "Segment not found and ffmpeg process completed", http.StatusNotFound)
					return
				}
			}
		case <-timeout:
			http.Error(w, "Timeout waiting for segment data from ffmpeg", http.StatusGatewayTimeout)
			return
		}
	}
}

func (h *HandlerContext) getOrCreateHLSSession(ctx context.Context, hashHex, fileIndexStr, audioParam, startParam, sessionKey string) (*hls.HLSSession, error) {
	return h.HLSSessionManager.GetOrCreate(sessionKey, func() (*hls.HLSSession, error) {
		var infoHash metainfo.Hash
		hexBytes, err := hex.DecodeString(hashHex)
		if err != nil || len(hexBytes) != 20 {
			return nil, fmt.Errorf("invalid hash: %w", err)
		}
		copy(infoHash[:], hexBytes)

		t, ok := h.Engine.Client.Torrent(infoHash)
		if !ok {
			return nil, fmt.Errorf("torrent not active")
		}

		if t.Info() == nil {
			slog.Info("HLS session waiting for torrent info...", "hash", hashHex)
			select {
			case <-t.GotInfo():
				slog.Info("HLS session: Torrent info resolved", "hash", hashHex)
			case <-ctx.Done():
				return nil, fmt.Errorf("timeout waiting for torrent info: %w", ctx.Err())
			case <-time.After(30 * time.Second):
				return nil, fmt.Errorf("timeout waiting for torrent info")
			}
		}

		fileIndex, err := strconv.Atoi(fileIndexStr)
		if err != nil || fileIndex < 1 {
			return nil, fmt.Errorf("invalid file index")
		}

		files := t.Files()
		idx := fileIndex - 1
		if idx < 0 || idx >= len(files) {
			return nil, fmt.Errorf("file index out of bounds")
		}
		file := files[idx]

		cache, ok := h.Engine.Storage.GetCache(infoHash)
		if !ok {
			return nil, fmt.Errorf("cache not found")
		}

		slog.Info("HLS Preloading starting in background...", "session", sessionKey)
		go func() {
			err = cache.Preload(t, file, h.Config.PreloadBytes)
			if err != nil {
				slog.Warn("HLS Preload failed/warn", "error", err)
			}
		}()

		// Wait for critical pieces (first piece and last piece) to be ready before starting ffmpeg
		criticalPieces := []int{int(file.Offset() / cache.PieceLen())}
		lastPieceIdx := int((file.Offset() + file.Length() - 1) / cache.PieceLen())
		if lastPieceIdx != criticalPieces[0] {
			criticalPieces = append(criticalPieces, lastPieceIdx)
		}

		slog.Info("HLS session waiting for critical pieces to download...", "session", sessionKey, "criticalPieces", criticalPieces)
		if err := cache.WaitForPieces(criticalPieces, t.Length(), 500*time.Millisecond); err != nil {
			slog.Warn("HLS session critical pieces download timeout/error", "session", sessionKey, "error", err)
		} else {
			slog.Info("HLS session critical pieces downloaded successfully", "session", sessionKey)
		}

		inputURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
		uploadURL := h.getLoopbackURL(fmt.Sprintf("/api/hls/upload/%s", sessionKey))

		session := hls.NewHLSSession(sessionKey, h.Config.HLSMaxSegments)

		startNum := 0
		if startParam != "" {
			startSecs := parseStartSecs(startParam)
			startNum = int(startSecs / float64(h.Config.HLSSegmentDuration))
		}

		err = session.StartFFmpeg(context.Background(), inputURL, audioParam, startParam, startNum, uploadURL)
		if err != nil {
			return nil, err
		}

		return session, nil
	})
}

func parseStartSecs(s string) float64 {
	if strings.Contains(s, ":") {
		parts := strings.Split(s, ":")
		if len(parts) == 3 {
			h, _ := strconv.ParseFloat(parts[0], 64)
			m, _ := strconv.ParseFloat(parts[1], 64)
			sec, _ := strconv.ParseFloat(parts[2], 64)
			return h*3600 + m*60 + sec
		}
		if len(parts) == 2 {
			m, _ := strconv.ParseFloat(parts[0], 64)
			sec, _ := strconv.ParseFloat(parts[1], 64)
			return m*60 + sec
		}
	}
	val, _ := strconv.ParseFloat(s, 64)
	return val
}

func parseSegmentIdx(name string) int {
	trimmed := strings.TrimPrefix(name, "segment_")
	trimmed = strings.TrimSuffix(trimmed, ".ts")
	idx, _ := strconv.Atoi(trimmed)
	return idx
}
