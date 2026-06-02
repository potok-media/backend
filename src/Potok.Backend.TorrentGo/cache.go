package main

import (
	"context"
	"log/slog"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/anacrolix/torrent"
)

type CacheManager struct {
	client        *torrent.Client
	cacheDir      string
	timeout       time.Duration
	checkInterval time.Duration
	maxCacheSize  int64

	mu          sync.RWMutex
	lastActive  map[string]time.Time
	activeCount map[string]int
}

func NewCacheManager(client *torrent.Client, cacheDir string, timeout time.Duration, checkInterval time.Duration) *CacheManager {
	var maxSize int64 = 10 * 1024 * 1024 * 1024 // 10 GB default
	if envVal := os.Getenv("POTOK_MAX_CACHE_SIZE_GB"); envVal != "" {
		if val, err := strconv.ParseInt(envVal, 10, 64); err == nil && val > 0 {
			maxSize = val * 1024 * 1024 * 1024
		}
	}

	return &CacheManager{
		client:        client,
		cacheDir:      cacheDir,
		timeout:       timeout,
		checkInterval: checkInterval,
		maxCacheSize:  maxSize,
		lastActive:    make(map[string]time.Time),
		activeCount:   make(map[string]int),
	}
}

func (cm *CacheManager) Touch(hash string) {
	cm.mu.Lock()
	defer cm.mu.Unlock()
	cm.lastActive[hash] = time.Now()
}

func (cm *CacheManager) IncrementActive(hash string) {
	cm.mu.Lock()
	defer cm.mu.Unlock()
	cm.activeCount[hash]++
	cm.lastActive[hash] = time.Now()
}

func (cm *CacheManager) DecrementActive(hash string) {
	cm.mu.Lock()
	defer cm.mu.Unlock()
	// Prevent resurrection of purged torrents
	if _, ok := cm.lastActive[hash]; !ok {
		return
	}
	cm.activeCount[hash]--
	if cm.activeCount[hash] < 0 {
		cm.activeCount[hash] = 0
	}
	cm.lastActive[hash] = time.Now()
}

func (cm *CacheManager) IsStreaming(hash string) bool {
	cm.mu.RLock()
	defer cm.mu.RUnlock()
	return cm.activeCount[hash] > 0
}

func (cm *CacheManager) Delete(hash string) {
	cm.mu.Lock()
	defer cm.mu.Unlock()
	delete(cm.lastActive, hash)
	delete(cm.activeCount, hash)
}

func (cm *CacheManager) Start(ctx context.Context) {
	slog.Info("CacheManager background worker started",
		slog.Duration("timeout", cm.timeout),
		slog.Duration("checkInterval", cm.checkInterval),
		slog.Int64("maxCacheSizeGB", cm.maxCacheSize/(1024*1024*1024)),
	)

	ticker := time.NewTicker(cm.checkInterval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			slog.Info("CacheManager worker stopped cleanly.")
			return
		case <-ticker.C:
			cm.Clean()
		}
	}
}

func getDirSize(path string) (int64, error) {
	var size int64
	err := filepath.Walk(path, func(_ string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if !info.IsDir() {
			size += info.Size()
		}
		return nil
	})
	return size, err
}

