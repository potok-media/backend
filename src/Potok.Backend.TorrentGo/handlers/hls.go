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
//
// Output is fMP4 (`.m4s` media + a shared `init.mp4`, referenced via #EXT-X-MAP): the codec config
// (SPS/PPS) lives out-of-band in the init segment, so a segment produced by a mid-stream reposition
// is decodable even when the source uses open GOPs (its keyframe is a non-IDR I-frame). Plain
// MPEG-TS copy dropped the parameter sets there → MSE couldn't decode → the picture froze until the
// next IDR while audio kept going. fMP4 fixes that at the source.
const (
	hlsIdleTimeout = 10 * time.Minute
	// How far ahead of the producer's head a requested segment may be before we reposition instead
	// of waiting. Larger than hls.js's forward prefetch (~maxBufferLength) so ordinary buffering
	// waits for the running muxer, and only genuine far seeks pay for a restart.
	hlsReloadAhead = 16
	hlsSegTimeout  = 45 * time.Second
	// Minimum spacing between producer restarts for one (file,audio). Guards against restart storms
	// during rapid seeking — a fresh run is given a moment to make progress before it can be torn
	// down again.
	hlsRepositionCooldown = 300 * time.Millisecond
)

var hlsSegmentRe = regexp.MustCompile(`^seg(\d+)\.m4s$`)

type hlsSession struct {
	mu             sync.Mutex
	dir            string
	cancel         context.CancelFunc
	startSeg       int
	started        bool
	gen            int // producer generation; a run's cmd-wait only self-heals if it's still current
	lastAccess     time.Time
	lastReposition time.Time
	head           atomic.Int64 // highest cached absolute segment index this run produced (startSeg-1 = none)
}

func (h *HandlerContext) HandleHls(w http.ResponseWriter, r *http.Request) {
	res := chi.URLParam(r, "res")
	switch {
	case res == "master.m3u8" || res == "index.m3u8":
		h.serveHlsPlaylist(w, r)
	case res == "init.mp4":
		h.serveHlsInit(w, r)
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
	b.WriteString("#EXTM3U\n#EXT-X-VERSION:7\n")
	fmt.Fprintf(&b, "#EXT-X-TARGETDURATION:%d\n", int(math.Ceil(maxDur)))
	b.WriteString("#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n")
	// fMP4: the shared init segment carries the codec config for every media segment.
	fmt.Fprintf(&b, "#EXT-X-MAP:URI=\"init.mp4%s\"\n", q)
	for i := 0; i < sl.count(); i++ {
		fmt.Fprintf(&b, "#EXTINF:%.6f,\nseg%d.m4s%s\n", sl.extinf(i), i, q)
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

	cacheKey := hashHex + "_" + fileIndexStr + "_" + audio + "_" + strconv.Itoa(n)

	// Serve from cache first — an already-produced segment comes straight from RAM without touching
	// the producer, so backward/repeat seeks are free and never restart ffmpeg (the old thrash).
	data, ok := h.hlsSegCache.get(cacheKey)
	if !ok {
		// Miss: ensure a producer is heading to n, then wait for the watcher to cache it.
		if h.ensureSessionCovers(r.Context(), hashHex, fileIndexStr, audio, sl, n) == nil {
			http.Error(w, "hls unavailable", http.StatusInternalServerError)
			return
		}
		var werr error
		data, werr = h.waitForCachedSegment(r.Context(), cacheKey, hlsSegTimeout)
		if werr != nil {
			if r.Context().Err() != nil {
				return
			}
			http.Error(w, "segment not ready", http.StatusGatewayTimeout)
			return
		}
	}
	w.Header().Set("Content-Type", "video/mp4")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "public, max-age=86400, immutable")
	_, _ = w.Write(data)
}

// serveHlsInit answers the fMP4 #EXT-X-MAP init segment (codec config). It's identical for every
// segment/run of one (file,audio), so it is produced by whatever session exists and cached once.
func (h *HandlerContext) serveHlsInit(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	audio := sanitizeAudioParam(r.URL.Query().Get("audio"))

	initKey := hashHex + "_" + fileIndexStr + "_" + audio + "_init"
	data, ok := h.hlsSegCache.get(initKey)
	if !ok {
		sl, err := h.getSegList(r.Context(), hashHex, fileIndexStr)
		if err != nil {
			http.Error(w, "hls unavailable", http.StatusInternalServerError)
			return
		}
		if h.ensureSessionCovers(r.Context(), hashHex, fileIndexStr, audio, sl, 0) == nil {
			http.Error(w, "hls unavailable", http.StatusInternalServerError)
			return
		}
		var werr error
		data, werr = h.waitForCachedSegment(r.Context(), initKey, hlsSegTimeout)
		if werr != nil {
			if r.Context().Err() != nil {
				return
			}
			http.Error(w, "init not ready", http.StatusGatewayTimeout)
			return
		}
	}
	w.Header().Set("Content-Type", "video/mp4")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "public, max-age=86400, immutable")
	_, _ = w.Write(data)
}

