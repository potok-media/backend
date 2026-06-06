package config

import (
	"os"
	"strconv"
	"time"
)

type Config struct {
	Port               int
	LogLevel           string
	AuthUser           string
	AuthPass           string
	ListenPort         int
	ConnsPerTorrent    int
	HalfOpenConns      int
	CacheSizeBytes     int64
	PreloadBytes       int64
	ReadaheadPercent   int
	HLSSegmentDuration int
	HLSMaxSegments     int
	HLSIdleTimeout     time.Duration
	ThumbCacheSize     int
	ThumbCacheTTL      time.Duration
	TorrentIdleTimeout time.Duration
}

func LoadConfig() *Config {
	cacheMB := getEnvInt64("POTOK_CACHE_SIZE_MB", 256)
	preloadMB := getEnvInt64("POTOK_PRELOAD_MB", 20)

	return &Config{
		Port:               getEnvInt("PORT", 5282),
		LogLevel:           getEnvStr("POTOK_LOG_LEVEL", "info"),
		AuthUser:           getEnvStr("POTOK_AUTH_USER", ""),
		AuthPass:           getEnvStr("POTOK_AUTH_PASS", ""),
		ListenPort:         getEnvInt("POTOK_LISTEN_PORT", 55123),
		ConnsPerTorrent:    getEnvInt("POTOK_CONNS_PER_TORRENT", 250),
		HalfOpenConns:      getEnvInt("POTOK_HALF_OPEN_CONNS", 120),
		CacheSizeBytes:     cacheMB * 1024 * 1024,
		PreloadBytes:       preloadMB * 1024 * 1024,
		ReadaheadPercent:   getEnvInt("POTOK_READAHEAD_PERCENT", 50),
		HLSSegmentDuration: getEnvInt("POTOK_HLS_SEGMENT_DURATION", 6),
		HLSMaxSegments:     getEnvInt("POTOK_HLS_MAX_SEGMENTS", 50),
		HLSIdleTimeout:     getEnvDuration("POTOK_HLS_IDLE_TIMEOUT", 30*time.Second),
		ThumbCacheSize:     getEnvInt("POTOK_THUMB_CACHE_SIZE", 200),
		ThumbCacheTTL:      getEnvDuration("POTOK_THUMB_CACHE_TTL", 5*time.Minute),
		TorrentIdleTimeout: getEnvDuration("POTOK_TORRENT_IDLE_TIMEOUT", 30*time.Second),
	}
}

func getEnvStr(key, defaultVal string) string {
	if val, ok := os.LookupEnv(key); ok {
		return val
	}
	return defaultVal
}

func getEnvInt(key string, defaultVal int) int {
	if val, ok := os.LookupEnv(key); ok {
		if parsed, err := strconv.Atoi(val); err == nil {
			return parsed
		}
	}
	return defaultVal
}

func getEnvInt64(key string, defaultVal int64) int64 {
	if val, ok := os.LookupEnv(key); ok {
		if parsed, err := strconv.ParseInt(val, 10, 64); err == nil {
			return parsed
		}
	}
	return defaultVal
}

func getEnvDuration(key string, defaultVal time.Duration) time.Duration {
	if val, ok := os.LookupEnv(key); ok {
		if parsed, err := time.ParseDuration(val); err == nil {
			return parsed
		}
	}
	return defaultVal
}
