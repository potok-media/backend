package storage

import (
	"io"
	"sync"
	"time"
)

type MemPiece struct {
	mu            sync.RWMutex
	size          int64
	data          []byte
	writtenBlocks []bool
	complete      bool
	accessed      time.Time
	doneCh        chan struct{}
	closeOnce     sync.Once
	notifyCh      chan struct{}
}

func NewMemPiece(size int64) *MemPiece {
	numBlocks := (size + 16383) / 16384
	return &MemPiece{
		size:          size,
		writtenBlocks: make([]bool, numBlocks),
		accessed:      time.Now(),
		doneCh:        make(chan struct{}),
		notifyCh:      make(chan struct{}),
	}
}

func (m *MemPiece) Release() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.data = nil
	m.writtenBlocks = nil
	m.complete = false
	if m.notifyCh != nil {
		select {
		case <-m.notifyCh:
			// already closed
		default:
			close(m.notifyCh)
		}
	}
	m.notifyCh = make(chan struct{})
}

func (m *MemPiece) ReadAt(p []byte, off int64) (n int, err error) {
	m.mu.Lock()
	m.accessed = time.Now()
	if m.data == nil {
		m.data = make([]byte, m.size)
	}
	m.mu.Unlock()

	m.mu.RLock()
	defer m.mu.RUnlock()

	if off >= int64(len(m.data)) {
		return 0, io.EOF
	}
	n = copy(p, m.data[off:])
	if n < len(p) {
		err = io.EOF
	}
	return
}

func (m *MemPiece) WriteAt(p []byte, off int64) (n int, err error) {
	m.mu.Lock()
	defer m.mu.Unlock()

	m.accessed = time.Now()
	if m.data == nil {
		m.data = make([]byte, m.size)
	}
	if off >= int64(len(m.data)) {
		return 0, io.ErrShortWrite
	}
	n = copy(m.data[off:], p)
	if n < len(p) {
		err = io.ErrShortWrite
	}

	startBlock := off / 16384
	endBlock := (off + int64(n) - 1) / 16384
	for i := startBlock; i <= endBlock && i < int64(len(m.writtenBlocks)); i++ {
		m.writtenBlocks[i] = true
	}

	if m.notifyCh != nil {
		select {
		case <-m.notifyCh:
			// already closed
		default:
			close(m.notifyCh)
		}
	}
	m.notifyCh = make(chan struct{})
	return
}

func (m *MemPiece) MarkComplete() {
	m.mu.Lock()
	m.complete = true
	if m.data == nil {
		m.data = make([]byte, m.size)
	}
	for i := range m.writtenBlocks {
		m.writtenBlocks[i] = true
	}
	m.accessed = time.Now()
	if m.notifyCh != nil {
		select {
		case <-m.notifyCh:
			// already closed
		default:
			close(m.notifyCh)
		}
	}
	m.notifyCh = make(chan struct{})
	m.mu.Unlock()

	m.closeOnce.Do(func() {
		close(m.doneCh)
	})
}

func (m *MemPiece) IsComplete() bool {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.complete
}

func (m *MemPiece) HasRange(off int64, length int64) bool {
	m.mu.RLock()
	defer m.mu.RUnlock()

	if m.complete {
		return true
	}
	if m.data == nil {
		return false
	}

	startBlock := off / 16384
	endBlock := (off + length - 1) / 16384
	if startBlock < 0 || endBlock >= int64(len(m.writtenBlocks)) {
		return false
	}

	for i := startBlock; i <= endBlock; i++ {
		if !m.writtenBlocks[i] {
			return false
		}
	}
	return true
}

func (m *MemPiece) Watch() <-chan struct{} {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.notifyCh
}

func (m *MemPiece) Done() <-chan struct{} {
	return m.doneCh
}

func (m *MemPiece) Accessed() time.Time {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.accessed
}
