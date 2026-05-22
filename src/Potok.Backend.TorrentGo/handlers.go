package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"mime"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/anacrolix/torrent"
	"github.com/anacrolix/torrent/metainfo"
	"github.com/go-chi/chi/v5"
)

// Default trackers registered for every added torrent in parallel tiers
var defaultTrackers = [][]string{
	{"http://bt.t-ru.org/ann?magnet"},
	{"http://bt2.t-ru.org/ann?magnet"},
	{"http://bt3.t-ru.org/ann?magnet"},
	{"http://bt4.t-ru.org/ann?magnet"},
	{"udp://tracker.opentrackr.org:1337/announce"},
	{"udp://tracker.coppersurfer.tk:6969/announce"},
	{"udp://open.stealth.si:80/announce"},
	{"udp://tracker.torrent.eu.org:451/announce"},
	{"udp://exodus.desync.com:6969/announce"},
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
	Season     *int    `json:"season"`
	Episode    *int    `json:"episode"`
	IsSerial   bool    `json:"isSerial"`
	FolderName string  `json:"folderName"`
	Extension  string  `json:"extension"`
}

type TorrentFilesResponse struct {
	Hash  *string           `json:"hash"`
	Items []TorrentFileItem `json:"items"`
}

// HandleGetFiles registers a torrent and resolves its files list (metadata)
func HandleGetFiles(w http.ResponseWriter, r *http.Request) {
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

	log.Printf("Resolving torrent metadata for: %s", req.Title)
	t, err := getOrAddTorrent(link)
	if err != nil {
		log.Printf("Error adding torrent: %v", err)
		http.Error(w, fmt.Sprintf("Error adding torrent: %v", err), http.StatusInternalServerError)
		return
	}

	// If metadata isn't resolved, wait for it
	if t.Info() == nil {
		log.Printf("Waiting for metadata for torrent %s...", t.InfoHash().HexString())

		ctx, cancel := context.WithTimeout(r.Context(), 45*time.Second)
		defer cancel()

		select {
		case <-t.GotInfo():
			log.Printf("Metadata successfully resolved for: %s", t.InfoHash().HexString())
		case <-ctx.Done():
			log.Printf("Metadata download timeout for torrent: %s", t.InfoHash().HexString())
			t.Drop() // Clean up resources
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusGatewayTimeout)
			json.NewEncoder(w).Encode(map[string]string{
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

	type parsedFile struct {
		Item TorrentFileItem
		Path string
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
		season, episode := parseSeasonAndEpisode(path)

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
			Season:     season,
			Episode:    episode,
			IsSerial:   mediaType == "tv",
			FolderName: "",
			Extension:  ext,
		}

		videoFiles = append(videoFiles, parsedFile{
			Item: item,
			Path: path,
		})
	}

	// Sort video files alphabetically by path (matching C# behavior)
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

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
}

// HandleGetStatus returns active statistics of a torrent
func HandleGetStatus(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")

	var h metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		http.Error(w, "Invalid torrent hash format", http.StatusBadRequest)
		return
	}
	copy(h[:], hexBytes)

	t, ok := torrentClient.Torrent(h)
	if !ok {
		http.Error(w, "Torrent not found", http.StatusNotFound)
		return
	}

	stats := t.Stats()
	speeds := speedMonitor.GetSpeed(hashHex)

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
	json.NewEncoder(w).Encode(response)
}

// HandleDeleteTorrent stops, removes, and cleans up files of a torrent
func HandleDeleteTorrent(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")

	var h metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		http.Error(w, "Invalid torrent hash format", http.StatusBadRequest)
		return
	}
	copy(h[:], hexBytes)

	t, ok := torrentClient.Torrent(h)
	if !ok {
		// Return success if already deleted
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]bool{"success": true})
		return
	}

	log.Printf("Stopping and removing torrent: %s", hashHex)
	pathsToDelete := []string{}
	for _, file := range t.Files() {
		pathsToDelete = append(pathsToDelete, filepath.Join(cacheDir, file.Path()))
	}

	// Drop torrent from client
	t.Drop()

	// Clean up files and parent directories
	for _, p := range pathsToDelete {
		os.Remove(p) // Delete file if exists

		// Clean up parent directory if empty
		dir := filepath.Dir(p)
		if dir != cacheDir && strings.HasPrefix(dir, cacheDir) {
			os.Remove(dir) // Will only delete if directory is empty
		}
	}

	// Also delete any subdirectory matching hashHex
	hashDir := filepath.Join(cacheDir, hashHex)
	if _, err := os.Stat(hashDir); err == nil {
		os.RemoveAll(hashDir)
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]bool{"success": true})
}

