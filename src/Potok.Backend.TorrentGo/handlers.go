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
	"os/exec"
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

var cacheManager *CacheManager

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
	cacheManager.Touch(hashHex)
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
	cacheManager.Touch(hashHex)

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
	cacheManager.PurgeTorrent(t)

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]bool{"success": true})
}

// HandleStream handles seekable high-performance streaming with Range-requests or dynamic fMP4 remuxing
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

	cacheManager.IncrementActive(hashHex)
	defer cacheManager.DecrementActive(hashHex)

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

	// 1. Process Loopback Recursion & FFmpeg Bypass checks
	isRaw := r.URL.Query().Get("raw") == "true"
	isFFmpeg := strings.HasPrefix(r.Header.Get("User-Agent"), "Lavf/")

	if isRaw || isFFmpeg {
		// Serve original direct file stream
		file.Download()
		reader := file.NewReader()
		defer reader.Close()
		reader.SetReadahead(35 * 1024 * 1024)
		reader.SetResponsive()

		contentType := getMimeType(file.Path())
		w.Header().Set("Content-Type", contentType)
		w.Header().Set("Accept-Ranges", "bytes")
		w.Header().Set("Access-Control-Allow-Origin", "*")

		http.ServeContent(w, r, filepath.Base(file.Path()), time.Time{}, reader)
		return
	}

	// 2. Determine if dynamic fMP4 remuxing is required
	audioParam := r.URL.Query().Get("audio")
	startParam := r.URL.Query().Get("start")
	ext := strings.ToLower(filepath.Ext(file.Path()))
	isMKV := ext == ".mkv"

	if audioParam != "" || startParam != "" || isMKV {
		// Verify if ffmpeg is available
		if _, err := exec.LookPath("ffmpeg"); err == nil {
			port := os.Getenv("PORT")
			if port == "" {
				port = "5282"
			}

			// Local loopback URL with "?raw=true" is critical to bypass infinite loop recursion!
			localStreamURL := fmt.Sprintf("http://127.0.0.1:%s/stream/%s/%s?raw=true", port, hashHex, fileIndexStr)

			w.Header().Set("Content-Type", "video/mp4")
			w.Header().Set("Access-Control-Allow-Origin", "*")
			w.Header().Set("Access-Control-Expose-Headers", "Content-Range, Accept-Ranges, Content-Length, Content-Type")

			args := []string{}
			if startParam != "" {
				// Fast seeking before input for instantaneous start
				args = append(args, "-ss", startParam)
			}
			args = append(args, "-i", localStreamURL)
			args = append(args, "-map", "0:v:0")

			if audioParam != "" {
				args = append(args, "-map", fmt.Sprintf("0:%s", audioParam))
			} else {
				args = append(args, "-map", "0:a:0?")
			}

			args = append(args,
				"-c:v", "copy",
				"-c:a", "aac",
				"-f", "mp4",
				"-movflags", "frag_keyframe+empty_moov",
				"-",
			)

			cmd := exec.CommandContext(r.Context(), "ffmpeg", args...)
			cmd.Stdout = w
			cmd.Stderr = nil

			if err := cmd.Start(); err != nil {
				log.Printf("Failed to spawn ffmpeg remuxer: %v", err)
				http.Error(w, "ffmpeg spawn failed", http.StatusInternalServerError)
				return
			}

			if err := cmd.Wait(); err != nil {
				if r.Context().Err() == nil {
					log.Printf("ffmpeg remuxer completed with error: %v", err)
				}
			}
			return
		}
	}

	// 3. Standard direct progressive playback (MP4 or other natively supported formats)
	file.Download()
	reader := file.NewReader()
	defer reader.Close()
	reader.SetReadahead(35 * 1024 * 1024)
	reader.SetResponsive()

	contentType := getMimeType(file.Path())
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Accept-Ranges", "bytes")
	w.Header().Set("Access-Control-Allow-Origin", "*")

	log.Printf("Streaming file: %s (Mime: %s, Size: %d bytes)", file.Path(), contentType, file.Length())
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
type regexItem struct {
	re   *regexp.Regexp
	keys []string
}

