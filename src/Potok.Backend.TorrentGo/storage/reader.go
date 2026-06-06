package storage

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"sync"

	"github.com/anacrolix/torrent"
)

type Reader struct {
	mu           sync.Mutex
	torrent      *torrent.Torrent
	cache        *Cache
	fileOffset   int64
	fileSize     int64
	pos          int64
	closed       bool
	lastPieceIdx int
	closeCh      chan struct{}
	closeOnce    sync.Once
	ctx          context.Context
}

func NewReader(ctx context.Context, t *torrent.Torrent, cache *Cache, fileOffset, fileSize int64) *Reader {
	r := &Reader{
		torrent:      t,
		cache:        cache,
		fileOffset:   fileOffset,
		fileSize:     fileSize,
		lastPieceIdx: -1,
		closeCh:      make(chan struct{}),
		ctx:          ctx,
	}
	cache.RegisterReader(r)
	return r
}

func (r *Reader) Read(p []byte) (n int, err error) {
	r.mu.Lock()
	if r.closed {
		r.mu.Unlock()
		return 0, errors.New("reader closed")
	}

	if r.pos >= r.fileSize {
		r.mu.Unlock()
		return 0, io.EOF
	}

	pos := r.pos
	fileOffset := r.fileOffset
	fileSize := r.fileSize
	r.mu.Unlock()

	slog.Debug("Reader Read called (unlocked)", "len(p)", len(p), "pos", pos, "fileSize", fileSize)

	limit := fileSize - pos
	if int64(len(p)) > limit {
		p = p[:limit]
	}
	if len(p) == 0 {
		return 0, nil
	}

	totalRead := 0
	for len(p) > 0 {
		absOffset := fileOffset + pos
		pieceIdx := int(absOffset / r.cache.pieceLen)
		pieceOffset := absOffset % r.cache.pieceLen
		pieceRemaining := r.cache.pieceLen - pieceOffset

		pieceSize := r.cache.pieceLen
		if pieceIdx == r.cache.pieceCount-1 {
			totalLen := r.torrent.Length()
			pieceSize = totalLen - int64(pieceIdx)*r.cache.pieceLen
			pieceRemaining = pieceSize - pieceOffset
		}

		if pieceRemaining <= 0 {
			break
		}

		toRead := int64(len(p))
		if toRead > pieceRemaining {
			toRead = pieceRemaining
		}

		mp := r.cache.GetOrCreateMemPiece(pieceIdx, pieceSize)

		r.mu.Lock()
		if r.closed {
			r.mu.Unlock()
			return totalRead, errors.New("reader closed")
		}
		lastPieceIdx := r.lastPieceIdx
		r.mu.Unlock()

		if pieceIdx != lastPieceIdx {
			r.mu.Lock()
			r.lastPieceIdx = pieceIdx
			r.mu.Unlock()

			fileStartPiece := int(fileOffset / r.cache.pieceLen)
			fileEndPiece := int((fileOffset + fileSize - 1) / r.cache.pieceLen)
			r.cache.UpdatePriorities(fileStartPiece, fileEndPiece)
		}

		for !mp.HasRange(pieceOffset, toRead) {
			watchCh := mp.Watch()
			if mp.HasRange(pieceOffset, toRead) {
				break
			}
			slog.Debug("Reader waiting for piece block range", "piece", pieceIdx, "offset", pieceOffset, "toRead", toRead)
			select {
			case <-watchCh:
				slog.Debug("Reader woke up: watchCh closed/notified", "piece", pieceIdx)
			case <-mp.Done():
				slog.Debug("Reader woke up: mp.Done() closed", "piece", pieceIdx)
			case <-r.closeCh:
				slog.Warn("Reader closed by reader.Close()", "piece", pieceIdx)
				return totalRead, errors.New("reader closed")
			case <-r.ctx.Done():
				slog.Warn("Reader request context cancelled", "piece", pieceIdx, "err", r.ctx.Err())
				return totalRead, r.ctx.Err()
			}
			r.mu.Lock()
			closed := r.closed
			r.mu.Unlock()
			if closed {
				return totalRead, errors.New("reader closed during read")
			}
		}

		nr, err := mp.ReadAt(p[:toRead], pieceOffset)
		if nr > 0 {
			totalRead += nr
			pos += int64(nr)
			r.mu.Lock()
			r.pos = pos
			r.mu.Unlock()
			p = p[nr:]
		}
		if err != nil && err != io.EOF {
			return totalRead, err
		}
		if nr == 0 {
			break
		}
	}

	if totalRead == 0 && pos >= fileSize {
		return 0, io.EOF
	}

	return totalRead, nil
}

func (r *Reader) Seek(offset int64, whence int) (int64, error) {
	r.mu.Lock()
	defer r.mu.Unlock()

	slog.Debug("Reader Seek called", "offset", offset, "whence", whence, "pos", r.pos)

	if r.closed {
		return 0, errors.New("reader closed")
	}

	var newPos int64
	switch whence {
	case io.SeekStart:
		newPos = offset
	case io.SeekCurrent:
		newPos = r.pos + offset
	case io.SeekEnd:
		newPos = r.fileSize + offset
	default:
		return 0, errors.New("invalid whence")
	}

	if newPos < 0 {
		return 0, errors.New("negative position")
	}
	r.pos = newPos
	return r.pos, nil
}

func (r *Reader) Close() error {
	r.mu.Lock()
	wasClosed := r.closed
	if !wasClosed {
		r.closed = true
		r.closeOnce.Do(func() {
			close(r.closeCh)
		})
	}
	r.mu.Unlock()

	if !wasClosed {
		r.cache.UnregisterReader(r)
		fileStartPiece := int(r.fileOffset / r.cache.pieceLen)
		fileEndPiece := int((r.fileOffset + r.fileSize - 1) / r.cache.pieceLen)
		r.cache.UpdatePriorities(fileStartPiece, fileEndPiece)
	}
	return nil
}

func (r *Reader) GetActiveWindow() (int, int) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.closed {
		return 0, 0
	}

	absOffset := r.fileOffset + r.pos
	currPiece := int(absOffset / r.cache.pieceLen)

	start := currPiece - 2
	if start < 0 {
		start = 0
	}
	end := currPiece + 15
	if end >= r.cache.pieceCount {
		end = r.cache.pieceCount - 1
	}
	return start, end
}
