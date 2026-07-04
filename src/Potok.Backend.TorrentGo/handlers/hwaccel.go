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

// detectVideoAccel probes hardware H.264 encoders in priority order and returns the first that both
// runs on this host AND honors -force_key_frames (probeEncoder verifies the encoded output actually
// splits into keyframe-aligned fMP4 segments — a HW encoder that ignores forced keyframes misaligns
// segments and is rejected → next candidate / software libx264). Auto-enabled when a valid encoder is
// present; POTOK_DISABLE_HWACCEL forces software. Result is cached for reuse.
func detectVideoAccel(ffmpegPath string) *hwAccel {
	// Auto-enable HW: the hardened probe below is the safety net that made the old POTOK_HWACCEL opt-in
	// unnecessary (that flag is gone — a leftover POTOK_HWACCEL=1 in someone's env is simply ignored).
	// POTOK_DISABLE_HWACCEL is the escape hatch to force software.
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
			// -forced-idr 1 makes -force_key_frames emit real IDR frames at the segment grid (nvenc
			// otherwise inserts non-IDR keyframes → fMP4 segments don't tile). Verified by probeEncoder.
			encArgs: []string{"-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "24", "-forced-idr", "1"},
		})
		if dev := findRenderNode(); dev != "" {
			candidates = append(candidates, &hwAccel{
				name:       "vaapi",
				deviceArgs: []string{"-init_hw_device", "vaapi=va:" + dev, "-filter_hw_device", "va"},
				decodeArgs: []string{"-hwaccel", "vaapi", "-hwaccel_device", "va"},
				vf:         "format=nv12,hwupload",
				// -forced-idr 1: same as nvenc — h264_vaapi must emit IDR at the forced keyframe grid or
				// the fMP4 segments a reposition produces won't tile. Verified by probeEncoder.
				encArgs: []string{"-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", "24", "-forced-idr", "1"},
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

// probeEncoder runs the exact device+filter+encoder shape the real producer uses against a synthetic
// source AND verifies it honors -force_key_frames. It encodes 4s forcing a keyframe every 1s and cuts
// fMP4 HLS segments on that grid; an encoder that ignores the forcing produces one long GOP → the hls
// muxer can't split → too few segments → we reject it (that exact misalignment broke fMP4 before and
// is why HW used to be opt-in). Success means the real transcode both initializes and tiles cleanly.
func probeEncoder(ffmpegPath string, a *hwAccel) bool {
	ctx, cancel := context.WithTimeout(context.Background(), 12*time.Second)
	defer cancel()

	dir, err := os.MkdirTemp("", "potok-hwprobe-")
	if err != nil {
		slog.Debug("hwaccel probe mkdtemp failed", "backend", a.name, "err", err)
		return false
	}
	defer os.RemoveAll(dir)

	const segDur = "1"
	args := []string{"-hide_banner", "-loglevel", "error"}
	args = append(args, a.deviceArgs...)
	args = append(args, "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=15:duration=4")
	args = append(args, a.videoArgs()...)
	args = append(args,
		"-force_key_frames", "expr:gte(t,n_forced*"+segDur+")",
		"-an",
		"-f", "hls",
		"-hls_time", segDur,
		"-hls_playlist_type", "vod",
		"-hls_flags", "independent_segments+temp_file",
		"-hls_segment_type", "fmp4",
		"-hls_fmp4_init_filename", "init.mp4",
		"-hls_list_size", "0",
		"-hls_segment_filename", filepath.Join(dir, "seg%d.m4s"),
		filepath.Join(dir, "index.m3u8"),
	)

	if out, err := exec.CommandContext(ctx, ffmpegPath, args...).CombinedOutput(); err != nil {
		slog.Debug("hwaccel probe encode failed", "backend", a.name, "err", err, "out", strings.TrimSpace(string(out)))
		return false
	}

	// 4s of 1s-forced keyframes should yield ~4 segments; fewer than 3 means the encoder ignored the
	// forced keyframes (one big GOP) and would misalign real fMP4 segments — reject.
	segs, _ := filepath.Glob(filepath.Join(dir, "seg*.m4s"))
	if len(segs) < 3 {
		slog.Debug("hwaccel probe: forced keyframes not honored", "backend", a.name, "segments", len(segs))
		return false
	}
	return true
}
