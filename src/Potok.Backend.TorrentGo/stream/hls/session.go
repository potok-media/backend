package hls

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"time"
)

type HLSSession struct {
	mu           sync.RWMutex
	Key          string
	Cmd          *exec.Cmd
	Cancel       context.CancelFunc
	Segments     map[string]*Segment
	PlaylistData string
	LastAccess   time.Time
	StartPos     string
	SeqStart     int
	MaxSegments  int
	Done         bool
	Failed       bool
}

func NewHLSSession(key string, maxSegments int) *HLSSession {
	return &HLSSession{
		Key:         key,
		Segments:    make(map[string]*Segment),
		LastAccess:  time.Now(),
		MaxSegments: maxSegments,
	}
}

func (s *HLSSession) Touch() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.LastAccess = time.Now()
}

func (s *HLSSession) GetLastAccess() time.Time {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.LastAccess
}

func (s *HLSSession) IsDone() bool {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.Done
}

func (s *HLSSession) HasFailed() bool {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.Failed
}

func (s *HLSSession) SetPlaylistData(data string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.PlaylistData = data
}

func (s *HLSSession) GetPlaylistData() string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.PlaylistData
}

func (s *HLSSession) AddSegment(name string, data []byte) {
	s.mu.Lock()
	defer s.mu.Unlock()

	idx := parseSegmentIndex(name)
	seg, ok := s.Segments[name]
	if !ok {
		seg = NewSegment(idx, name)
		s.Segments[name] = seg
	}
	seg.SetData(data)

	if len(s.Segments) > s.MaxSegments {
		for k, v := range s.Segments {
			if v.Index < idx-10 {
				delete(s.Segments, k)
			}
		}
	}
}

func (s *HLSSession) GetSegment(name string) (*Segment, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	seg, ok := s.Segments[name]
	return seg, ok
}

func (s *HLSSession) GetOrCreateSegmentPlaceholder(name string, index int) *Segment {
	s.mu.Lock()
	defer s.mu.Unlock()

	seg, ok := s.Segments[name]
	if !ok {
		seg = NewSegment(index, name)
		s.Segments[name] = seg
	}
	return seg
}

func (s *HLSSession) ClearSegmentsOnSeek(newStartNum int) {
	s.mu.Lock()
	defer s.mu.Unlock()

	for k, v := range s.Segments {
		v.SetData(nil) // Wake up waiting readers to prevent hanging
		delete(s.Segments, k)
	}
}

func (s *HLSSession) CheckAndSeek(reqIdx int, segmentDuration int) (bool, string, int) {
	s.mu.Lock()
	defer s.mu.Unlock()

	// 1. If segment is already complete in memory, we never seek
	if seg, ok := s.Segments[fmt.Sprintf("segment_%05d.ts", reqIdx)]; ok && seg.IsComplete() {
		return false, "", 0
	}

	// 2. Find maximum generated segment index
	maxIdx := s.SeqStart - 1
	for _, v := range s.Segments {
		if v.Index > maxIdx {
			maxIdx = v.Index
		}
	}

	// 3. Seek if requested index is before start, or more than 10 segments ahead of current generation
	if reqIdx < s.SeqStart || reqIdx > maxIdx+10 {
		s.SeqStart = reqIdx
		
		// Run ClearSegmentsOnSeek outside s.mu lock of CheckAndSeek (it manages its own lock)
		s.mu.Unlock()
		s.ClearSegmentsOnSeek(reqIdx)
		s.mu.Lock()

		newStartSecs := float64(reqIdx) * float64(segmentDuration)
		newStartParam := fmt.Sprintf("%f", newStartSecs)
		return true, newStartParam, reqIdx
	}

	return false, "", 0
}

func (s *HLSSession) StartFFmpeg(ctx context.Context, inputURL, audioTrack, startPos string, startNum int, uploadURL string) error {
	s.mu.Lock()
	oldCmd := s.Cmd
	oldCancel := s.Cancel
	if oldCmd != nil {
		s.Cmd = nil
	}
	s.mu.Unlock()

	if oldCmd != nil {
		oldCancel()
		_ = oldCmd.Wait()
	}

	subCtx, cancel := context.WithCancel(ctx)

	s.mu.Lock()
	s.Cancel = cancel
	s.StartPos = startPos
	s.SeqStart = startNum
	s.Done = false
	s.Failed = false
	s.mu.Unlock()

	args := []string{
		"-nostdin",
		"-hide_banner",
	}

	if startPos != "" {
		args = append(args, "-ss", startPos)
	}

	args = append(args,
		"-i", inputURL,
		"-map", "0:v:0",
		"-map", fmt.Sprintf("0:a:%s?", audioTrack),
		"-c:v", "copy",
		"-c:a", "aac",
		"-ac", "2",
		"-af", "aresample=async=1",
		"-avoid_negative_ts", "make_zero",
	)

	if startPos != "" && startPos != "0" {
		args = append(args, "-output_ts_offset", startPos, "-muxdelay", "0")
	}

	args = append(args,
		"-f", "hls",
		"-hls_time", "6",
		"-hls_list_size", "0",
		"-hls_flags", "independent_segments",
		"-start_number", strconv.Itoa(startNum),
		"-hls_segment_type", "mpegts",
		"-hls_playlist_type", "event",
		"-method", "PUT",
		"-hls_segment_filename", fmt.Sprintf("%s/segment_%%05d.ts", uploadURL),
		fmt.Sprintf("%s/index.m3u8", uploadURL),
	)

	cmd := exec.CommandContext(subCtx, "ffmpeg", args...)

	s.mu.Lock()
	s.Cmd = cmd
	s.mu.Unlock()

	cmd.Stderr = os.Stderr

	slog.Info("Starting ffmpeg for HLS session", "key", s.Key, "args", strings.Join(args, " "))
	if err := cmd.Start(); err != nil {
		cancel()
		s.mu.Lock()
		if s.Cmd == cmd {
			s.Cmd = nil
		}
		s.mu.Unlock()
		return fmt.Errorf("failed to start ffmpeg: %w", err)
	}

	go func() {
		err := cmd.Wait()
		s.mu.Lock()
		if s.Cmd != cmd {
			s.mu.Unlock()
			return
		}
		s.Done = true
		if err != nil {
			if subCtx.Err() == nil {
				slog.Error("ffmpeg HLS process exited with error", "key", s.Key, "error", err)
				s.Failed = true
			} else {
				slog.Info("ffmpeg HLS process stopped by request", "key", s.Key)
			}
		} else {
			slog.Info("ffmpeg HLS process completed successfully", "key", s.Key)
		}
		s.mu.Unlock()
	}()

	return nil
}

func (s *HLSSession) Close() {
	s.mu.Lock()
	cmd := s.Cmd
	cancel := s.Cancel
	s.mu.Unlock()

	if cancel != nil {
		cancel()
	}
	if cmd != nil {
		_ = cmd.Wait()
	}

	s.mu.Lock()
	s.Segments = make(map[string]*Segment)
	s.mu.Unlock()
}

func parseSegmentIndex(name string) int {
	trimmed := strings.TrimPrefix(name, "segment_")
	trimmed = strings.TrimSuffix(trimmed, ".ts")
	idx, _ := strconv.Atoi(trimmed)
	return idx
}
