package handlers

import (
	"context"
	"fmt"
	"log/slog"
	"math"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/go-chi/chi/v5"
)

// HLS delivery, Jellyfin-style: the whole file is described up-front by a static VOD playlist whose
// per-segment EXTINF comes from the container keyframe index (see hlsindex.go). Because the playlist
// is VOD (#EXT-X-ENDLIST), hls.js pipelines segments far ahead off buffer state with no manifest
// reloads — so there is no segment-boundary stall — and native seeking works everywhere.
//
// Segments are produced on demand by a single repositionable `ffmpeg -f hls` muxer (the only thing
// that cuts cleanly at keyframes). It runs with `-copyts -muxdelay 0` so every segment carries its
// true source PTS regardless of which run produced it, and `-start_number N` so segment files are
// named by their absolute index. When a request lands far outside the running muxer's window, the
// muxer is repositioned to that segment's keyframe.
const (
	hlsIdleTimeout = 10 * time.Minute
	// How far ahead of the producer's head a requested segment may be before we reposition instead
	// of waiting. Larger than hls.js's forward prefetch (~maxBufferLength) so ordinary buffering
	// waits for the running muxer, and only genuine far seeks pay for a restart.
	hlsReloadAhead = 16
	hlsSegTimeout  = 45 * time.Second
)

var hlsSegmentRe = regexp.MustCompile(`^seg(\d+)\.ts$`)

type hlsSession struct {
	mu         sync.Mutex
	dir        string
	cancel     context.CancelFunc
	startSeg   int
	started    bool
	lastAccess time.Time
	head       atomic.Int64 // highest contiguous produced absolute segment index (startSeg-1 = none)
}

func (h *HandlerContext) HandleHls(w http.ResponseWriter, r *http.Request) {
	res := chi.URLParam(r, "res")
	switch {
	case res == "master.m3u8" || res == "index.m3u8":
		h.serveHlsPlaylist(w, r)
	default:
		if m := hlsSegmentRe.FindStringSubmatch(res); m != nil {
			n, _ := strconv.Atoi(m[1])
			h.serveHlsSegment(w, r, n)
			return
		}
		http.NotFound(w, r)
	}
}

func (h *HandlerContext) serveHlsPlaylist(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	audio := sanitizeAudioParam(r.URL.Query().Get("audio"))

	sl, err := h.getSegList(r.Context(), hashHex, fileIndexStr)
	if err != nil {
		slog.Error("hls seglist failed", "hash", hashHex, "file", fileIndexStr, "error", err)
		http.Error(w, "hls unavailable", http.StatusInternalServerError)
		return
	}

	// Warm a producer from the start so the first segments are being made by the time hls.js asks.
	go h.ensureSessionCovers(context.Background(), hashHex, fileIndexStr, audio, sl, 0)

	body := renderVodPlaylist(sl, audio)
	w.Header().Set("Content-Type", "application/vnd.apple.mpegurl")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "public, max-age=3600")
	_, _ = w.Write(body)
}

func renderVodPlaylist(sl *segList, audio string) []byte {
	q := ""
	if audio != "" {
		q = "?audio=" + audio
	}
	maxDur := 0.0
	for i := 0; i < sl.count(); i++ {
		if d := sl.extinf(i); d > maxDur {
			maxDur = d
		}
	}
	var b strings.Builder
	b.WriteString("#EXTM3U\n#EXT-X-VERSION:3\n")
	fmt.Fprintf(&b, "#EXT-X-TARGETDURATION:%d\n", int(math.Ceil(maxDur)))
	b.WriteString("#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n")
	for i := 0; i < sl.count(); i++ {
		fmt.Fprintf(&b, "#EXTINF:%.6f,\nseg%d.ts%s\n", sl.extinf(i), i, q)
	}
	b.WriteString("#EXT-X-ENDLIST\n")
	return []byte(b.String())
}

func (h *HandlerContext) serveHlsSegment(w http.ResponseWriter, r *http.Request, n int) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	audio := sanitizeAudioParam(r.URL.Query().Get("audio"))

	sl, err := h.getSegList(r.Context(), hashHex, fileIndexStr)
	if err != nil || n < 0 || n >= sl.count() {
		http.NotFound(w, r)
		return
	}

	s := h.ensureSessionCovers(r.Context(), hashHex, fileIndexStr, audio, sl, n)
	if s == nil {
		http.Error(w, "hls unavailable", http.StatusInternalServerError)
		return
	}
	s.mu.Lock()
	dir := s.dir
	s.mu.Unlock()

	data, err := waitForSegment(r.Context(), filepath.Join(dir, fmt.Sprintf("seg%d.ts", n)), hlsSegTimeout)
	if err != nil {
		if r.Context().Err() != nil {
			return
		}
		http.Error(w, "segment not ready", http.StatusGatewayTimeout)
		return
	}
	w.Header().Set("Content-Type", "video/mp2t")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "public, max-age=86400, immutable")
	_, _ = w.Write(data)
}

