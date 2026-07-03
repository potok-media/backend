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

// HandleGetKeyframe returns the presentation timestamp of the nearest video keyframe
// at or before the requested `time`. The client uses this to remux-seek exactly onto a
// keyframe, so the requested start equals the real output start and subtitles stay in sync
// (the remux uses `-ss` input seeking which otherwise snaps to an earlier keyframe).
func (h *HandlerContext) HandleGetKeyframe(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Access-Control-Allow-Origin", "*")

	target, err := strconv.ParseFloat(r.URL.Query().Get("time"), 64)
	if err != nil || target < 0 {
		target = 0
	}

	// Nothing to resolve before the first GOP.
	if target <= 0 {
		_ = json.NewEncoder(w).Encode(map[string]float64{"keyframe": 0})
		return
	}

	kf, err := h.probeNearestKeyframe(r.Context(), hashHex, fileIndexStr, target)
	if err != nil {
		slog.Warn("keyframe probe failed", "error", err, "time", target)
		// Fall back to the requested time so the client can still seek (subtitles may drift
		// slightly for this one seek, but playback is never blocked).
		_ = json.NewEncoder(w).Encode(map[string]float64{"keyframe": target})
		return
	}

	_ = json.NewEncoder(w).Encode(map[string]float64{"keyframe": kf})
}

func (h *HandlerContext) probeNearestKeyframe(ctx context.Context, hashHex, fileIndexStr string, target float64) (float64, error) {
	if _, err := exec.LookPath("ffprobe"); err != nil {
		return 0, fmt.Errorf("ffprobe not found")
	}

	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))

	// Read a window ending at `target`, wide enough to contain at least one keyframe for
	// typical GOP sizes.
	lo := target - 20.0
	if lo < 0 {
		lo = 0
	}
	hi := target + 0.05

	probeCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()

	args := []string{
		"-v", "error",
		"-read_intervals", fmt.Sprintf("%.3f%%%.3f", lo, hi),
		"-select_streams", "v:0",
		"-show_packets",
		"-show_entries", "packet=pts_time,dts_time,flags",
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
		return 0, fmt.Errorf("ffprobe failed: %w (%s)", err, stderrBuf.String())
	}

	type ffPacket struct {
		PtsTime string `json:"pts_time"`
		DtsTime string `json:"dts_time"`
		Flags   string `json:"flags"`
	}
	var parsed struct {
		Packets []ffPacket `json:"packets"`
	}
	if err := json.Unmarshal(stdoutBuf.Bytes(), &parsed); err != nil {
		return 0, fmt.Errorf("parse ffprobe json: %w", err)
	}

	best := -1.0
	for _, p := range parsed.Packets {
		// Keyframe packets carry the "K" flag (e.g. "K__").
		if !strings.Contains(p.Flags, "K") {
			continue
		}
		ts := p.PtsTime
		if ts == "" {
			ts = p.DtsTime
		}
		pt, perr := strconv.ParseFloat(ts, 64)
		if perr != nil {
			continue
		}
		if pt <= target+0.001 && pt > best {
			best = pt
		}
	}

	if best < 0 {
		return 0, fmt.Errorf("no keyframe found in [%.3f, %.3f]", lo, hi)
	}
	return best, nil
}