var torrentRegexes = []regexItem{
	{regexp.MustCompile(`\bs([0-9]+)\.?ep?([0-9]+)\b`), []string{"season", "episode"}},
	{regexp.MustCompile(`\b([0-9]{1,2})[x-]([0-9]+)\b`), []string{"season", "episode"}},
	{regexp.MustCompile(`\bs([0-9]{2})([0-9]{2,3})\b`), []string{"season", "episode"}},
	{regexp.MustCompile(`season ([0-9]+) episode ([0-9]+)`), []string{"season", "episode"}},
	{regexp.MustCompile(`сезон ([0-9]+) серия ([0-9]+)`), []string{"season", "episode"}},
	{regexp.MustCompile(`([0-9]+) season ([0-9]+) episode`), []string{"season", "episode"}},
	{regexp.MustCompile(`([0-9]+) сезон ([0-9]+) серия`), []string{"season", "episode"}},
	{regexp.MustCompile(`episode ([0-9]+)`), []string{"episode"}},
	{regexp.MustCompile(`серия ([0-9]+)`), []string{"episode"}},
	{regexp.MustCompile(`([0-9]+) episode`), []string{"episode"}},
	{regexp.MustCompile(`([0-9]+) серия`), []string{"episode"}},
	{regexp.MustCompile(`season ([0-9]+)`), []string{"season"}},
	{regexp.MustCompile(`сезон ([0-9]+)`), []string{"season"}},
	{regexp.MustCompile(`([0-9]+) season`), []string{"season"}},
	{regexp.MustCompile(`([0-9]+) сезон`), []string{"season"}},
	{regexp.MustCompile(`\bs([0-9]+)\b`), []string{"season"}},
	{regexp.MustCompile(`\bep?\.?([0-9]+)\b`), []string{"episode"}},
	{regexp.MustCompile(`\b([0-9]{1,3}) of ([0-9]+)`), []string{"episode"}},
	{regexp.MustCompile(`\b([0-9]{1,3}) из ([0-9]+)`), []string{"episode"}},
	{regexp.MustCompile(` - ([0-9]{1,3})\b`), []string{"episode"}},
	{regexp.MustCompile(`\[([0-9]{1,3})\]`), []string{"episode"}},
	{regexp.MustCompile(`([0-9]+) сер`), []string{"episode"}},
}

var folderRegexes = []*regexp.Regexp{
	regexp.MustCompile(`season ([0-9]+)`),
	regexp.MustCompile(`сезон ([0-9]+)`),
	regexp.MustCompile(`([0-9]+) season`),
	regexp.MustCompile(`([0-9]+) сезон`),
	regexp.MustCompile(`\bs([0-9]+)\b`),
}

