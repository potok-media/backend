package handlers

import (
	"context"
	"encoding/hex"
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"Potok.Backend.TorrentGo/storage"
	"github.com/anacrolix/torrent/metainfo"
)

// hlsSegmentSeconds is the target segment length. Kept moderate so the playlist isn't enormous and
// each on-demand segment is quick to produce.
const hlsSegmentSeconds = 4.0

// segList is the deterministic segmentation of a file: a fixed list of content-time boundaries used
// to render the VOD playlist and to position the ffmpeg producer. For H.264 with a readable
// container index the boundaries fall on real keyframes (copy); otherwise they're uniform and the
// producer transcodes with forced keyframes so the real cuts match.
type segList struct {
	contentStarts []float64 // 0-based content start of each segment (contentStarts[0] == 0)
	contentEnd    float64   // 0-based content end of the whole file
	base          float64   // source PTS of the first frame; srcStart(i) = contentStarts[i] + base
	transcode     bool      // producer must transcode (non-H.264 or no usable keyframe index)
}

func (s *segList) count() int { return len(s.contentStarts) }

func (s *segList) extinf(i int) float64 {
	if i < 0 || i >= len(s.contentStarts) {
		return 0
	}
	end := s.contentEnd
	if i+1 < len(s.contentStarts) {
		end = s.contentStarts[i+1]
	}
	d := end - s.contentStarts[i]
	if d < 0 {
		return 0
	}
	return d
}

// srcStart is the source-PTS seek point for segment i (what ffmpeg -ss receives).
func (s *segList) srcStart(i int) float64 {
	if i <= 0 {
		return s.base
	}
	if i >= len(s.contentStarts) {
		i = len(s.contentStarts) - 1
	}
	return s.contentStarts[i] + s.base
}

// getSegList builds (and caches) the segmentation for a file. Always returns a usable list: an
// index-based copy list when possible, else a uniform transcode list.
func (h *HandlerContext) getSegList(ctx context.Context, hashHex, fileIndexStr string) (*segList, error) {
	key := hashHex + "_" + fileIndexStr
	if v, ok := h.hlsSegList.Load(key); ok {
		return v.(*segList), nil
	}
	sl, err := h.buildSegList(ctx, hashHex, fileIndexStr)
	if err != nil {
		return nil, err
	}
	h.hlsSegList.Store(key, sl)
	return sl, nil
}

func (h *HandlerContext) buildSegList(ctx context.Context, hashHex, fileIndexStr string) (*segList, error) {
	dur, err := h.getOrProbeDuration(ctx, hashHex, fileIndexStr)
	if err != nil || dur <= 0 {
		return nil, fmt.Errorf("duration unavailable: %w", err)
	}

	// Only H.264 can be copied; anything else must transcode (browser can't decode it in MSE).
	isH264 := h.probeVideoCodec(ctx, hashHex, fileIndexStr) == "h264"

	if isH264 {
		if sl := h.tryIndexSegList(ctx, hashHex, fileIndexStr, dur); sl != nil {
			return sl, nil
		}
	}
	// Fallback: uniform boundaries, producer transcodes with forced keyframes so cuts line up.
	return uniformSegList(dur, h.videoStartPTS(ctx, hashHex, fileIndexStr)), nil
}

// tryIndexSegList reads the container keyframe index (moov / Cues) and derives keyframe-aligned
// boundaries. Returns nil (→ caller falls back to transcode) when the index is unavailable.
func (h *HandlerContext) tryIndexSegList(ctx context.Context, hashHex, fileIndexStr string, dur float64) *segList {
	rctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	rs, ext, err := h.openTorrentFileReader(rctx, hashHex, fileIndexStr)
	if err != nil {
		return nil
	}
	defer rs.Close()

	var kfs []float64
	var ok bool
	switch ext {
	case ".mp4", ".mov", ".m4v":
		kfs, ok, err = mp4Keyframes(rs)
	case ".mkv", ".webm":
		kfs, ok, err = mkvKeyframes(rs)
	default:
		return nil
	}
	if err != nil || !ok || len(kfs) < 1 {
		return nil
	}
	return computeSegList(kfs, dur)
}

// computeSegList turns a sorted list of source-PTS keyframes + total duration into keyframe-aligned
// content boundaries. Greedy rule: open a new segment at the first keyframe that is >=
// hlsSegmentSeconds past the current segment start — this reproduces ffmpeg -f hls -hls_time cuts
// exactly at every interior boundary.
func computeSegList(kfs []float64, dur float64) *segList {
	base := kfs[0]
	starts := []float64{0}
	segStart := kfs[0]
	for _, kf := range kfs[1:] {
		if kf-segStart >= hlsSegmentSeconds-0.001 {
			starts = append(starts, kf-base)
			segStart = kf
		}
	}
	contentEnd := dur - base
	if contentEnd <= starts[len(starts)-1] {
		contentEnd = starts[len(starts)-1] + hlsSegmentSeconds
	}
	return &segList{contentStarts: starts, contentEnd: contentEnd, base: base, transcode: false}
}

func uniformSegList(dur, base float64) *segList {
	contentEnd := dur - base
	if contentEnd <= 0 {
		contentEnd = dur
	}
	starts := []float64{}
	for t := 0.0; t < contentEnd-0.001; t += hlsSegmentSeconds {
		starts = append(starts, t)
	}
	if len(starts) == 0 {
		starts = []float64{0}
	}
	return &segList{contentStarts: starts, contentEnd: contentEnd, base: base, transcode: true}
}

// openTorrentFileReader returns a seekable reader over a torrent file plus its lowercase extension.
func (h *HandlerContext) openTorrentFileReader(ctx context.Context, hashHex, fileIndexStr string) (*storage.Reader, string, error) {
	var infoHash metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		return nil, "", fmt.Errorf("bad hash")
	}
	copy(infoHash[:], hexBytes)

	t, ok := h.Engine.Client.Torrent(infoHash)
	if !ok {
		return nil, "", fmt.Errorf("torrent not active")
	}
	if t.Info() == nil {
		select {
		case <-t.GotInfo():
		case <-ctx.Done():
			return nil, "", ctx.Err()
		}
	}
	idx := 0
	if _, err := fmt.Sscanf(fileIndexStr, "%d", &idx); err != nil || idx < 1 {
		return nil, "", fmt.Errorf("bad file index")
	}
	files := t.Files()
	if idx-1 < 0 || idx-1 >= len(files) {
		return nil, "", fmt.Errorf("file index out of range")
	}
	file := files[idx-1]
	cache, ok := h.Engine.Storage.GetCache(infoHash)
	if !ok {
		return nil, "", fmt.Errorf("no storage cache")
	}
	rs := storage.NewReader(ctx, t, cache, file.Offset(), file.Length())
	ext := strings.ToLower(filepath.Ext(file.Path()))
	return rs, ext, nil
}
