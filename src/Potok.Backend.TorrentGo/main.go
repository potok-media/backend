package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"sync"
	"syscall"
	"time"

	"github.com/anacrolix/torrent"
	"github.com/go-chi/chi/v5"
	"github.com/go-chi/chi/v5/middleware"
)

var (
	torrentClient *torrent.Client
	cacheDir      string
	speedMonitor  *SpeedMonitor
)

type TorrentSpeed struct {
	DownloadSpeed int64 `json:"downloadSpeed"`
	UploadSpeed   int64 `json:"uploadSpeed"`
}

type SpeedMonitor struct {
	client *torrent.Client
	speeds map[string]TorrentSpeed
	mu     sync.RWMutex
}

func NewSpeedMonitor(client *torrent.Client) *SpeedMonitor {
	return &SpeedMonitor{
		client: client,
		speeds: make(map[string]TorrentSpeed),
	}
}

func (sm *SpeedMonitor) Start(ctx context.Context) {
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
			sm.mu.Lock()
			currentTorrents := sm.client.Torrents()

			// Build active hash set to clean up old entries
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

				sm.speeds[h] = TorrentSpeed{
					DownloadSpeed: dlSpeed,
					UploadSpeed:   ulSpeed,
				}

				lastStats[h] = statsSnapshot{
					read:  currRead,
					write: currWrite,
				}
			}

			// Clean up speed history for torrents that were removed
			for h := range sm.speeds {
				if !activeHashes[h] {
					delete(sm.speeds, h)
					delete(lastStats, h)
				}
			}
			sm.mu.Unlock()
		}
	}
}

func (sm *SpeedMonitor) GetSpeed(hashHex string) TorrentSpeed {
	sm.mu.RLock()
	defer sm.mu.RUnlock()
	return sm.speeds[hashHex]
}

func main() {
	log.Println("Starting Potok Go Torrent Engine...")

	// 1. Setup cache directory
	var err error
	cacheDir, err = filepath.Abs("./torrent-cache")
	if err != nil {
		log.Fatalf("Failed to get absolute path for cache: %v", err)
	}
	if err := os.MkdirAll(cacheDir, 0755); err != nil {
		log.Fatalf("Failed to create cache directory: %v", err)
	}
	log.Printf("Cache directory: %s", cacheDir)

	// 2. Configure torrent client
	cfg := torrent.NewDefaultClientConfig()
	cfg.DataDir = cacheDir

	// DHT endpoint listen port (DHT uses UDP, clients use TCP/UDP)
	cfg.ListenPort = 55123

	// Initialize the client
	client, err := torrent.NewClient(cfg)
	if err != nil {
		log.Fatalf("Failed to start torrent client: %v", err)
	}
	torrentClient = client
	log.Println("Torrent client initialized with uTP, DHT, and ListenPort 55123.")

	// 3. Initialize speed monitor
	speedMonitor = NewSpeedMonitor(client)
	monitorCtx, monitorCancel := context.WithCancel(context.Background())
	defer monitorCancel()
	go speedMonitor.Start(monitorCtx)

	// 4. Setup HTTP Router
	r := chi.NewRouter()
	r.Use(middleware.Logger)
	r.Use(middleware.Recoverer)
	r.Use(CORSMiddleware)

	// Health check
	r.Get("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("OK"))
	})

	// API routes
	r.Route("/api/torrent", func(r chi.Router) {
		r.Post("/files", HandleGetFiles)
		r.Get("/status/{hash}", HandleGetStatus)
		r.Delete("/{hash}", HandleDeleteTorrent)
	})

	// Streaming route
	r.Get("/stream/{hash}/{fileIndex}", HandleStream)
	r.Head("/stream/{hash}/{fileIndex}", HandleStream)

	// 5. Start Server
	server := &http.Server{
		Addr:    ":5282",
		Handler: r,
	}

	// Graceful shutdown
	go func() {
		sigChan := make(chan os.Signal, 1)
		signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)
		<-sigChan

		log.Println("Shutting down gracefully...")
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()

		server.Shutdown(ctx)
		torrentClient.Close()
		log.Println("Server stopped.")
	}()

	log.Println("Server is running on http://localhost:5282")
	if err := server.ListenAndServe(); err != http.ErrServerClosed {
		log.Fatalf("HTTP server failed: %v", err)
	}
}

// CORSMiddleware enables cross-origin resource sharing
func CORSMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "*")
		w.Header().Set("Access-Control-Expose-Headers", "Content-Range, Accept-Ranges, Content-Length, Content-Type")
		if r.Method == "OPTIONS" {
			w.WriteHeader(http.StatusOK)
			return
		}
		next.ServeHTTP(w, r)
	})
}
