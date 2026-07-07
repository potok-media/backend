package handlers

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"Potok.Backend.TorrentGo/media"
	"Potok.Backend.TorrentGo/storage"
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
	// container keyframe index) and the stream layout now — the first in-process segment produce is then
	// probe-free. No producer to start: media/ makes segments on demand.
	go func() {
		_, _ = h.getSegList(context.Background(), hashHex, fileIndexStr)
		layout, lerr := h.getStreamLayout(context.Background(), hashHex, fileIndexStr)
		// Pre-warm the continuous AAC transcode for the DEFAULT (first) audio track if it needs transcoding, so
		// its frames are already filling by the time hls.js requests audio seg 0. Other tracks start lazily on
		// first request; AAC-source tracks need no transcoder (plain copy path).
		if lerr == nil && len(layout.audioCodecs) > 0 && layout.audioCodecs[0] != "aac" {
			_, _ = h.getAudioCont(context.Background(), hashHex, fileIndexStr, 0, nil, 0)
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
		// Detached ctx: this probe is shared via singleflight, so one client disconnecting must not fail
		// the others (probeAndCacheMetadata applies its own timeout).
		return h.probeAndCacheMetadata(context.Background(), hashHex, fileIndexStr)
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
		// Detached ctx (shared singleflight run — a caller leaving must not cancel it for others).
		return h.probeAndCacheMetadata(context.Background(), hashHex, fileIndexStr)
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

	probeCtx, cancel := context.WithTimeout(ctx, 45*time.Second)
	defer cancel()

	// In-process probe (libav, no ffprobe subprocess). Same ClassColdProbe reader that getStreamLayout uses.
	rs, _, rerr := h.openTorrentFileReader(probeCtx, hashHex, fileIndexStr, storage.ClassColdProbe)
	if rerr != nil {
		return nil, fmt.Errorf("open reader: %w", rerr)
	}
	defer rs.Close()

	probe, perr := media.ProbeTracks(probeCtx, rs)
	if perr != nil {
		return nil, fmt.Errorf("probe tracks: %w", perr)
	}

	duration := probe.DurationSec
	if duration > 0 {
		h.durationCache.Store(cacheKey, duration)
	}

	// Audio + subtitle tracks, in stream order. RelIndex is the rendition index the HLS4 master uses in the
	// EXT-X-MEDIA URI (a/{rel}/…, s/{rel}/…) and matches streamLayout.audios[rel] (both walk ProbeTracks order).
	tracks := []ClientTrack{}
	audioCounter := 0
	subCounter := 0
	for _, t := range probe.Tracks {
		switch t.Kind {
		case "audio":
			title := t.Title
			if title == "" {
				if t.Language != "" {
					title = fmt.Sprintf("Аудио (%s)", strings.ToUpper(t.Language))
				} else {
					title = fmt.Sprintf("Аудиодорожка #%d", audioCounter+1)
				}
			}
			tracks = append(tracks, ClientTrack{
				Index: t.Index, Type: "audio", Codec: t.Codec,
				Language: t.Language, Title: title, RelIndex: audioCounter,
			})
			audioCounter++
		case "subtitle":
			title := t.Title
			if title == "" {
				if t.Language != "" {
					title = fmt.Sprintf("Субтитры (%s)", strings.ToUpper(t.Language))
				} else {
					title = fmt.Sprintf("Субтитры #%d", subCounter+1)
				}
			}
			tracks = append(tracks, ClientTrack{
				Index: t.Index, Type: "subtitle", Codec: t.Codec,
				Language: t.Language, Title: title, RelIndex: subCounter,
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
