package storage

import (
	"context"
	"sync"

	"Potok.Backend.TorrentGo/config"
	"github.com/anacrolix/torrent/metainfo"
	"github.com/anacrolix/torrent/storage"
)

type Storage struct {
	mu     sync.RWMutex
	caches map[metainfo.Hash]*Cache
	config *config.Config
}

func NewStorage(cfg *config.Config) *Storage {
	return &Storage{
		caches: make(map[metainfo.Hash]*Cache),
		config: cfg,
	}
}

// OpenTorrent implements storage.ClientImpl
func (s *Storage) OpenTorrent(ctx context.Context, info *metainfo.Info, hash metainfo.Hash) (storage.TorrentImpl, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	pieceCount := len(info.Pieces) / 20

	cache := NewCache(hash, s.config.CacheSizeBytes, info.PieceLength, pieceCount)
	cache.totalSize = info.TotalLength()
	s.caches[hash] = cache

	impl := storage.TorrentImpl{
		Piece: func(p metainfo.Piece) storage.PieceImpl {
			return cache.Piece(p)
		},
		Close: func() error {
			return cache.Close()
		},
	}
	return impl, nil
}

func (s *Storage) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()

	for _, cache := range s.caches {
		_ = cache.Close()
	}
	s.caches = make(map[metainfo.Hash]*Cache)
	return nil
}

func (s *Storage) GetCache(hash metainfo.Hash) (*Cache, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()

	cache, ok := s.caches[hash]
	return cache, ok
}

func (s *Storage) DeleteCache(hash metainfo.Hash) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if cache, ok := s.caches[hash]; ok {
		_ = cache.Close()
		delete(s.caches, hash)
	}
}
