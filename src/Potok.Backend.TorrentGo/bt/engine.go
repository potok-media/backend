package bt

import (
	"fmt"
	"log/slog"
	"os"

	"Potok.Backend.TorrentGo/config"
	"Potok.Backend.TorrentGo/storage"
	"github.com/anacrolix/torrent"
)

type Engine struct {
	Client  *torrent.Client
	Storage *storage.Storage
	Config  *config.Config
}

func NewEngine(cfg *config.Config, store *storage.Storage) (*Engine, error) {
	clientCfg := torrent.NewDefaultClientConfig()
	
	if err := os.MkdirAll("./torrent-cache", 0755); err != nil {
		slog.Warn("Failed to create torrent-cache directory", "error", err)
	}
	clientCfg.DataDir = "./torrent-cache"
	
	clientCfg.DefaultStorage = store
	clientCfg.ListenPort = cfg.ListenPort
	clientCfg.EstablishedConnsPerTorrent = 250
	clientCfg.HalfOpenConnsPerTorrent = 100
	
	slog.Info("Initializing Torrent Client with custom storage...",
		slog.Int("listenPort", cfg.ListenPort),
		slog.String("dataDir", clientCfg.DataDir),
	)

	client, err := torrent.NewClient(clientCfg)
	if err != nil {
		return nil, fmt.Errorf("failed to create torrent client: %w", err)
	}

	return &Engine{
		Client:  client,
		Storage: store,
		Config:  cfg,
	}, nil
}

func (e *Engine) Close() error {
	slog.Info("Closing Torrent Engine...")
	e.Client.Close()
	return e.Storage.Close()
}