// ensureSessionCovers guarantees a producer is running that will emit segment n, repositioning it
// when n falls before the current run or too far beyond its head. One producer per (file,audio).
func (h *HandlerContext) ensureSessionCovers(ctx context.Context, hashHex, fileIndexStr, audio string, sl *segList, n int) *hlsSession {
	h.hlsReaperOnce.Do(func() { go h.reapHlsSessions() })

	key := hashHex + "_" + fileIndexStr + "_" + audio
	v, _ := h.hlsSessions.LoadOrStore(key, &hlsSession{})
	s := v.(*hlsSession)

	s.mu.Lock()
	defer s.mu.Unlock()
	s.lastAccess = time.Now()

	if s.started && n >= s.startSeg && n <= int(s.head.Load())+hlsReloadAhead {
		return s
	}

	// (Re)start the producer at segment n.
	if s.cancel != nil {
		s.cancel()
	}
	if s.dir != "" {
		_ = os.RemoveAll(s.dir)
	}
	dir, err := os.MkdirTemp("", fmt.Sprintf("potok-hls-%s-%s-%s-", hashHex, fileIndexStr, audio))
	if err != nil {
		slog.Error("hls mkdtemp failed", "error", err)
		s.started = false
		return nil
	}
	sctx, cancel := context.WithCancel(context.Background())
	if err := h.startHlsProducer(sctx, hashHex, fileIndexStr, audio, sl, n, dir); err != nil {
		cancel()
		_ = os.RemoveAll(dir)
		s.started = false
		slog.Error("hls producer start failed", "key", key, "seg", n, "error", err)
		return nil
	}
	s.dir = dir
	s.cancel = cancel
	s.startSeg = n
	s.head.Store(int64(n - 1))
	s.started = true
	go watchHlsHead(sctx, s, dir, n)
	slog.Info("hls producer started", "key", key, "startSeg", n, "transcode", sl.transcode)
	return s
}

