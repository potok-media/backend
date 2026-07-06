package handlers

import (
	"context"
	"fmt"
	"time"

	"Potok.Backend.TorrentGo/media"
	"Potok.Backend.TorrentGo/storage"
)

// In-process HLS production (TorrentGo v2, multivariant "HLS4"). Each rendition is produced INDEPENDENTLY
// and single-stream: the VIDEO rendition (audio-agnostic, produced once and shared) and one AUDIO rendition
// per source track (produced lazily — only the track the client actually loads). Because video and audio
// are separate renditions, switching audio never re-touches video, and an audio segment is seeked on the
// audio stream itself (every audio frame is a sync point) so there is no pre-roll seam. A segment is a
// function call over the RAM torrent cache — no subprocess, no producer to spawn/reposition/reap, no hang.

// streamLayout is a file's source stream map, probed once per file via in-process libav and cached: the
// primary video stream (+ its codec and source start-PTS, which drive the copy-vs-transcode grid choice)
// and the audio streams in rendition order, each with its codec.
type streamLayout struct {
	video         int      // source video stream index (-1 if none)
	videoCodec    string   // the video track's libav codec name ("h264" → copy grid, else transcode)
	videoStartSec float64  // the video track's source start-PTS in seconds (base for the uniform grid)
	audios        []int    // source stream indices of the audio tracks, in rendition order (index = relIndex)
	audioCodecs   []string // parallel to audios: each track's libav codec name ("aac", "ac3", …)
}

// getStreamLayout probes (once, cached) which source streams a file has, so a rendition request can map a
// relIndex to a real source stream index.
func (h *HandlerContext) getStreamLayout(ctx context.Context, hashHex, fileIndexStr string) (*streamLayout, error) {
	key := hashHex + "_" + fileIndexStr
	if v, ok := h.hlsStreamLayout.Load(key); ok {
		return v.(*streamLayout), nil
	}
	rs, _, err := h.openTorrentFileReader(ctx, hashHex, fileIndexStr, storage.ClassColdProbe)
	if err != nil {
		return nil, err
	}
	defer rs.Close()

	probe, err := media.ProbeTracks(ctx, rs)
	if err != nil {
		return nil, err
	}
	layout := &streamLayout{video: -1}
	for _, t := range probe.Tracks {
		switch t.Kind {
		case "video":
			if layout.video < 0 {
				layout.video = t.Index
				layout.videoCodec = t.Codec
				layout.videoStartSec = t.StartSec
			}
		case "audio":
			layout.audios = append(layout.audios, t.Index)
			layout.audioCodecs = append(layout.audioCodecs, t.Codec)
		}
	}
	h.hlsStreamLayout.Store(key, layout)
	return layout, nil
}

// --- VIDEO rendition (audio-agnostic; produced once, shared by every audio choice) ---

func (h *HandlerContext) produceVideoInit(ctx context.Context, hashHex, fileIndexStr string) ([]byte, error) {
	layout, err := h.getStreamLayout(ctx, hashHex, fileIndexStr)
	if err != nil {
		return nil, err
	}
	if layout.video < 0 {
		return nil, fmt.Errorf("no video stream")
	}
	sl, err := h.getSegList(ctx, hashHex, fileIndexStr)
	if err != nil {
		return nil, err
	}
	rs, _, err := h.openTorrentFileReader(ctx, hashHex, fileIndexStr, storage.ClassPlayback)
	if err != nil {
		return nil, err
	}
	defer rs.Close()
	// sl.transcode: H.264 with a readable keyframe index → copy; else transcode to H.264.
	return media.InitSegment(ctx, rs, []int{layout.video}, sl.transcode)
}

func (h *HandlerContext) produceVideoSegment(ctx context.Context, hashHex, fileIndexStr string, sl *segList, n int) ([]byte, error) {
	layout, err := h.getStreamLayout(ctx, hashHex, fileIndexStr)
	if err != nil {
		return nil, err
	}
	if layout.video < 0 {
		return nil, fmt.Errorf("no video stream")
	}
	return h.produceSegmentRetry(ctx, hashHex, fileIndexStr, []int{layout.video}, sl, n, sl.transcode)
}

// --- AUDIO renditions (one per source track; produced lazily) ---

func (h *HandlerContext) produceAudioInit(ctx context.Context, hashHex, fileIndexStr string, rel int) ([]byte, error) {
	layout, err := h.getStreamLayout(ctx, hashHex, fileIndexStr)
	if err != nil {
		return nil, err
	}
	if rel < 0 || rel >= len(layout.audios) {
		return nil, fmt.Errorf("audio track %d out of range", rel)
	}
	rs, _, err := h.openTorrentFileReader(ctx, hashHex, fileIndexStr, storage.ClassPlayback)
	if err != nil {
		return nil, err
	}
	defer rs.Close()
	// Audio decides copy (AAC) vs transcode (→AAC) per codec internally; transcodeVideo is irrelevant here.
	return media.InitSegment(ctx, rs, []int{layout.audios[rel]}, false)
}

func (h *HandlerContext) produceAudioSegment(ctx context.Context, hashHex, fileIndexStr string, rel int, sl *segList, n int) ([]byte, error) {
	layout, err := h.getStreamLayout(ctx, hashHex, fileIndexStr)
	if err != nil {
		return nil, err
	}
	if rel < 0 || rel >= len(layout.audios) {
		return nil, fmt.Errorf("audio track %d out of range", rel)
	}
	return h.produceSegmentRetry(ctx, hashHex, fileIndexStr, []int{layout.audios[rel]}, sl, n, false)
}

// produceSegmentRetry produces one fMP4 media segment for a single-stream rendition. A far seek can land
// the demux in a region the torrent hasn't fully downloaded yet: the reader returns bytes at the download
// front, the demuxer misparses (bad EBML / non-monotonic dts) and the mux rejects it. Those pieces fill in
// within moments, so retry a few times with a short backoff (a fresh reader each time).
func (h *HandlerContext) produceSegmentRetry(ctx context.Context, hashHex, fileIndexStr string, streams []int, sl *segList, n int, transcode bool) ([]byte, error) {
	var lastErr error
	for attempt := 0; attempt < 3; attempt++ {
		if attempt > 0 {
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(500 * time.Millisecond):
			}
		}
		rs, _, oerr := h.openTorrentFileReader(ctx, hashHex, fileIndexStr, storage.ClassPlayback)
		if oerr != nil {
			return nil, oerr
		}
		data, serr := media.Segment(ctx, rs, sl.srcStart(n), sl.extinf(n), streams, transcode)
		_ = rs.Close()
		if serr == nil {
			return data, nil
		}
		lastErr = serr
	}
	return nil, lastErr
}

// produceSubSegment renders one subtitle-rendition segment as WebVTT: the cues in this segment's time
// window, with ABSOLUTE timestamps (media.SubtitleWindow), so hls.js drops them onto the shared timeline.
// Only the client's ACTIVE subtitle track is ever fetched, so this stays lazy/windowed.
func (h *HandlerContext) produceSubSegment(ctx context.Context, hashHex, fileIndexStr string, rel int, sl *segList, n int) ([]byte, error) {
	rs, _, err := h.openTorrentFileReader(ctx, hashHex, fileIndexStr, storage.ClassAheadDemux)
	if err != nil {
		return nil, err
	}
	defer rs.Close()
	start := sl.srcStart(n)
	return media.SubtitleWindow(ctx, rs, rel, start, start+sl.extinf(n), "webvtt")
}
