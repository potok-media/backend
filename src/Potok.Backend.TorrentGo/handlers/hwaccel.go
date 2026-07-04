package handlers

import (
	"context"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

// resolveFFmpegBinaries picks the ffmpeg/ffprobe binaries: prefer $FFMPEG_PATH (the jellyfin-ffmpeg
// bundle baked into the container image), otherwise whatever is on PATH (system ffmpeg on a dev box).
func resolveFFmpegBinaries() (ffmpeg, ffprobe string) {
	ffmpeg, ffprobe = "ffmpeg", "ffprobe"
	if p := os.Getenv("FFMPEG_PATH"); p != "" {
		if _, err := os.Stat(p); err == nil {
			ffmpeg = p
			if pr := filepath.Join(filepath.Dir(p), "ffprobe"); fileExists(pr) {
				ffprobe = pr
			}
		}
	}
	if lp, err := exec.LookPath(ffmpeg); err == nil {
		ffmpeg = lp
	}
	if lp, err := exec.LookPath(ffprobe); err == nil {
		ffprobe = lp
	}
	return
}

func fileExists(p string) bool {
	_, err := os.Stat(p)
	return err == nil
}

// hwAccel describes a transcode backend: the device/decode flags placed before -i and the filter +
// encoder placed after. Chosen once at startup by probing a real encode; software (libx264) is the
// always-available fallback.
type hwAccel struct {
	name       string
	deviceArgs []string // -init_hw_device / -filter_hw_device — needed by both the decode and the encode filter
	decodeArgs []string // -hwaccel … — HW decode (forgiving: falls back to software decode per-codec)
	vf         string   // filter chain that normalizes frames to the encoder's input
	encArgs    []string // -c:v <encoder> + rate control
}

// inputArgs returns the flags to place before -i (device init + HW decode). Empty for software.
func (a *hwAccel) inputArgs() []string {
	if a == nil {
		return nil
	}
	out := append([]string{}, a.deviceArgs...)
	return append(out, a.decodeArgs...)
}

// videoArgs returns the video-encode portion (filter + encoder + tuning) to place after -i.
func (a *hwAccel) videoArgs() []string {
	if a == nil {
		return []string{"-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-pix_fmt", "yuv420p"}
	}
	out := []string{}
	if a.vf != "" {
		out = append(out, "-vf", a.vf)
	}
	return append(out, a.encArgs...)
}

// detectVideoAccel probes hardware H.264 encoders in priority order and returns the first that
// actually runs a tiny encode on this host. Returns nil (→ software libx264) when none work or when
// POTOK_DISABLE_HWACCEL is set. Result is computed once and reused for every transcode.
func detectVideoAccel(ffmpegPath string) *hwAccel {
	if os.Getenv("POTOK_DISABLE_HWACCEL") != "" {
		slog.Info("hwaccel disabled via POTOK_DISABLE_HWACCEL, using software x264")
		return nil
	}

	var candidates []*hwAccel
	if runtime.GOOS == "darwin" {
		candidates = []*hwAccel{{
			name:       "videotoolbox",
			decodeArgs: []string{"-hwaccel", "videotoolbox"},
			encArgs:    []string{"-c:v", "h264_videotoolbox", "-pix_fmt", "yuv420p"},
		}}
	} else {
		candidates = append(candidates, &hwAccel{
			name:       "nvenc",
			decodeArgs: []string{"-hwaccel", "cuda"},
			vf:         "format=yuv420p",
			encArgs:    []string{"-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "24"},
		})
		if dev := findRenderNode(); dev != "" {
			candidates = append(candidates, &hwAccel{
				name:       "vaapi",
				deviceArgs: []string{"-init_hw_device", "vaapi=va:" + dev, "-filter_hw_device", "va"},
				decodeArgs: []string{"-hwaccel", "vaapi", "-hwaccel_device", "va"},
				vf:         "format=nv12,hwupload",
				encArgs:    []string{"-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", "24"},
			})
		}
	}

	for _, c := range candidates {
		if probeEncoder(ffmpegPath, c) {
			slog.Info("hwaccel selected", "backend", c.name)
			return c
		}
		slog.Info("hwaccel candidate unavailable", "backend", c.name)
	}
	slog.Info("no hardware encoder available, using software x264")
	return nil
}

// findRenderNode returns the first /dev/dri/renderD* node, or "" if the GPU wasn't passed into the
// container (the default — nothing to accelerate against).
func findRenderNode() string {
	matches, _ := filepath.Glob("/dev/dri/renderD*")
	for _, m := range matches {
		if fileExists(m) {
			return m
		}
	}
	return ""
}

// probeEncoder runs the exact device+filter+encoder shape against a synthetic source; success means
// the real transcode will initialize on this host.
func probeEncoder(ffmpegPath string, a *hwAccel) bool {
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()

	args := []string{"-hide_banner", "-loglevel", "error"}
	args = append(args, a.deviceArgs...)
	args = append(args, "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10:duration=0.3")
	args = append(args, a.videoArgs()...)
	args = append(args, "-f", "null", "-")

	out, err := exec.CommandContext(ctx, ffmpegPath, args...).CombinedOutput()
	if err != nil {
		slog.Debug("hwaccel probe failed", "backend", a.name, "err", err, "out", strings.TrimSpace(string(out)))
		return false
	}
	return true
}