// waitForCachedSegment blocks until the watcher has moved segment `cacheKey` into the byte cache (or
// the request/timeout ends). Concurrent requests for the same segment all poll the one cache entry.
func (h *HandlerContext) waitForCachedSegment(ctx context.Context, cacheKey string, timeout time.Duration) ([]byte, error) {
	deadline := time.Now().Add(timeout)
	for {
		if data, ok := h.hlsSegCache.get(cacheKey); ok {
			return data, nil
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("segment timeout")
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(100 * time.Millisecond):
		}
	}
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

	// Restart storm guard: if a run was just (re)started, don't tear it down again immediately. When
	// n is still reachable by the fresh run (n >= startSeg) let the caller wait for it; only a real
	// backward jump the run can't serve falls through to restart.
	if s.started && n >= s.startSeg && time.Since(s.lastReposition) < hlsRepositionCooldown {
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
	cmd, err := h.startHlsProducer(sctx, hashHex, fileIndexStr, audio, sl, n, dir)
	if err != nil {
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
	s.gen++
	s.lastReposition = time.Now()
	// State is fully set under s.mu before these goroutines run; waitProducer/watchHlsHead can only
	// lock s.mu after we unlock, so they always see this run's committed generation.
	go h.watchHlsHead(sctx, s, dir, key, n)
	go h.waitProducer(sctx, s, s.gen, dir, key, cmd)
	slog.Info("hls producer started", "key", key, "startSeg", n, "transcode", sl.transcode)
	return s
}

// waitProducer waits for one ffmpeg run to exit. If it dies on its own (crash/EOF) while it is still
// the session's current run, mark the session unstarted so the next request restarts it, and free
// this run's temp dir (its completed segments are already in the byte cache). A stale run (session
// was repositioned/torn down) only cleans its own dir.
func (h *HandlerContext) waitProducer(sctx context.Context, s *hlsSession, gen int, dir, keyPrefix string, cmd *exec.Cmd) {
	err := cmd.Wait()
	// Cache any completed segments the watcher hasn't drained yet (e.g. the last one at EOF) before
	// removing the dir, so the tail of the file doesn't have to be re-produced on demand.
	h.drainSegments(dir, keyPrefix)
	s.mu.Lock()
	current := s.gen == gen
	if current {
		s.started = false
		if s.cancel != nil {
			s.cancel() // stop this run's watchHlsHead goroutine (sctx isn't cancelled at natural EOF)
		}
	}
	s.mu.Unlock()
	_ = os.RemoveAll(dir) // idempotent: reposition/reap may have removed it already
	if current && sctx.Err() == nil {
		slog.Warn("hls producer exited unexpectedly (will restart on next request)", "err", err)
	}
}

// drainSegments caches every completed segment (and the init) currently in dir that isn't cached yet.
func (h *HandlerContext) drainSegments(dir, keyPrefix string) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	for _, e := range entries {
		m := hlsSegmentRe.FindStringSubmatch(e.Name())
		if m == nil {
			continue
		}
		key := keyPrefix + "_" + m[1]
		if _, ok := h.hlsSegCache.get(key); ok {
			continue
		}
		if data, rerr := os.ReadFile(filepath.Join(dir, e.Name())); rerr == nil && len(data) > 0 {
			h.hlsSegCache.put(key, data)
		}
	}
	initKey := keyPrefix + "_init"
	if _, ok := h.hlsSegCache.get(initKey); !ok {
		if data, rerr := os.ReadFile(filepath.Join(dir, "init.mp4")); rerr == nil && len(data) > 0 {
			h.hlsSegCache.put(initKey, data)
		}
	}
}

// startHlsProducer launches the ffmpeg muxer for one contiguous run beginning at segment startSeg,
// returning the running command so the caller can watch for its exit (see waitProducer).
func (h *HandlerContext) startHlsProducer(sctx context.Context, hashHex, fileIndexStr, audio string, sl *segList, startSeg int, dir string) (*exec.Cmd, error) {
	if _, err := exec.LookPath(h.ffmpegPath); err != nil {
		return nil, fmt.Errorf("ffmpeg not found")
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
	if sl.transcode {
		args = append(args, h.videoAccel.inputArgs()...)
	}
	if srcStart > 0.001 {
		args = append(args, "-ss", fmt.Sprintf("%.3f", srcStart))
	}
	args = append(args, "-i", streamURL, "-map", "0:v:0", "-map", audioMap)

	segDur := fmt.Sprintf("%g", hlsSegmentSeconds)
	if sl.transcode {
		// Force keyframes on the segment grid so real cuts match the uniform playlist, and shift the
		// timeline back to the absolute source position so runs stay on one timeline. Video codec is
		// the probed hardware encoder (or software x264 fallback).
		args = append(args, h.videoAccel.videoArgs()...)
		args = append(args,
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
		"-hls_segment_type", "fmp4",
		"-hls_fmp4_init_filename", "init.mp4",
		"-hls_list_size", "0",
		"-start_number", strconv.Itoa(startSeg),
		"-hls_segment_filename", filepath.Join(dir, "seg%d.m4s"),
		filepath.Join(dir, "index.m3u8"),
	)

	cmd := exec.CommandContext(sctx, h.ffmpegPath, args...)
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	return cmd, nil
}

// watchHlsHead moves each completed segment file into the byte cache and deletes the file, then
// advances s.head. Serving reads only from the cache, so a segment survives the producer being
// killed/repositioned, and disk holds only the in-progress segment. The `temp_file` hls flag makes
// segN.m4s appear atomically (complete) — safe to read+delete.
func (h *HandlerContext) watchHlsHead(ctx context.Context, s *hlsSession, dir, keyPrefix string, startSeg int) {
	next := startSeg
	initCached := false
	initKey := keyPrefix + "_init"
	t := time.NewTicker(150 * time.Millisecond)
	defer t.Stop()
	for {
		// The fMP4 init segment (codec config) is written once at start and is identical across
		// runs of this (file,audio); cache it once so #EXT-X-MAP can be answered from any run.
		if !initCached {
			if _, ok := h.hlsSegCache.get(initKey); ok {
				initCached = true
			} else if data, err := os.ReadFile(filepath.Join(dir, "init.mp4")); err == nil && len(data) > 0 {
				h.hlsSegCache.put(initKey, data)
				initCached = true
			}
		}
		for {
			path := filepath.Join(dir, fmt.Sprintf("seg%d.m4s", next))
			fi, err := os.Stat(path)
			if err != nil || fi.Size() == 0 {
				break
			}
			data, rerr := os.ReadFile(path)
			if rerr != nil || len(data) == 0 {
				break
			}
			h.hlsSegCache.put(keyPrefix+"_"+strconv.Itoa(next), data)
			_ = os.Remove(path)
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
	if _, err := exec.LookPath(h.ffprobePath); err != nil {
		return ""
	}
	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
	args := []string{"-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0"}
	if strings.HasPrefix(probeURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args, probeURL)
	out, err := exec.CommandContext(ctx, h.ffprobePath, args...).Output()
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
	if _, err := exec.LookPath(h.ffprobePath); err != nil {
		return 0
	}
	probeURL := h.getLoopbackURL(fmt.Sprintf("/api/torrents/%s/files/%s/stream?raw=true", hashHex, fileIndexStr))
	args := []string{"-v", "error", "-select_streams", "v:0",
		"-show_entries", "packet=pts_time", "-read_intervals", "%+#1", "-of", "csv=p=0"}
	if strings.HasPrefix(probeURL, "https://") {
		args = append(args, "-tls_verify", "0")
	}
	args = append(args, probeURL)
	out, err := exec.CommandContext(ctx, h.ffprobePath, args...).Output()
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
	// Drop all per-file caches for this torrent (segmentation, codec, PTS offset, segment bytes).
	for _, m := range []*sync.Map{&h.hlsSegList, &h.hlsVideoCodec, &h.hlsVideoStartPTS} {
		m.Range(func(k, _ interface{}) bool {
			if key, _ := k.(string); strings.HasPrefix(key, prefix) {
				m.Delete(k)
			}
			return true
		})
	}
	h.hlsSegCache.purgePrefix(prefix)
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
			// Reap by idleness regardless of `started`, so failed-start (started=false) entries and
			// self-crashed producers are cleaned up too, not just live ones.
			idle := s.lastAccess.Before(cutoff)
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