func parseSeasonAndEpisode(path string) (*int, *int) {
	// First clean path: replace underscores with spaces, similar to Swift client
	cleanPath := strings.ReplaceAll(path, "_", " ")
	
	name := filepath.Base(cleanPath)
	nameLower := strings.ToLower(name)

	var season, episode *int

	// 1. Parse filename using the ordered regexes
	for _, item := range torrentRegexes {
		matches := item.re.FindStringSubmatch(nameLower)
		if len(matches) > 1 {
			for idx, key := range item.keys {
				if idx+1 < len(matches) {
					valStr := matches[idx+1]
					if val, err := strconv.Atoi(valStr); err == nil {
						if key == "season" && season == nil {
							season = &val
						} else if key == "episode" && episode == nil {
							episode = &val
						}
					}
				}
			}
		}
	}

	// 2. Parse folder name if season is still missing
	dir := filepath.Dir(cleanPath)
	if dir != "" && dir != "." && season == nil {
		folderName := filepath.Base(dir)
		folderLower := strings.ToLower(folderName)
		for _, re := range folderRegexes {
			matches := re.FindStringSubmatch(folderLower)
			if len(matches) > 1 {
				if val, err := strconv.Atoi(matches[1]); err == nil {
					season = &val
					break
				}
			}
		}
	}

	// 3. Special case: episode number at the very beginning of filename (e.g. "01.mkv")
	if episode == nil {
		reStartNum := regexp.MustCompile(`^([0-9]{1,3})\b`)
		matches := reStartNum.FindStringSubmatch(nameLower)
		if len(matches) > 1 {
			if val, err := strconv.Atoi(matches[1]); err == nil {
				episode = &val
			}
		}
	}

	// 4. Fallback to finding any standalone number in filename that is not a video resolution
	if episode == nil {
		reNum := regexp.MustCompile(`\b([0-9]{1,3})\b`)
		matches := reNum.FindAllStringSubmatch(nameLower, -1)
		
		var validNums []int
		for _, match := range matches {
			if len(match) > 1 {
				valStr := match[1]
				if valStr == "1080" || valStr == "720" || valStr == "2160" || valStr == "264" || valStr == "265" || valStr == "4k" {
					continue
				}
				if val, err := strconv.Atoi(valStr); err == nil {
					validNums = append(validNums, val)
				}
			}
		}

		if len(validNums) == 1 {
			// If there is only one standalone number, it must be the episode
			episode = &validNums[0]
		} else if len(validNums) > 1 {
			// If there are multiple, try to skip the one that equals the season
			for _, val := range validNums {
				if season != nil && *season == val {
					continue
				}
				episode = &val
				break
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

// ClientTrack represents a streamlined track schema for the frontend player
type ClientTrack struct {
	Index    int    `json:"index"`    // Absolute index inside container
	Type     string `json:"type"`     // "audio" or "subtitle"
	Codec    string `json:"codec"`
	Language string `json:"language"`
	Title    string `json:"title"`
	RelIndex int    `json:"relIndex"` // Stream-relative index (e.g. N-th subtitle stream)
}

// ClientMetadata represents the unified media metadata response
type ClientMetadata struct {
	Success  bool          `json:"success"`
	Duration float64       `json:"duration"`
	Tracks   []ClientTrack `json:"tracks"`
}

// HandleGetMediaMetadata queries the stream structure using ffprobe
func HandleGetMediaMetadata(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	port := os.Getenv("PORT")
	if port == "" {
		port = "5282"
	}

	probeURL := fmt.Sprintf("http://127.0.0.1:%s/stream/%s/%s", port, hashHex, fileIndexStr)

	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Access-Control-Allow-Origin", "*")

	// 1. Verify if ffprobe is installed in PATH
	if _, err := exec.LookPath("ffprobe"); err != nil {
		log.Printf("Warning: ffprobe not found in PATH")
		json.NewEncoder(w).Encode(ClientMetadata{Success: false})
		return
	}

	// 2. Query stream metadata with context timeout to prevent deadlocks
	ctx, cancel := context.WithTimeout(r.Context(), 8*time.Second)
	defer cancel()

	cmd := exec.CommandContext(ctx, "ffprobe",
		"-v", "error",
		"-show_entries", "format=duration",
		"-show_entries", "stream=index,codec_type,codec_name:stream_tags=language,title",
		"-of", "json",
		probeURL,
	)

	var stdoutBuf, stderrBuf bytes.Buffer
	cmd.Stdout = &stdoutBuf
	cmd.Stderr = &stderrBuf

	if err := cmd.Run(); err != nil {
		log.Printf("ffprobe failed: %v, stderr: %s", err, stderrBuf.String())
		http.Error(w, fmt.Sprintf("Probing failed: %v", err), http.StatusGatewayTimeout)
		return
	}

	// 3. Decode ffprobe output
	type FFProbeStream struct {
		Index     int               `json:"index"`
		CodecName string            `json:"codec_name"`
		CodecType string            `json:"codec_type"`
		Tags      map[string]string `json:"tags"`
	}

	type FFProbeResult struct {
		Streams []FFProbeStream `json:"streams"`
		Format  *struct {
			Duration string `json:"duration"`
		} `json:"format"`
	}

	var ffResult FFProbeResult
	if err := json.Unmarshal(stdoutBuf.Bytes(), &ffResult); err != nil {
		http.Error(w, "Failed to parse probe data: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// 4. Map stream formats into clean schema
	duration, _ := strconv.ParseFloat(ffResult.Format.Duration, 64)
	tracks := []ClientTrack{}
	audioCounter := 0
	subCounter := 0

	for _, s := range ffResult.Streams {
		if s.CodecType == "audio" {
			title := ""
			lang := ""
			if s.Tags != nil {
				title = s.Tags["title"]
				lang = s.Tags["language"]
			}
			if title == "" {
				if lang != "" {
					title = fmt.Sprintf("Аудио (%s)", strings.ToUpper(lang))
				} else {
					title = fmt.Sprintf("Аудиодорожка #%d", audioCounter+1)
				}
			}
			tracks = append(tracks, ClientTrack{
				Index:    s.Index,
				Type:     "audio",
				Codec:    s.CodecName,
				Language: lang,
				Title:    title,
				RelIndex: audioCounter,
			})
			audioCounter++
		} else if s.CodecType == "subtitle" {
			title := ""
			lang := ""
			if s.Tags != nil {
				title = s.Tags["title"]
				lang = s.Tags["language"]
			}
			if title == "" {
				if lang != "" {
					title = fmt.Sprintf("Субтитры (%s)", strings.ToUpper(lang))
				} else {
					title = fmt.Sprintf("Субтитры #%d", subCounter+1)
				}
			}
			tracks = append(tracks, ClientTrack{
				Index:    s.Index,
				Type:     "subtitle",
				Codec:    s.CodecName,
				Language: lang,
				Title:    title,
				RelIndex: subCounter,
			})
			subCounter++
		}
	}

	json.NewEncoder(w).Encode(ClientMetadata{
		Success:  true,
		Duration: duration,
		Tracks:   tracks,
	})
}

// HandleGetSubtitles extracts a subtitle track on the fly and streams it as WebVTT
func HandleGetSubtitles(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	trackIndexStr := chi.URLParam(r, "trackIndex")

	port := os.Getenv("PORT")
	if port == "" {
		port = "5282"
	}

	streamURL := fmt.Sprintf("http://127.0.0.1:%s/stream/%s/%s", port, hashHex, fileIndexStr)

	w.Header().Set("Content-Type", "text/vtt; charset=utf-8")
	w.Header().Set("Access-Control-Allow-Origin", "*")

	// 1. Verify if ffmpeg is installed in PATH
	if _, err := exec.LookPath("ffmpeg"); err != nil {
		http.Error(w, "ffmpeg not found in PATH", http.StatusInternalServerError)
		return
	}

	// 2. Stream dynamic subtitle remuxing, binding execution to HTTP request context
	cmd := exec.CommandContext(r.Context(), "ffmpeg",
		"-i", streamURL,
		"-map", fmt.Sprintf("0:s:%s", trackIndexStr),
		"-f", "webvtt",
		"-",
	)

	cmd.Stdout = w
	cmd.Stderr = nil

	if err := cmd.Start(); err != nil {
		log.Printf("Failed to spawn ffmpeg: %v", err)
		http.Error(w, "ffmpeg spawn failed", http.StatusInternalServerError)
		return
	}

	if err := cmd.Wait(); err != nil {
		// Ignore command cancel errors caused by the client closing connection (normal behavior)
		if r.Context().Err() == nil {
			log.Printf("ffmpeg subtitle extraction completed with error: %v", err)
		}
	}
}