// HandleStream handles seekable high-performance streaming with Range-requests
func HandleStream(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	var h metainfo.Hash
	hexBytes, err := hex.DecodeString(hashHex)
	if err != nil || len(hexBytes) != 20 {
		http.Error(w, "Invalid torrent hash format", http.StatusBadRequest)
		return
	}
	copy(h[:], hexBytes)

	t, ok := torrentClient.Torrent(h)
	if !ok {
		http.Error(w, "Torrent not active. Please add it first.", http.StatusNotFound)
		return
	}

	fileIndex, err := strconv.Atoi(fileIndexStr)
	if err != nil || fileIndex < 1 {
		http.Error(w, "Invalid file index. Must be 1-based.", http.StatusBadRequest)
		return
	}

	files := t.Files()
	idx := fileIndex - 1
	if idx < 0 || idx >= len(files) {
		http.Error(w, fmt.Sprintf("File index out of bounds. Must be between 1 and %d.", len(files)), http.StatusBadRequest)
		return
	}

	file := files[idx]

	// Create seekable reader
	reader := file.NewReader()
	defer reader.Close()

	// Aggressive read-ahead buffer (15 MB) to avoid buffering wheels
	reader.SetReadahead(15 * 1024 * 1024)
	// Prioritize active streaming blocks
	reader.SetResponsive()

	contentType := getMimeType(file.Path())

	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Accept-Ranges", "bytes")

	log.Printf("Streaming file: %s (Mime: %s, Size: %d bytes)", file.Path(), contentType, file.Length())

	// Use standard ServeContent to handle range requests elegantly
	http.ServeContent(w, r, filepath.Base(file.Path()), time.Time{}, reader)
}

// getOrAddTorrent is a helper to download or register a torrent across all formats
func getOrAddTorrent(link string) (*torrent.Torrent, error) {
	var t *torrent.Torrent
	var err error

	if strings.HasPrefix(strings.ToLower(link), "magnet:") {
		t, err = torrentClient.AddMagnet(link)
		if err != nil {
			return nil, fmt.Errorf("failed to add magnet: %w", err)
		}
	} else if strings.HasPrefix(strings.ToLower(link), "http://") || strings.HasPrefix(strings.ToLower(link), "https://") {
		resp, err := http.Get(link)
		if err != nil {
			return nil, fmt.Errorf("failed to download torrent file: %w", err)
		}
		defer resp.Body.Close()

		torrentBytes, err := io.ReadAll(resp.Body)
		if err != nil {
			return nil, fmt.Errorf("failed to read torrent body: %w", err)
		}

		mi, err := metainfo.Load(bytes.NewReader(torrentBytes))
		if err != nil {
			return nil, fmt.Errorf("failed to parse torrent metainfo: %w", err)
		}

		t, err = torrentClient.AddTorrent(mi)
		if err != nil {
			return nil, fmt.Errorf("failed to add torrent metainfo: %w", err)
		}
	} else if len(link) == 40 || len(link) == 64 {
		var h metainfo.Hash
		hexBytes, err := hex.DecodeString(link[:40])
		if err != nil {
			return nil, fmt.Errorf("failed to parse raw infohash: %w", err)
		}
		copy(h[:], hexBytes)

		var ok bool
		t, ok = torrentClient.Torrent(h)
		if !ok {
			magnetUri := "magnet:?xt=urn:btih:" + link[:40]
			t, err = torrentClient.AddMagnet(magnetUri)
			if err != nil {
				return nil, fmt.Errorf("failed to add magnet from infohash: %w", err)
			}
		}
	} else if _, err := os.Stat(link); err == nil {
		mi, err := metainfo.LoadFromFile(link)
		if err != nil {
			return nil, fmt.Errorf("failed to load local torrent file: %w", err)
		}
		t, err = torrentClient.AddTorrent(mi)
		if err != nil {
			return nil, fmt.Errorf("failed to add local torrent metainfo: %w", err)
		}
	} else {
		return nil, fmt.Errorf("unsupported torrent link format")
	}

	// Register high-speed trackers for parallel queries
	t.AddTrackers(defaultTrackers)

	return t, nil
}

