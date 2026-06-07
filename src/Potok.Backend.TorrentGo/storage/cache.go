package storage

import (
	"context"
	"log/slog"
	"sort"
	"sync"
	"time"

	"github.com/anacrolix/torrent"
	"github.com/anacrolix/torrent/metainfo"
	"github.com/anacrolix/torrent/storage"
)

type Cache struct {
	mu         sync.RWMutex
	hash       metainfo.Hash
	pieces     map[int]*MemPiece
	readers    map[*Reader]struct{}
	capacity   int64
	filled     int64
	pieceLen   int64
	pieceCount int
	totalSize  int64
	torrent    *torrent.Torrent
}

func NewCache(hash metainfo.Hash, capacity int64, pieceLen int64, pieceCount int) *Cache {
	return &Cache{
		hash:       hash,
		pieces:     make(map[int]*MemPiece),
		readers:    make(map[*Reader]struct{}),
		capacity:   capacity,
		pieceLen:   pieceLen,
		pieceCount: pieceCount,
	}
}

// Implement storage.TorrentImpl-like Piece getter
func (c *Cache) Piece(p metainfo.Piece) storage.PieceImpl {
	index := p.Index()
	size := c.pieceLen
	if index == c.pieceCount-1 {
		if c.totalSize > 0 {
			size = c.totalSize - int64(index)*c.pieceLen
		} else if p.Info != nil {
			totalLen := p.Info.Length
			if totalLen > 0 {
				size = totalLen - int64(index)*c.pieceLen
			}
		}
	}

	return NewPiece(index, size, c)
}

func (c *Cache) Close() error {
	c.mu.Lock()
	defer c.mu.Unlock()

	for _, mp := range c.pieces {
		mp.Release()
	}
	c.pieces = make(map[int]*MemPiece)
	c.filled = 0
	return nil
}

func (c *Cache) GetOrCreateMemPiece(idx int, size int64) *MemPiece {
	c.mu.Lock()
	defer c.mu.Unlock()

	mp, ok := c.pieces[idx]
	if !ok {
		mp = NewMemPiece(size)
		c.pieces[idx] = mp
	}
	return mp
}

func (c *Cache) MarkNotComplete(idx int) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if mp, ok := c.pieces[idx]; ok {
		mp.Release()
		delete(c.pieces, idx)
	}
}

func (c *Cache) UpdateFilled(n int64) {
	c.mu.Lock()
	c.filled += n
	c.mu.Unlock()
}

func (c *Cache) EvictIfNeeded() {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.filled <= c.capacity {
		return
	}

	c.cleanPieces()
}

func (c *Cache) RegisterReader(r *Reader) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.readers[r] = struct{}{}
	if c.torrent == nil {
		c.torrent = r.torrent
	}
}

func (c *Cache) UnregisterReader(r *Reader) {
	c.mu.Lock()
	defer c.mu.Unlock()
	delete(c.readers, r)
}

func (c *Cache) getReaderWindows() map[int]bool {
	protected := make(map[int]bool)
	for r := range c.readers {
		// 1. Protect sliding active window
		start, end := r.GetActiveWindow()
		for i := start; i <= end; i++ {
			protected[i] = true
		}

		// 2. Protect file headers (first 3 pieces) and footers (last 3 pieces)
		r.mu.Lock()
		fileOffset := r.fileOffset
		fileSize := r.fileSize
		r.mu.Unlock()

		fileStartPiece := int(fileOffset / c.pieceLen)
		fileEndPiece := int((fileOffset + fileSize - 1) / c.pieceLen)

		// Protect first 3 pieces of the file
		for i := fileStartPiece; i < fileStartPiece+3 && i <= fileEndPiece; i++ {
			protected[i] = true
		}
		// Protect last 3 pieces of the file
		for i := fileEndPiece - 2; i <= fileEndPiece && i >= fileStartPiece; i++ {
			protected[i] = true
		}
	}
	return protected
}

