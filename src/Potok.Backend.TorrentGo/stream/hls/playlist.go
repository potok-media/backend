package hls

import (
	"fmt"
	"net/url"
)

func GenerateMasterPlaylist(audio, start string) string {
	params := url.Values{}
	if audio != "" {
		params.Set("audio", audio)
	}
	if start != "" {
		params.Set("start", start)
	}

	query := ""
	if len(params) > 0 {
		query = "?" + params.Encode()
	}

	return fmt.Sprintf(`#EXTM3U
#EXT-X-VERSION:3
#EXT-X-STREAM-INF:BANDWIDTH=10000000,RESOLUTION=1920x1080
stream.m3u8%s
`, query)
}