// parseSeasonAndEpisode extracts season and episode numbers from title string or path
func parseSeasonAndEpisode(path string) (*int, *int) {
	name := filepath.Base(path)
	nameLower := strings.ToLower(name)
	pathLower := strings.ToLower(path)

	var season, episode *int

	// Season regexes
	seasonRegexes := []*regexp.Regexp{
		regexp.MustCompile(`\bs([0-9]+)\b`),                         // s03 / s3
		regexp.MustCompile(`(?:season|сезон)[\s._-]*([0-9]+)`),      // season 3 / сезон 3
		regexp.MustCompile(`(?:tv|тв)[\s._-]*([0-9]+)`),             // tv-3 / тв-3
		regexp.MustCompile(`([0-9]+)(?:st|nd|rd|th)[\s._-]*season`), // 3rd season
	}

	for _, re := range seasonRegexes {
		matches := re.FindStringSubmatch(pathLower)
		if len(matches) > 1 {
			if val, err := strconv.Atoi(matches[1]); err == nil {
				season = &val
				break
			}
		}
	}

	// Episode regexes
	episodeRegexes := []*regexp.Regexp{
		regexp.MustCompile(`\b(?:e|ep)[\s._-]*([0-9]+)\b`),             // e02 / ep02
		regexp.MustCompile(`(?:episode|серия|эпизод)[\s._-]*([0-9]+)`), // episode 2 / серия 2
		regexp.MustCompile(`\b-\s*([0-9]+)\b`),                         // - 02.mkv
		regexp.MustCompile(`[\[\(\s]([0-9]+)[\]\)\s]`),                 // [02] or (02)
	}

	for _, re := range episodeRegexes {
		matches := re.FindStringSubmatch(nameLower)
		if len(matches) > 1 {
			if val, err := strconv.Atoi(matches[1]); err == nil {
				episode = &val
				break
			}
		}
	}

	// Standalone "3 - 15" format (Season 3, Episode 15)
	if season == nil {
		reStandAlone := regexp.MustCompile(`\b([0-9]+)\s*-\s*([0-9]+)\b`)
		matches := reStandAlone.FindStringSubmatch(nameLower)
		if len(matches) > 2 {
			if sVal, err := strconv.Atoi(matches[1]); err == nil {
				if epVal, err := strconv.Atoi(matches[2]); err == nil {
					season = &sVal
					episode = &epVal
				}
			}
		}
	}

	// Fallback to finding any standalone number in filename that is not a video resolution
	if episode == nil {
		reNum := regexp.MustCompile(`\b([0-9]{1,3})\b`)
		matches := reNum.FindAllStringSubmatch(nameLower, -1)
		for _, match := range matches {
			if len(match) > 1 {
				valStr := match[1]
				if valStr == "1080" || valStr == "720" || valStr == "2160" || valStr == "264" || valStr == "265" || valStr == "4k" {
					continue
				}
				if val, err := strconv.Atoi(valStr); err == nil {
					if season != nil && *season == val {
						continue
					}
					episode = &val
					break
				}
			}
		}
	}

	return season, episode
}

// getMimeType maps media extensions to video content types for players
func getMimeType(path string) string {
	ext := strings.ToLower(filepath.Ext(path))
	switch ext {
	case ".mkv":
		return "video/x-matroska"
	case ".mp4":
		return "video/mp4"
	case ".avi":
		return "video/x-msvideo"
	case ".ts":
		return "video/MP2T"
	case ".mov":
		return "video/quicktime"
	default:
		t := mime.TypeByExtension(ext)
		if t != "" {
			return t
		}
		return "application/octet-stream"
	}
}
