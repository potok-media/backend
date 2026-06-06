package speed

import (
	"context"
	"sync"
	"time"

	"github.com/anacrolix/torrent"
)

type TorrentSpeed struct {
	DownloadSpeed int64 `json:"downloadSpeed"`
	UploadSpeed   int64 `json:"uploadSpeed"`
}

type Monitor struct {
	client *torrent.Client
	speeds map[string]TorrentSpeed
	mu     sync.RWMutex
}

func NewMonitor(client *torrent.Client) *Monitor {
	return &Monitor{
		client: client,
		speeds: make(map[string]TorrentSpeed),
	}
}

func (m *Monitor) Start(ctx context.Context) {
	ticker := time.NewTicker(1 * time.Second)
	defer ticker.Stop()

	type statsSnapshot struct {
		read  int64
		write int64
	}
	lastStats := make(map[string]statsSnapshot)

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			m.mu.Lock()
			currentTorrents := m.client.Torrents()
			activeHashes := make(map[string]bool)

			for _, t := range currentTorrents {
				h := t.InfoHash().HexString()
				activeHashes[h] = true

				stats := t.Stats()
				currRead := stats.BytesReadUsefulData.Int64()
				currWrite := stats.BytesWrittenData.Int64()

				var dlSpeed, ulSpeed int64
				if last, ok := lastStats[h]; ok {
					dlSpeed = currRead - last.read
					ulSpeed = currWrite - last.write
					if dlSpeed < 0 {
						dlSpeed = 0
					}
					if ulSpeed < 0 {
						ulSpeed = 0
					}
				}

				m.speeds[h] = TorrentSpeed{
					DownloadSpeed: dlSpeed,
					UploadSpeed:   ulSpeed,
				}

				lastStats[h] = statsSnapshot{
					read:  currRead,
					write: currWrite,
				}
			}

			for h := range m.speeds {
				if !activeHashes[h] {
					delete(m.speeds, h)
					delete(lastStats, h)
				}
			}
			m.mu.Unlock()
		}
	}
}

func (m *Monitor) GetSpeed(hashHex string) TorrentSpeed {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.speeds[hashHex]
}
