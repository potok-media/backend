package handlers

import (
	"encoding/json"
	"fmt"
	"log/slog"
	"math"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/go-chi/chi/v5"
)

// HLS delivery, multivariant ("HLS4"). `master.m3u8` is a MULTIVARIANT playlist: one VIDEO rendition
// (`v/…`, audio-agnostic) referenced by an EXT-X-STREAM-INF, plus per-track AUDIO and SUBTITLES renditions
// as EXT-X-MEDIA (`a/{rel}/…`, `s/{rel}/…`). hls.js switches audio/subtitles NATIVELY (separate SourceBuffers)
// — no source reload, no video re-work, no muxed-fragment seam. Every rendition shares the same VOD segment
// grid (hlsindex.go) so timelines align. Segments + init are produced on demand, in-process, by media/
// (libav via go-astiav over the RAM torrent cache); only the video (once, shared) and the actually-loaded
// audio track are ever transcoded. fMP4 for A/V (.m4s + shared init.mp4 via EXT-X-MAP); WebVTT (.vtt) for subs.

func (h *HandlerContext) HandleHls(w http.ResponseWriter, r *http.Request) {
	// The `/hls/*` wildcard captures the rendition sub-path: master.m3u8 | v/… | a/{rel}/… | s/{rel}/…
	rest := chi.URLParam(r, "*")
	// [HLS4-DIAG] playlist requests (segments are logged in serveProduced). Remove after diagnosis.
	if strings.HasSuffix(rest, ".m3u8") {
		slog.Info("[HLS4-DIAG] playlist", "path", rest)
	}
	parts := strings.Split(rest, "/")
	switch {
	case rest == "master.m3u8":
		h.serveMasterPlaylist(w, r)
		return
	case len(parts) == 2 && parts[0] == "v":
		h.serveVideoRendition(w, r, parts[1])
		return
	case len(parts) == 3 && parts[0] == "a":
		if rel, err := strconv.Atoi(parts[1]); err == nil {
			h.serveAudioRendition(w, r, rel, parts[2])
			return
		}
	case len(parts) == 3 && parts[0] == "s":
		if rel, err := strconv.Atoi(parts[1]); err == nil {
			h.serveSubRendition(w, r, rel, parts[2])
			return
		}
	}
	http.NotFound(w, r)
}

// serveMasterPlaylist emits the multivariant master: EXT-X-MEDIA per audio/subtitle track + one video variant.
func (h *HandlerContext) serveMasterPlaylist(w http.ResponseWriter, r *http.Request) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")

	metaBytes, err := h.probeAndCacheMetadata(r.Context(), hashHex, fileIndexStr)
	if err != nil {
		if r.Context().Err() != nil {
			return
		}
		slog.Error("hls master: metadata failed", "hash", hashHex, "file", fileIndexStr, "error", err)
		http.Error(w, "hls unavailable", http.StatusInternalServerError)
		return
	}
	var meta ClientMetadata
	if err := json.Unmarshal(metaBytes, &meta); err != nil {
		http.Error(w, "hls unavailable", http.StatusInternalServerError)
		return
	}

	var b strings.Builder
	b.WriteString("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n")

	audioCount := 0
	for _, t := range meta.Tracks {
		if t.Type != "audio" {
			continue
		}
		def := "NO"
		if audioCount == 0 {
			def = "YES" // hls.js loads the DEFAULT rendition first; others are fetched only on switch
		}
		fmt.Fprintf(&b, "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"%s\",LANGUAGE=\"%s\",DEFAULT=%s,AUTOSELECT=YES,URI=\"a/%d/index.m3u8\"\n",
			m3uEscape(trackName(t, "Audio")), t.Language, def, t.RelIndex)
		audioCount++
	}

	hasSubs := false
	for _, t := range meta.Tracks {
		if t.Type != "subtitle" || !isTextSubtitleCodec(t.Codec) {
			continue
		}
		hasSubs = true
		fmt.Fprintf(&b, "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"%s\",LANGUAGE=\"%s\",DEFAULT=NO,AUTOSELECT=NO,FORCED=NO,URI=\"s/%d/index.m3u8\"\n",
			m3uEscape(trackName(t, "Subtitle")), t.Language, t.RelIndex)
	}

	// Single video variant. CODECS is a broadly-compatible default (H.264 High@4.1 + AAC-LC); hls.js only
	// needs it to pass MediaSource.isTypeSupported. Refine to the real profile/level later if needed.
	inf := "#EXT-X-STREAM-INF:BANDWIDTH=8000000,CODECS=\"avc1.640029,mp4a.40.2\""
	if audioCount > 0 {
		inf += ",AUDIO=\"aud\""
	}
	if hasSubs {
		inf += ",SUBTITLES=\"subs\""
	}
	b.WriteString(inf + "\nv/index.m3u8\n")

	writeM3U8(w, []byte(b.String()))
}