// startHlsProducer launches the ffmpeg muxer for one contiguous run beginning at segment startSeg.
func (h *HandlerContext) startHlsProducer(sctx context.Context, hashHex, fileIndexStr, audio string, sl *segList, startSeg int, dir string) error {
	if _, err := exec.LookPath("ffmpeg"); err != nil {
		return fmt.Errorf("ffmpeg not found in PATH")
	}
	streamURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
	srcStart := sl.srcStart(startSeg)

	audioMap := "0:a:0?"
	if audio != "" && audio != "0" {
		audioMap = "0:" + audio
	}

	args := []string{"-nostdin", "-hide_banner", "-loglevel", "error"}
	if strings.HasPrefix(streamURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	if srcStart > 0.001 {
		args = append(args, "-ss", fmt.Sprintf("%.3f", srcStart))
	}
	args = append(args, "-i", streamURL, "-map", "0:v:0", "-map", audioMap)

	segDur := fmt.Sprintf("%g", hlsSegmentSeconds)
	if sl.transcode {
		// No source keyframe index (or non-H.264): transcode and force keyframes on the segment
		// grid so real cuts match the uniform playlist. Normalize to 0 then shift the timeline back
		// to the absolute source position (output_ts_offset) so runs stay on one timeline.
		args = append(args,
			"-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-pix_fmt", "yuv420p",
			"-force_key_frames", "expr:gte(t,n_forced*"+segDur+")",
			"-output_ts_offset", fmt.Sprintf("%.3f", srcStart),
		)
	} else {
		// H.264 direct copy; -copyts preserves absolute source PTS so segments tile across runs.
		args = append(args, "-c:v", "copy", "-copyts")
	}
	args = append(args,
		"-c:a", "aac", "-ac", "2", "-af", "aresample=async=1",
		"-muxdelay", "0",
		"-f", "hls",
		"-hls_time", segDur,
		"-hls_playlist_type", "vod",
		"-hls_flags", "independent_segments+temp_file",
		"-hls_segment_type", "mpegts",
		"-hls_list_size", "0",
		"-start_number", strconv.Itoa(startSeg),
		"-hls_segment_filename", filepath.Join(dir, "seg%d.ts"),
		filepath.Join(dir, "index.m3u8"),
	)

	cmd := exec.CommandContext(sctx, "ffmpeg", args...)
	if err := cmd.Start(); err != nil {
		return err
	}
	go func() { _ = cmd.Wait() }()
	return nil
}

// watchHlsHead advances s.head as contiguous segment files appear, so ensureSessionCovers can tell
// how far the producer has reached without rescanning on every request.
func watchHlsHead(ctx context.Context, s *hlsSession, dir string, startSeg int) {
	next := startSeg
	t := time.NewTicker(150 * time.Millisecond)
	defer t.Stop()
	for {
		for {
			fi, err := os.Stat(filepath.Join(dir, fmt.Sprintf("seg%d.ts", next)))
			if err != nil || fi.Size() == 0 {
				break
			}
			s.head.Store(int64(next))
			next++
		}
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
	}
}

// probeVideoCodec returns the file's first video-stream codec (cached), e.g. "h264"/"hevc".
func (h *HandlerContext) probeVideoCodec(ctx context.Context, hashHex, fileIndexStr string) string {
	key := hashHex + "_" + fileIndexStr
	if v, ok := h.hlsVideoCodec.Load(key); ok {
		return v.(string)
	}
	if _, err := exec.LookPath("ffprobe"); err != nil {
		return ""
	}
	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
	args := []string{"-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0"}
	if strings.HasPrefix(probeURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args, probeURL)
	out, err := exec.CommandContext(ctx, "ffprobe", args...).Output()
	if err != nil {
		return ""
	}
	codec := strings.TrimSpace(string(out))
	if codec != "" {
		h.hlsVideoCodec.Store(key, codec)
	}
	return codec
}

// videoStartPTS returns the file's first video-packet PTS (source timestamp offset), cached. Used
// as the base for the uniform (transcode) segmentation when there is no container keyframe index.
func (h *HandlerContext) videoStartPTS(ctx context.Context, hashHex, fileIndexStr string) float64 {
	key := hashHex + "_" + fileIndexStr
	if v, ok := h.hlsVideoStartPTS.Load(key); ok {
		return v.(float64)
	}
	if _, err := exec.LookPath("ffprobe"); err != nil {
		return 0
	}
	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
	args := []string{"-v", "error", "-select_streams", "v:0",
		"-show_entries", "packet=pts_time", "-read_intervals", "%+#1", "-of", "csv=p=0"}
	if strings.HasPrefix(probeURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args, probeURL)
	out, err := exec.CommandContext(ctx, "ffprobe", args...).Output()
	if err != nil {
		return 0
	}
	fields := strings.Fields(string(out))
	if len(fields) == 0 {
		return 0
	}
	off, _ := strconv.ParseFloat(strings.Trim(fields[0], ","), 64)
	if off < 0 {
		off = 0
	}
	h.hlsVideoStartPTS.Store(key, off)
	return off
}

func (h *HandlerContext) purgeHlsSessions(hashHex string) {
	prefix := hashHex + "_"
	h.hlsSessions.Range(func(k, v interface{}) bool {
		if key, _ := k.(string); strings.HasPrefix(key, prefix) {
			if s, ok := v.(*hlsSession); ok {
				s.mu.Lock()
				if s.cancel != nil {
					s.cancel()
				}
				if s.dir != "" {
					_ = os.RemoveAll(s.dir)
				}
				s.started = false
				s.mu.Unlock()
			}
			h.hlsSessions.Delete(k)
		}
		return true
	})
	// Drop all per-file caches for this torrent (segmentation, codec, PTS offset).
	for _, m := range []*sync.Map{&h.hlsSegList, &h.hlsVideoCodec, &h.hlsVideoStartPTS} {
		m.Range(func(k, _ interface{}) bool {
			if key, _ := k.(string); strings.HasPrefix(key, prefix) {
				m.Delete(k)
			}
			return true
		})
	}
}

func (h *HandlerContext) reapHlsSessions() {
	for {
		time.Sleep(time.Minute)
		cutoff := time.Now().Add(-hlsIdleTimeout)
		h.hlsSessions.Range(func(k, v interface{}) bool {
			s, ok := v.(*hlsSession)
			if !ok {
				return true
			}
			s.mu.Lock()
			idle := s.started && s.lastAccess.Before(cutoff)
			if idle {
				if s.cancel != nil {
					s.cancel()
				}
				if s.dir != "" {
					_ = os.RemoveAll(s.dir)
				}
				s.started = false
			}
			s.mu.Unlock()
			if idle {
				h.hlsSessions.Delete(k)
				slog.Info("hls session reaped (idle)", "key", k)
			}
			return true
		})
	}
}

func sanitizeAudioParam(a string) string {
	if a == "" || a == "default" {
		return ""
	}
	for _, c := range a {
		if c < '0' || c > '9' {
			return ""
		}
	}
	return a
}

func waitForSegment(ctx context.Context, path string, timeout time.Duration) ([]byte, error) {
	deadline := time.Now().Add(timeout)
	for {
		if fi, err := os.Stat(path); err == nil && fi.Size() > 0 {
			return os.ReadFile(path)
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("segment timeout")
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(120 * time.Millisecond):
		}
	}
}
