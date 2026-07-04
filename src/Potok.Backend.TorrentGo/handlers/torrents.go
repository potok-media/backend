package handlers

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"Potok.Backend.TorrentGo/bt"
	"Potok.Backend.TorrentGo/config"
	"Potok.Backend.TorrentGo/speed"
	"github.com/anacrolix/torrent/metainfo"
	"github.com/go-chi/chi/v5"
	"golang.org/x/sync/singleflight"
)

type parsedFile struct {
	Item TorrentFileItem
	Path string
}

type HandlerContext struct {
	Engine            *bt.Engine
	SpeedMonitor      *speed.Monitor
	Config            *config.Config
	ThumbService      *ThumbnailService
	durationCache     sync.Map // map[string]float64
	timecodeCache     sync.Map // map[string]map[string]*TimecodeRange
	metadataCache     sync.Map // map[string][]byte
	headersCache      sync.Map // map[string]*FileHeaders
	metadataSFG       singleflight.Group
	hlsVideoCodec     sync.Map // map[string]string — cached video codec per file (h264 → copy)
	hlsVideoStartPTS  sync.Map // map[string]float64 — cached first video PTS (source offset) per file
	hlsSegList        sync.Map // map[string]*segList — cached VOD segmentation per file
	hlsSessions       sync.Map // map[string]*hlsSession — one repositionable ffmpeg muxer per (file,audio)
	hlsSegCache       segCache // LRU of produced segment bytes — serving source, decoupled from sessions
	hlsReaperOnce     sync.Once
	lastSeen          sync.Map // map[hash]time.Time — heartbeat from the client's status poll; drives the idle reaper
	subtitleCache     sync.Map // map[hash_file_relIndex_format][]byte — extracted subtitle text; one demux per file, served forever
	subtitleExtracted sync.Map // map[hash_file]bool — marks a file's one-pass subtitle extraction as already done
	subtitleSFG       singleflight.Group
	subtitleSem       chan struct{} // caps concurrent subtitle extractions (heavy full-file demux) to keep CPU/heat down
	ffmpegPath        string
	ffprobePath       string
	videoAccel        *hwAccel // chosen H.264 transcode backend (nil = software libx264)
}

func NewHandlerContext(engine *bt.Engine, sm *speed.Monitor, cfg *config.Config, ts *ThumbnailService) *HandlerContext {
	hc := &HandlerContext{
		Engine:            engine,
		SpeedMonitor:      sm,
		Config:            cfg,
		ThumbService:      ts,
	}
	if cfg != nil && cfg.HlsCacheBytes > 0 {
		hc.hlsSegCache.maxBytes = cfg.HlsCacheBytes
	}
	// Serialize subtitle extraction: it's a one-time, cached full-file demux per file — running
	// several at once is what pegged the CPU. One at a time keeps the box cool; cache makes it rare.
	hc.subtitleSem = make(chan struct{}, 1)
	hc.ffmpegPath, hc.ffprobePath = resolveFFmpegBinaries()
	hc.videoAccel = detectVideoAccel(hc.ffmpegPath)
	return hc
}

type TorrentFilesRequest struct {
	Title           string  `json:"title"`
	EnglishTitle    *string `json:"englishTitle,omitempty"`
	Link            *string `json:"link,omitempty"`
	MagnetUri       *string `json:"magnetUri,omitempty"`
	MediaType       *string `json:"mediaType,omitempty"`
	NumberOfSeasons *int    `json:"numberOfSeasons,omitempty"`
	OriginalTitle   *string `json:"originalTitle,omitempty"`
	Poster          *string `json:"poster,omitempty"`
	TmdbId          *int64  `json:"tmdbId,omitempty"`
}

type TorrentFileItem struct {
	Id         string  `json:"id"`
	Title      *string `json:"title"`
	SizeLabel  *string `json:"sizeLabel"`
	SizeBytes  *int64  `json:"sizeBytes"`
	Path       *string `json:"path"`
	IsSerial   bool    `json:"isSerial"`
	FolderName string  `json:"folderName"`
	Extension  string  `json:"extension"`
}

type TorrentFilesResponse struct {
	Hash  *string           `json:"hash"`
	Items []TorrentFileItem `json:"items"`
}

