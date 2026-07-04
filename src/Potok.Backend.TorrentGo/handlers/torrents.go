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
}

func NewHandlerContext(engine *bt.Engine, sm *speed.Monitor, cfg *config.Config, ts *ThumbnailService) *HandlerContext {
	return &HandlerContext{
		Engine:            engine,
		SpeedMonitor:      sm,
		Config:            cfg,
		ThumbService:      ts,
	}
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

	h.purgeHlsSessions(hashHex)

	t, ok := h.Engine.Client.Torrent(infoHash)
	if !ok {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]bool{"success": true})
		return
	}

	// Prevent deletion if actively streaming
	if cache, ok := h.Engine.Storage.GetCache(infoHash); ok {
		if cache.ActiveReaderCount() > 0 {
			http.Error(w, "Torrent is actively playing", http.StatusConflict)
			return
		}
		_ = cache.Close()
		h.Engine.Storage.DeleteCache(infoHash)
	}

	slog.Info("Stopping and dropping torrent from client", "hash", hashHex)
	t.Drop()

	h.timecodeCache.Delete(hashHex)

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]bool{"success": true})
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