// serveVideoRendition serves the audio-agnostic video rendition: index.m3u8 | init.mp4 | seg{N}.m4s.
func (h *HandlerContext) serveVideoRendition(w http.ResponseWriter, r *http.Request, leaf string) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	sl, err := h.getSegList(r.Context(), hashHex, fileIndexStr)
	if err != nil {
		http.Error(w, "hls unavailable", http.StatusInternalServerError)
		return
	}
	switch {
	case leaf == "index.m3u8":
		writeM3U8(w, renderMediaPlaylist(sl, "init.mp4", "m4s"))
	case leaf == "init.mp4":
		h.serveProduced(w, r, hashHex+"_"+fileIndexStr+"_v_init", "video/mp4", func() ([]byte, error) {
			return h.produceVideoInit(r.Context(), hashHex, fileIndexStr)
		})
	default:
		if n, ok := parseSeg(leaf, "m4s"); ok && n >= 0 && n < sl.count() {
			h.serveProduced(w, r, fmt.Sprintf("%s_%s_v_%d", hashHex, fileIndexStr, n), "video/mp4", func() ([]byte, error) {
				return h.produceVideoSegment(r.Context(), hashHex, fileIndexStr, sl, n)
			})
			return
		}
		http.NotFound(w, r)
	}
}

// serveAudioRendition serves one audio track's rendition: index.m3u8 | init.mp4 | seg{N}.m4s.
func (h *HandlerContext) serveAudioRendition(w http.ResponseWriter, r *http.Request, rel int, leaf string) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	sl, err := h.getSegList(r.Context(), hashHex, fileIndexStr)
	if err != nil {
		http.Error(w, "hls unavailable", http.StatusInternalServerError)
		return
	}
	switch {
	case leaf == "index.m3u8":
		writeM3U8(w, renderMediaPlaylist(sl, "init.mp4", "m4s"))
	case leaf == "init.mp4":
		h.serveProduced(w, r, fmt.Sprintf("%s_%s_a%d_init", hashHex, fileIndexStr, rel), "video/mp4", func() ([]byte, error) {
			return h.produceAudioInit(r.Context(), hashHex, fileIndexStr, rel)
		})
	default:
		if n, ok := parseSeg(leaf, "m4s"); ok && n >= 0 && n < sl.count() {
			h.serveProduced(w, r, fmt.Sprintf("%s_%s_a%d_%d", hashHex, fileIndexStr, rel, n), "video/mp4", func() ([]byte, error) {
				return h.produceAudioSegment(r.Context(), hashHex, fileIndexStr, rel, sl, n)
			})
			return
		}
		http.NotFound(w, r)
	}
}

// serveSubRendition serves one subtitle track's rendition: index.m3u8 | seg{N}.vtt (WebVTT, no init).
func (h *HandlerContext) serveSubRendition(w http.ResponseWriter, r *http.Request, rel int, leaf string) {
	hashHex := chi.URLParam(r, "hash")
	fileIndexStr := chi.URLParam(r, "fileIndex")
	sl, err := h.getSegList(r.Context(), hashHex, fileIndexStr)
	if err != nil {
		http.Error(w, "hls unavailable", http.StatusInternalServerError)
		return
	}
	switch {
	case leaf == "index.m3u8":
		writeM3U8(w, renderMediaPlaylist(sl, "", "vtt"))
	default:
		if n, ok := parseSeg(leaf, "vtt"); ok && n >= 0 && n < sl.count() {
			h.serveProduced(w, r, fmt.Sprintf("%s_%s_s%d_%d", hashHex, fileIndexStr, rel, n), "text/vtt; charset=utf-8", func() ([]byte, error) {
				return h.produceSubSegment(r.Context(), hashHex, fileIndexStr, rel, sl, n)
			})
			return
		}
		http.NotFound(w, r)
	}
}