func (cm *CacheManager) Clean() {
	torrents := cm.client.Torrents()
	now := time.Now()

	// 1. First run the idle-timeout sweep
	for _, t := range torrents {
		hashHex := t.InfoHash().HexString()

		cm.mu.RLock()
		activeCount := cm.activeCount[hashHex]
		lastTouch, ok := cm.lastActive[hashHex]
		cm.mu.RUnlock()

		if !ok {
			cm.Touch(hashHex)
			continue
		}

		if activeCount > 0 {
			cm.Touch(hashHex)
			continue
		}

		if now.Sub(lastTouch) > cm.timeout {
			slog.Info("CacheManager: Inactive torrent detected. Starting purge...",
				slog.String("hash", hashHex),
				slog.Duration("idleDuration", now.Sub(lastTouch)),
			)
			cm.PurgeTorrent(t)
		}
	}

	// 2. Perform total cache size limit check (LRU Cache Eviction)
	totalSize, err := getDirSize(cm.cacheDir)
	if err != nil {
		slog.Warn("CacheManager: Failed to calculate cache directory size", slog.String("error", err.Error()))
		return
	}

	if totalSize <= cm.maxCacheSize {
		return // We are safely below the cache ceiling!
	}

	slog.Info("CacheManager: Cache size limit exceeded. Initiating LRU eviction sweep...",
		slog.Int64("currentSize", totalSize),
		slog.Int64("maxSize", cm.maxCacheSize),
	)

	// Gather all inactive torrents that can be safely evicted (activeCount == 0)
	type evictionItem struct {
		torrent    *torrent.Torrent
		lastActive time.Time
	}
	var inactiveTorrents []evictionItem

	torrents = cm.client.Torrents() // refresh torrents list
	for _, t := range torrents {
		hashHex := t.InfoHash().HexString()
		cm.mu.RLock()
		activeCount := cm.activeCount[hashHex]
		lastTouch := cm.lastActive[hashHex]
		cm.mu.RUnlock()

		if activeCount == 0 {
			inactiveTorrents = append(inactiveTorrents, evictionItem{
				torrent:    t,
				lastActive: lastTouch,
			})
		}
	}

	// Sort inactive torrents by their last active time (oldest first)
	sort.Slice(inactiveTorrents, func(i, j int) bool {
		return inactiveTorrents[i].lastActive.Before(inactiveTorrents[j].lastActive)
	})

	// Evict oldest torrents until we are below the maxCacheSize limit
	for _, item := range inactiveTorrents {
		hashHex := item.torrent.InfoHash().HexString()
		slog.Info("CacheManager: Evicting oldest inactive torrent (LRU)",
			slog.String("hash", hashHex),
			slog.Time("lastActive", item.lastActive),
		)

		// Subtract the size of the evicted torrent mathematically to avoid infinite queries
		// on disk while files are asynchronously deleted
		evictedSize := item.torrent.BytesCompleted()
		totalSize -= evictedSize

		cm.PurgeTorrent(item.torrent)

		if totalSize <= cm.maxCacheSize {
			slog.Info("CacheManager: LRU eviction completed. New size is within limits mathematically.", slog.Int64("size", totalSize))
			break
		}
	}
}

func (cm *CacheManager) PurgeTorrent(t *torrent.Torrent) {
	hashHex := t.InfoHash().HexString()

	// 1. Gather files and root folder to delete
	pathsToDelete := []string{}
	var torrentRootDir string

	if t.Info() != nil {
		torrentName := t.Name()
		if torrentName != "" {
			torrentRootDir = filepath.Join(cm.cacheDir, torrentName)
			pathsToDelete = append(pathsToDelete, torrentRootDir)
		}
		for _, file := range t.Files() {
			pathsToDelete = append(pathsToDelete, filepath.Join(cm.cacheDir, file.Path()))
		}
	}

	// 2. Drop torrent from client memory
	t.Drop()
	cm.Delete(hashHex)
	slog.Info("CacheManager: Torrent dropped from client memory", slog.String("hash", hashHex))

	// 3. Delete files from disk asynchronously in a background goroutine
	go func() {
		// Give the OS and torrent client 2 seconds to release any locks
		time.Sleep(2 * time.Second)

		// Delete all paths recursively (including the root directory/file)
		for _, p := range pathsToDelete {
			if err := os.RemoveAll(p); err != nil {
				if os.IsNotExist(err) {
					continue
				}
				slog.Warn("CacheManager: Failed to delete cached path, retrying in 5 seconds", slog.String("path", p), slog.String("error", err.Error()))
				time.Sleep(5 * time.Second)
				if err := os.RemoveAll(p); err != nil {
					if !os.IsNotExist(err) {
						slog.Error("CacheManager: Failed to delete cached path after retry", slog.String("path", p), slog.String("error", err.Error()))
					}
				} else {
					slog.Info("CacheManager: Deleted cached path on retry", slog.String("path", p))
				}
			} else {
				slog.Debug("CacheManager: Deleted cached path", slog.String("path", p))
			}
		}

		// Cleanup empty directories recursively up to the cache root
		if torrentRootDir != "" {
			cm.removeEmptyDirs(filepath.Dir(torrentRootDir))
		}

		// 4. Delete the dedicated hash folder if it exists
		hashDir := filepath.Join(cm.cacheDir, hashHex)
		if _, err := os.Stat(hashDir); err == nil {
			if err := os.RemoveAll(hashDir); err != nil {
				slog.Error("CacheManager: Failed to delete dedicated torrent hash folder", slog.String("path", hashDir), slog.String("error", err.Error()))
			} else {
				slog.Info("CacheManager: Deleted dedicated torrent hash folder", slog.String("path", hashDir))
			}
		}
	}()
}

func (cm *CacheManager) removeEmptyDirs(dirPath string) {
	current := filepath.Clean(dirPath)
	root := filepath.Clean(cm.cacheDir)

	for current != root && strings.HasPrefix(current, root) {
		entries, err := os.ReadDir(current)
		if err != nil || len(entries) > 0 {
			return // Not empty or error
		}

		if err := os.Remove(current); err != nil {
			slog.Warn("Failed to remove directory", slog.String("dir", current), slog.String("error", err.Error()))
			return
		}

		slog.Debug("Removed empty parent directory", slog.String("dir", current))
		current = filepath.Dir(current)
	}
}
