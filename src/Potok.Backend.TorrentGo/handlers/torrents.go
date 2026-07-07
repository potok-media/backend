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
	Engine          *bt.Engine
	SpeedMonitor    *speed.Monitor
	Config          *config.Config
	ThumbService    *ThumbnailService
	durationCache   sync.Map // map[string]float64
	timecodeCache   sync.Map // map[string]map[string]*TimecodeRange
	metadataCache   sync.Map // map[string][]byte
	headersCache    sync.Map // map[string]*FileHeaders
	metadataSFG     singleflight.Group
	hlsSegList      sync.Map           // map[string]*segList — cached VOD segmentation per file
	hlsSegSFG       singleflight.Group // coalesce concurrent cold segList builds — the video + audio index.m3u8 requests build the SAME list, so only one runs the 40s+25s cold probe/Cues wait
	hlsStreamLayout sync.Map           // map[string]*streamLayout — cached source stream indices (video + audios) per file
	hlsLayoutSFG    singleflight.Group // coalesce concurrent cold stream-layout probes (every produce path + both playlists probe the same layout)
	audioCont       sync.Map           // map[hash_file_rel]*media.ContinuousAAC — continuous AAC transcode per NON-AAC audio track (copy-sliced into segments)
	audioContSFG    singleflight.Group // coalesce concurrent starts of the same track's continuous transcoder
	hlsSegCache     segCache           // LRU of produced segment bytes — serving source, decoupled from sessions
	// One lock over the torrent lifecycle: playback sessions + the drop grace clock. Single owner of that
	// state (anacrolix/Jellyfin shape), so the old Delete-outside-lock / LoadOrStore-before-lock races
	// can't recur. Rule: hold it only to read/mutate these maps; do the blocking torrent Drop OUTSIDE it.
	lifecycleMu       sync.Mutex
	playback          map[string]*playSession // sessionId → what a live player is watching (see playback.go)
	torrentSeen       map[string]time.Time    // hash → last time it had ≥1 session; grace clock for drop
	dropping          map[string]bool         // hash → a drop is in flight; makes dropTorrent idempotent (reaper vs DELETE)
	subtitleCache     sync.Map                // map[hash_file_relIndex_format][]byte — extracted subtitle text; one demux per file, served forever
	subtitleExtracted sync.Map                // map[hash_file]bool — marks a file's one-pass subtitle extraction as already done
	subtitleSFG       singleflight.Group
	subtitleWinBad    sync.Map     // map[hash_file]bool — windowed seek yielded nothing (no index); force the full-file path
	extExec           *extExecutor // one admission controller for ALL in-process extraction (window/heavy/analyze)
}

func NewHandlerContext(engine *bt.Engine, sm *speed.Monitor, cfg *config.Config, ts *ThumbnailService) *HandlerContext {
	hc := &HandlerContext{
		Engine:       engine,
		SpeedMonitor: sm,
		Config:       cfg,
		ThumbService: ts,
	}
	if cfg != nil && cfg.HlsCacheBytes > 0 {
		hc.hlsSegCache.maxBytes = cfg.HlsCacheBytes
	}
	// One admission controller for every in-process extraction, per-class limits: window (subtitle window +
	// thumbnail: cheap seek+read, a few in parallel), heavy (full-file demux: serialized — that's what
	// pegged the CPU), analyze (fingerprint decode). Replaces the four disjoint channel semaphores so a
	// slot is never held across a starvable read and pools can't leak into each other.
	hc.extExec = newExtExecutor(3, 1, 3)
	hc.playback = make(map[string]*playSession)
	hc.torrentSeen = make(map[string]time.Time)
	hc.dropping = make(map[string]bool)
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
	// Lifetime is owned by playback sessions (playback.go). A just-added torrent has no session yet, so
	// start its grace clock now — if the user browses and leaves without playing, the sweeper drops it
	// after torrentGrace; if they play, the first keepalive takes over as the owner.
	h.lifecycleMu.Lock()
	h.torrentSeen[hashHex] = time.Now()
	h.lifecycleMu.Unlock()
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

	// Pure UI stats now (peers/speed/progress) — lifetime is owned by playback sessions, not this poll.
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

// dropTorrent force-tears-down a torrent and frees ALL of its memory: purges its playback sessions,
// closes+removes its piece cache, drops it from the client, and purges every per-file cache. Used by both
// the idle reaper and the DELETE endpoint.
func (h *HandlerContext) dropTorrent(infoHash metainfo.Hash, hashHex string) {
	prefix := hashHex + "_"

	// One serialized owner step: claim the drop (idempotent vs reaper+DELETE), purge this hash's playback
	// sessions, and clear the grace clock — all under the lock.
	h.lifecycleMu.Lock()
	if h.dropping[hashHex] {
		h.lifecycleMu.Unlock()
		return
	}
	h.dropping[hashHex] = true
	// Purge playback sessions for this hash so a re-add doesn't inherit phantom refcounts.
	for id, ps := range h.playback {
		if ps.hash == hashHex {
			delete(h.playback, id)
		}
	}
	delete(h.torrentSeen, hashHex)
	h.lifecycleMu.Unlock()

	// Per-file caches (segmentation/codec/PTS/subtitles/metadata/segment bytes).
	for _, m := range []*sync.Map{&h.metadataCache, &h.durationCache, &h.headersCache, &h.subtitleCache, &h.subtitleExtracted, &h.subtitleWinBad, &h.hlsSegList, &h.hlsStreamLayout} {
		m.Range(func(k, _ interface{}) bool {
			if key, _ := k.(string); strings.HasPrefix(key, prefix) {
				m.Delete(k)
			}
			return true
		})
	}
	h.timecodeCache.Delete(hashHex)
	h.hlsSegCache.purgePrefix(prefix)
	h.dropAudioCont(prefix) // stop + free continuous-AAC transcoders (goroutine + ~100MB/track)

	// Close (sets closed=true → no resurrection) then Drop; both can block on the torrent client's own
	// goroutines, so run them detached — the lifecycle state is already consistent. Clear `dropping`
	// after, keeping a re-drop from racing the slow teardown.
	go func() {
		if cache, ok := h.Engine.Storage.GetCache(infoHash); ok {
			_ = cache.Close()
			h.Engine.Storage.DeleteCache(infoHash)
		}
		if t, ok := h.Engine.Client.Torrent(infoHash); ok {
			t.Drop()
		}
		h.lifecycleMu.Lock()
		delete(h.dropping, hashHex)
		h.lifecycleMu.Unlock()
		slog.Info("torrent dropped", "hash", hashHex)
	}()
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