// renderMediaPlaylist builds a VOD media playlist over the shared grid. mapURI=="" → no EXT-X-MAP (subs);
// segExt is "m4s" (fMP4) or "vtt" (WebVTT).
func renderMediaPlaylist(sl *segList, mapURI, segExt string) []byte {
	maxDur := 0.0
	for i := 0; i < sl.count(); i++ {
		if d := sl.extinf(i); d > maxDur {
			maxDur = d
		}
	}
	var b strings.Builder
	b.WriteString("#EXTM3U\n#EXT-X-VERSION:7\n")
	fmt.Fprintf(&b, "#EXT-X-TARGETDURATION:%d\n", int(math.Ceil(maxDur)))
	b.WriteString("#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n")
	if mapURI != "" {
		fmt.Fprintf(&b, "#EXT-X-MAP:URI=\"%s\"\n", mapURI)
	}
	for i := 0; i < sl.count(); i++ {
		fmt.Fprintf(&b, "#EXTINF:%.6f,\nseg%d.%s\n", sl.extinf(i), i, segExt)
	}
	b.WriteString("#EXT-X-ENDLIST\n")
	return []byte(b.String())
}

// serveProduced serves a cached-or-produced binary/text artifact (segment, init, or vtt). The [HLS4-DIAG]
// line reports, per request, which rendition+segment it is (the cacheKey encodes _v_/_a{rel}_/_s{rel}_ +
// index), whether it was a cache hit, and how long a miss took to produce — so out-of-order/pending network
// requests become readable (a slow `_v_386` produce vs an instant `_a0_400` hit). Remove after diagnosis.
func (h *HandlerContext) serveProduced(w http.ResponseWriter, r *http.Request, cacheKey, contentType string, produce func() ([]byte, error)) {
	if data, ok := h.hlsSegCache.get(cacheKey); ok {
		slog.Info("[HLS4-DIAG] serve", "key", cacheKey, "cache", "hit", "bytes", len(data))
		writeSeg(w, contentType, data)
		return
	}
	t0 := time.Now()
	data, err := produce()
	if err != nil {
		if r.Context().Err() != nil {
			slog.Info("[HLS4-DIAG] produce canceled (client gone)", "key", cacheKey, "ms", time.Since(t0).Milliseconds())
			return
		}
		slog.Error("[HLS4-DIAG] produce FAILED", "key", cacheKey, "ms", time.Since(t0).Milliseconds(), "error", err)
		http.Error(w, "segment failed", http.StatusInternalServerError)
		return
	}
	h.hlsSegCache.put(cacheKey, data)
	slog.Info("[HLS4-DIAG] serve", "key", cacheKey, "cache", "miss", "bytes", len(data), "produceMs", time.Since(t0).Milliseconds())
	writeSeg(w, contentType, data)
}

func writeSeg(w http.ResponseWriter, contentType string, data []byte) {
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "public, max-age=86400, immutable")
	_, _ = w.Write(data)
}

func writeM3U8(w http.ResponseWriter, body []byte) {
	w.Header().Set("Content-Type", "application/vnd.apple.mpegurl")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "public, max-age=3600")
	_, _ = w.Write(body)
}

// parseSeg parses "seg{N}.{ext}" → N.
func parseSeg(leaf, ext string) (int, bool) {
	if !strings.HasPrefix(leaf, "seg") || !strings.HasSuffix(leaf, "."+ext) {
		return 0, false
	}
	n, err := strconv.Atoi(strings.TrimSuffix(strings.TrimPrefix(leaf, "seg"), "."+ext))
	if err != nil {
		return 0, false
	}
	return n, true
}

// trackName picks a display NAME for an EXT-X-MEDIA rendition: the backend title, else language, else a
// numbered fallback (e.g. "Audio 1"). The label now lives in the manifest (hls.js exposes it natively).
func trackName(t ClientTrack, kind string) string {
	if s := strings.TrimSpace(t.Title); s != "" {
		return s
	}
	if s := strings.TrimSpace(t.Language); s != "" {
		return s
	}
	return fmt.Sprintf("%s %d", kind, t.RelIndex+1)
}

// m3uEscape neutralises the one character that would break a quoted attribute value.
func m3uEscape(s string) string { return strings.ReplaceAll(s, "\"", "'") }