func (c *Cache) cleanPieces() {
	protected := c.getReaderWindows()

	type candidatePiece struct {
		index int
		mp    *MemPiece
	}
	var candList []candidatePiece

	for idx, mp := range c.pieces {
		if !protected[idx] && mp.IsComplete() {
			candList = append(candList, candidatePiece{index: idx, mp: mp})
		}
	}

	sort.Slice(candList, func(i, j int) bool {
		return candList[i].mp.Accessed().Before(candList[j].mp.Accessed())
	})

	for _, cand := range candList {
		if c.filled <= c.capacity {
			break
		}

		cand.mp.mu.RLock()
		size := int64(len(cand.mp.data))
		cand.mp.mu.RUnlock()

		c.filled -= size
		cand.mp.Release()
		delete(c.pieces, cand.index)

		slog.Debug("Evicted piece", "index", cand.index, "size", size, "hash", c.hash.HexString())

		if c.torrent != nil {
			t := c.torrent
			idx := cand.index
			go t.Piece(idx).UpdateCompletion()
		}
	}
}

func (c *Cache) ActiveReaderCount() int {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return len(c.readers)
}

func (c *Cache) UpdatePriorities(fileStartPiece, fileEndPiece int) {
	c.mu.Lock()
	t := c.torrent
	if t == nil {
		c.mu.Unlock()
		return
	}

	desired := make(map[int]torrent.PiecePriority)
	pieceLen := c.pieceLen
	pieceCount := c.pieceCount

	for r := range c.readers {
		r.mu.Lock()
		closed := r.closed
		pos := r.pos
		fileOffset := r.fileOffset
		r.mu.Unlock()

		if closed {
			continue
		}

		absOffset := fileOffset + pos
		currPiece := int(absOffset / pieceLen)

		if currPiece >= fileStartPiece && currPiece <= fileEndPiece {
			desired[currPiece] = torrent.PiecePriorityNow
		}

		for idx := currPiece + 1; idx <= currPiece+30; idx++ {
			if idx >= fileStartPiece && idx <= fileEndPiece {
				if desired[idx] < torrent.PiecePriorityHigh {
					desired[idx] = torrent.PiecePriorityHigh
				}
			}
		}

		// Ensure first 2 pieces (headers) are prioritized
		for idx := fileStartPiece; idx < fileStartPiece+2 && idx <= fileEndPiece; idx++ {
			if desired[idx] < torrent.PiecePriorityHigh {
				desired[idx] = torrent.PiecePriorityHigh
			}
		}

		// Ensure last 2 pieces (footers/cues) are prioritized
		for idx := fileEndPiece - 1; idx <= fileEndPiece && idx >= fileStartPiece; idx++ {
			if desired[idx] < torrent.PiecePriorityHigh {
				desired[idx] = torrent.PiecePriorityHigh
			}
		}
	}
	c.mu.Unlock()

	for idx := fileStartPiece; idx <= fileEndPiece && idx < pieceCount; idx++ {
		prio, ok := desired[idx]
		if !ok {
			prio = torrent.PiecePriorityNone
		}
		t.Piece(idx).SetPriority(prio)
	}
}

func (c *Cache) PieceLen() int64 {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.pieceLen
}

func (c *Cache) PieceCount() int {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.pieceCount
}

func (c *Cache) WaitForPieces(indices []int, totalLength int64, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for _, idx := range indices {
		c.mu.Lock()
		pieceLen := c.pieceLen
		pieceCount := c.pieceCount
		c.mu.Unlock()

		if idx < 0 || idx >= pieceCount {
			continue
		}

		size := pieceLen
		if idx == pieceCount-1 {
			size = totalLength - int64(idx)*pieceLen
		}

		mp := c.GetOrCreateMemPiece(idx, size)
		
		timeRemaining := time.Until(deadline)
		if timeRemaining <= 0 {
			return context.DeadlineExceeded
		}

		select {
		case <-mp.Done():
			// piece complete
		case <-time.After(timeRemaining):
			return context.DeadlineExceeded
		}
	}
	return nil
}