func (h *HandlerContext) HandleGetFiles(w http.ResponseWriter, r *http.Request) {
	var req TorrentFilesRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "Invalid request body", http.StatusBadRequest)
		return
	}

	link := ""
	if req.MagnetUri != nil && *req.MagnetUri != "" {
		link = *req.MagnetUri
	} else if req.Link != nil && *req.Link != "" {
		link = *req.Link
	}

	if link == "" {
		http.Error(w, "Link or MagnetUri is required", http.StatusBadRequest)
		return
	}

	slog.Info("Resolving torrent metadata", "title", req.Title)
	t, err := bt.ResolveTorrent(r.Context(), h.Engine.Client, link)
	if err != nil {
		slog.Error("Error adding torrent", "error", err)
		http.Error(w, fmt.Sprintf("Error adding torrent: %v", err), http.StatusInternalServerError)
		return
	}

	if t.Info() == nil {
		slog.Info("Waiting for metadata...", "hash", t.InfoHash().HexString())

		ctx, cancel := context.WithTimeout(r.Context(), 45*time.Second)
		defer cancel()

		select {
		case <-t.GotInfo():
			slog.Info("Metadata successfully resolved", "hash", t.InfoHash().HexString())
		case <-ctx.Done():
			slog.Warn("Metadata download timeout", "hash", t.InfoHash().HexString())
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusGatewayTimeout)
			_ = json.NewEncoder(w).Encode(map[string]string{
				"error":   "METADATA_TIMEOUT",
				"message": "Failed to download torrent metadata in time. Check seeders/trackers.",
			})
			return
		}
	}

	hashHex := t.InfoHash().HexString()
	// Grace heartbeat for a freshly-resolved torrent so the reaper doesn't drop it before the
	// client starts its status polling.
	h.lastSeen.Store(hashHex, time.Now())
	videoExtensions := map[string]bool{
		".mkv": true,
		".mp4": true,
		".avi": true,
		".ts":  true,
		".mov": true,
	}

	var videoFiles []parsedFile

	mediaType := ""
	if req.MediaType != nil {
		mediaType = *req.MediaType
	}

	for i, file := range t.Files() {
		path := file.Path()
		ext := strings.ToLower(filepath.Ext(path))

		if !videoExtensions[ext] {
			continue
		}

		name := filepath.Base(path)
		var title *string
		if name != "" {
			title = &name
		}

		sizeBytes := file.Length()

		item := TorrentFileItem{
			Id:         strconv.Itoa(i + 1), // original 1-based index in torrent
			Title:      title,
			SizeBytes:  &sizeBytes,
			Path:       &path,
			IsSerial:   mediaType == "tv",
			FolderName: "",
			Extension:  ext,
		}

		videoFiles = append(videoFiles, parsedFile{
			Item: item,
			Path: path,
		})
	}

	sort.Slice(videoFiles, func(i, j int) bool {
		return videoFiles[i].Path < videoFiles[j].Path
	})

	items := make([]TorrentFileItem, len(videoFiles))
	for i, vf := range videoFiles {
		items[i] = vf.Item
	}

	response := TorrentFilesResponse{
		Hash:  &hashHex,
		Items: items,
	}

	// Start background intro/outro timecode analysis if it is a multi-file torrent
	// Commented out to prevent network choking:
	// if len(videoFiles) >= 2 && !h.Config.DisableAnalyzer {
	// 	go h.AnalyzeTorrent(hashHex, videoFiles)
	// }

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(response)
}

func (h *HandlerContext) HandleGetStatus(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")

	var infoHash metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		http.Error(w, "Invalid torrent hash format", http.StatusBadRequest)
		return
	}
	copy(infoHash[:], hexBytes)

	// Heartbeat: the client polls this endpoint while the torrent is in use. As long as polls keep
	// arriving the idle reaper leaves it alone; when they stop (player/page closed) it gets dropped.
	h.lastSeen.Store(hashHex, time.Now())

	t, ok := h.Engine.Client.Torrent(infoHash)
	if !ok {
		http.Error(w, "Torrent not found", http.StatusNotFound)
		return
	}

	stats := t.Stats()
	speeds := h.SpeedMonitor.GetSpeed(hashHex)

	var progress float64 = 0.0
	length := t.Length()
	if length > 0 {
		progress = float64(t.BytesCompleted()) / float64(length)
	}

	state := "Downloading"
	if t.Info() == nil {
		state = "Metadata"
	} else if t.BytesCompleted() == length {
		state = "Seeding"
	}

	peers := stats.ActivePeers

	response := map[string]interface{}{
		"hash":          hashHex,
		"state":         state,
		"progress":      progress,
		"peers":         peers,
		"downloadSpeed": speeds.DownloadSpeed,
		"uploadSpeed":   speeds.UploadSpeed,
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(response)
}

