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

	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))

	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Access-Control-Allow-Origin", "*")

	if _, err := exec.LookPath("ffprobe"); err != nil {
		slog.Warn("ffprobe not found in PATH")
		_ = json.NewEncoder(w).Encode(ClientMetadata{Success: false})
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 45*time.Second)
	defer cancel()

	cmd := exec.CommandContext(ctx, "ffprobe",
		"-v", "error",
		"-show_entries", "format=duration",
		"-show_entries", "stream=index,codec_type,codec_name:stream_tags=language,title",
		"-of", "json",
		probeURL,
	)

	var stdoutBuf, stderrBuf bytes.Buffer
	cmd.Stdout = &stdoutBuf
	cmd.Stderr = &stderrBuf

	if err := cmd.Run(); err != nil {
		slog.Error("ffprobe failed", "error", err, "stderr", stderrBuf.String())
		http.Error(w, fmt.Sprintf("Probing failed: %v", err), http.StatusGatewayTimeout)
		return
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
		http.Error(w, "Failed to parse probe data: "+err.Error(), http.StatusInternalServerError)
		return
	}

	var duration float64
	if ffResult.Format != nil {
		duration, _ = strconv.ParseFloat(ffResult.Format.Duration, 64)
	}
	if duration > 0 {
		cacheKey := fmt.Sprintf("%s_%s", hashHex, fileIndexStr)
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

	_ = json.NewEncoder(w).Encode(metaResponse)
}

func (h *HandlerContext) getOrProbeDuration(ctx context.Context, hashHex, fileIndexStr string) (float64, error) {
	cacheKey := fmt.Sprintf("%s_%s", hashHex, fileIndexStr)
	if val, ok := h.durationCache.Load(cacheKey); ok {
		return val.(float64), nil
	}

	if _, err := exec.LookPath("ffprobe"); err != nil {
		return 0, fmt.Errorf("ffprobe not found")
	}

	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))

	probeCtx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()

	cmd := exec.CommandContext(probeCtx, "ffprobe",
		"-v", "error",
		"-show_entries", "format=duration",
		"-of", "default=noprint_wrappers=1:nokey=1",
		probeURL,
	)

	var stdoutBuf bytes.Buffer
	cmd.Stdout = &stdoutBuf

	if err := cmd.Run(); err != nil {
		return 0, err
	}

	trimmed := strings.TrimSpace(stdoutBuf.String())
	duration, err := strconv.ParseFloat(trimmed, 64)
	if err != nil {
		return 0, err
	}

	if duration > 0 {
		h.durationCache.Store(cacheKey, duration)
	}
	return duration, nil
}
