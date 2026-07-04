package handlers

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"os/exec"
	"strconv"
	"strings"
	"time"

	"github.com/go-chi/chi/v5"
)

type ClientTrack struct {
	Index    int    `json:"index"`
	Type     string `json:"type"`
	Codec    string `json:"codec"`
	Language string `json:"language"`
	Title    string `json:"title"`
	RelIndex int    `json:"relIndex"`
}

type ClientMetadata struct {
	Success    bool          `json:"success"`
	Duration   float64       `json:"duration"`
	Tracks     []ClientTrack `json:"tracks"`
	IntroStart float64       `json:"introStart"`
	IntroEnd   float64       `json:"introEnd"`
	OutroStart float64       `json:"outroStart"`
	OutroEnd   float64       `json:"outroEnd"`
}

func (h *HandlerContext) HandleGetMediaMetadata(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	// Pre-warm HLS: the player fetches metadata as it opens, so build the segmentation (parses the
	// container keyframe index) and start the producer from seg0 now — the first segments are being
	// made by the time hls.js requests the playlist.
	go func() {
		if sl, err := h.getSegList(context.Background(), hashHex, fileIndexStr); err == nil {
			h.ensureSessionCovers(context.Background(), hashHex, fileIndexStr, "", sl, 0)
		}
	}()

	cacheKey := fmt.Sprintf("%s_%s", hashHex, fileIndexStr)
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Access-Control-Allow-Origin", "*")

	if val, ok := h.metadataCache.Load(cacheKey); ok {
		slog.Debug("Serving metadata from RAM cache", "key", cacheKey)
		w.Write(val.([]byte))
		return
	}

	responseVal, err, _ := h.metadataSFG.Do(cacheKey, func() (interface{}, error) {
		return h.probeAndCacheMetadata(r.Context(), hashHex, fileIndexStr)
	})

	if err != nil {
		slog.Error("Probing metadata failed", "error", err)
		http.Error(w, "Probing failed: "+err.Error(), http.StatusInternalServerError)
		return
	}

	w.Write(responseVal.([]byte))
}

func (h *HandlerContext) getOrProbeDuration(ctx context.Context, hashHex, fileIndexStr string) (float64, error) {
	cacheKey := fmt.Sprintf("%s_%s", hashHex, fileIndexStr)
	if val, ok := h.durationCache.Load(cacheKey); ok {
		return val.(float64), nil
	}

	_, err, _ := h.metadataSFG.Do(cacheKey, func() (interface{}, error) {
		if val, ok := h.durationCache.Load(cacheKey); ok {
			return val.(float64), nil
		}
		return h.probeAndCacheMetadata(ctx, hashHex, fileIndexStr)
	})

	if err != nil {
		return 0, err
	}

	if val, ok := h.durationCache.Load(cacheKey); ok {
		return val.(float64), nil
	}
	return 0, fmt.Errorf("duration not found after probe")
}

func (h *HandlerContext) probeAndCacheMetadata(ctx context.Context, hashHex, fileIndexStr string) ([]byte, error) {
	cacheKey := fmt.Sprintf("%s_%s", hashHex, fileIndexStr)

	// Double check cache
	if val, ok := h.metadataCache.Load(cacheKey); ok {
		return val.([]byte), nil
	}

	if _, err := exec.LookPath("ffprobe"); err != nil {
		return nil, fmt.Errorf("ffprobe not found")
	}

	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))

	probeCtx, cancel := context.WithTimeout(ctx, 45*time.Second)
	defer cancel()

	args := []string{
		"-v", "error",
		"-show_entries", "format=duration",
		"-show_entries", "stream=index,codec_type,codec_name:stream_tags=language,title",
		"-of", "json",
	}
	if strings.HasPrefix(probeURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args, probeURL)
	cmd := exec.CommandContext(probeCtx, "ffprobe", args...)

	var stdoutBuf, stderrBuf bytes.Buffer
	cmd.Stdout = &stdoutBuf
	cmd.Stderr = &stderrBuf

	if err := cmd.Run(); err != nil {
		slog.Error("ffprobe failed", "error", err, "stderr", stderrBuf.String())
		return nil, fmt.Errorf("ffprobe failed: %w", err)
	}

	type FFProbeStream struct {
		Index     int               `json:"index"`
		CodecName string            `json:"codec_name"`
		CodecType string            `json:"codec_type"`
		Tags      map[string]string `json:"tags"`
	}

	type FFProbeResult struct {
		Streams []FFProbeStream `json:"streams"`
		Format  *struct {
			Duration string `json:"duration"`
		} `json:"format"`
	}

	var ffResult FFProbeResult
	if err := json.Unmarshal(stdoutBuf.Bytes(), &ffResult); err != nil {
		return nil, fmt.Errorf("failed to parse probe data: %w", err)
	}

	var duration float64
	if ffResult.Format != nil {
		duration, _ = strconv.ParseFloat(ffResult.Format.Duration, 64)
	}
	if duration > 0 {
		h.durationCache.Store(cacheKey, duration)
	}

	tracks := []ClientTrack{}
	audioCounter := 0
	subCounter := 0

	for _, s := range ffResult.Streams {
		if s.CodecType == "audio" {
			title := ""
			lang := ""
			if s.Tags != nil {
				title = s.Tags["title"]
				lang = s.Tags["language"]
			}
			if title == "" {
				if lang != "" {
					title = fmt.Sprintf("Аудио (%s)", strings.ToUpper(lang))
				} else {
					title = fmt.Sprintf("Аудиодорожка #%d", audioCounter+1)
				}
			}
			tracks = append(tracks, ClientTrack{
				Index:    s.Index,
				Type:     "audio",
				Codec:    s.CodecName,
				Language: lang,
				Title:    title,
				RelIndex: audioCounter,
			})
			audioCounter++
		} else if s.CodecType == "subtitle" {
			title := ""
			lang := ""
			if s.Tags != nil {
				title = s.Tags["title"]
				lang = s.Tags["language"]
			}
			if title == "" {
				if lang != "" {
					title = fmt.Sprintf("Субтитры (%s)", strings.ToUpper(lang))
				} else {
					title = fmt.Sprintf("Субтитры #%d", subCounter+1)
				}
			}
			tracks = append(tracks, ClientTrack{
				Index:    s.Index,
				Type:     "subtitle",
				Codec:    s.CodecName,
				Language: lang,
				Title:    title,
				RelIndex: subCounter,
			})
			subCounter++
		}
	}

	introStart := 0.0
	introEnd := 0.0
	outroStart := 0.0
	outroEnd := 0.0

	if val, ok := h.timecodeCache.Load(hashHex); ok {
		if rangesMap, ok := val.(map[string]*TimecodeRange); ok {
			if r, ok := rangesMap[fileIndexStr]; ok {
				introStart = r.IntroStart
				introEnd = r.IntroEnd
				outroStart = r.OutroStart
				outroEnd = r.OutroEnd
			}
		}
	}

	metaResponse := ClientMetadata{
		Success:    true,
		Duration:   duration,
		Tracks:     tracks,
		IntroStart: introStart,
		IntroEnd:   introEnd,
		OutroStart: outroStart,
		OutroEnd:   outroEnd,
	}

	responseBytes, err := json.Marshal(metaResponse)
	if err != nil {
		return nil, fmt.Errorf("failed to marshal metadata response: %w", err)
	}

	h.metadataCache.Store(cacheKey, responseBytes)
	return responseBytes, nil
}