func (h *HandlerContext) HandleDeleteTorrent(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")

	var infoHash metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		http.Error(w, "Invalid torrent hash format", http.StatusBadRequest)
		return
	}
	copy(infoHash[:], hexBytes)

	h.dropTorrent(infoHash, hashHex)

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]bool{"success": true})
}

// dropTorrent force-tears-down a torrent and frees ALL of its memory: kills its HLS ffmpeg
// producers (closing their loopback readers), closes+removes its piece cache, drops it from the
// client, and purges every per-file cache. Used by both the idle reaper and the DELETE endpoint.
func (h *HandlerContext) dropTorrent(infoHash metainfo.Hash, hashHex string) {
	h.purgeHlsSessions(hashHex) // cancel ffmpeg first so its loopback readers unwind
	if cache, ok := h.Engine.Storage.GetCache(infoHash); ok {
		_ = cache.Close()
		h.Engine.Storage.DeleteCache(infoHash)
	}
	if t, ok := h.Engine.Client.Torrent(infoHash); ok {
		t.Drop()
	}
	// Per-file caches that purgeHlsSessions doesn't cover — otherwise these never get freed.
	prefix := hashHex + "_"
	for _, m := range []*sync.Map{&h.metadataCache, &h.durationCache, &h.headersCache, &h.subtitleCache, &h.subtitleExtracted} {
		m.Range(func(k, _ interface{}) bool {
			if key, _ := k.(string); strings.HasPrefix(key, prefix) {
				m.Delete(k)
			}
			return true
		})
	}
	h.timecodeCache.Delete(hashHex)
	h.lastSeen.Delete(hashHex)
	slog.Info("torrent dropped", "hash", hashHex)
}

// ReapIdleTorrents drops torrents whose client heartbeat (the status poll) has gone silent past the
// idle timeout — the automatic replacement for a client-sent DELETE. Runs for the process lifetime.
func (h *HandlerContext) ReapIdleTorrents() {
	ticker := time.NewTicker(20 * time.Second)
	defer ticker.Stop()
	for range ticker.C {
		timeout := h.Config.TorrentIdleTimeout
		if timeout <= 0 {
			continue
		}
		now := time.Now()
		for _, t := range h.Engine.Client.Torrents() {
			hashHex := t.InfoHash().HexString()
			v, ok := h.lastSeen.Load(hashHex)
			if !ok {
				// No heartbeat recorded yet — grant a grace window rather than dropping immediately.
				h.lastSeen.Store(hashHex, now)
				continue
			}
			if now.Sub(v.(time.Time)) > timeout {
				h.dropTorrent(t.InfoHash(), hashHex)
			}
		}
	}
}

func (h *HandlerContext) getLoopbackURL(path string) string {
	var authPrefix string
	if h.Config.AuthUser != "" {
		authPrefix = fmt.Sprintf("%s:%s@", h.Config.AuthUser, h.Config.AuthPass)
	}
	return fmt.Sprintf("http://%s127.0.0.1:%d%s", authPrefix, h.Config.Port, path)
}

func (h *HandlerContext) HandleGetDiagnostics(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")

	var infoHash metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		http.Error(w, "Invalid torrent hash format", http.StatusBadRequest)
		return
	}
	copy(infoHash[:], hexBytes)

	t, ok := h.Engine.Client.Torrent(infoHash)
	if !ok {
		http.Error(w, "Torrent not found", http.StatusNotFound)
		return
	}

	stats := t.Stats()
	peerConns := t.PeerConns()

	peersList := []string{}
	for _, pc := range peerConns {
		peersList = append(peersList, pc.String())
	}

	response := map[string]interface{}{
		"hash":             hashHex,
		"hasInfo":          t.Info() != nil,
		"totalPeers":       stats.TotalPeers,
		"pendingPeers":     stats.PendingPeers,
		"activePeers":      stats.ActivePeers,
		"connectedSeeders": stats.ConnectedSeeders,
		"halfOpenPeers":    stats.HalfOpenPeers,
		"piecesComplete":   stats.PiecesComplete,
		"numPieces":        t.NumPieces(),
		"peerConns":        peersList,
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(response)
}
