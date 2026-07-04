# Build stage
FROM golang:1.23-alpine AS builder

WORKDIR /app

RUN apk add --no-cache git ca-certificates

COPY go.mod go.sum ./
RUN go mod download

COPY . .

RUN CGO_ENABLED=0 GOOS=linux go build -ldflags="-w -s" -o potok-torrent-go .

# Runtime stage — Debian (glibc) so jellyfin-ffmpeg installs. jellyfin-ffmpeg bundles the codecs and
# GPU drivers (VAAPI/QSV/NVENC) needed for hardware transcoding; TorrentGo probes them at startup and
# falls back to software x264 when no GPU is passed in. Falls back to distro ffmpeg where the
# jellyfin package isn't published for the target arch.
FROM debian:bookworm-slim
ARG TARGETARCH

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates tzdata gnupg curl; \
    mkdir -p /etc/apt/keyrings; \
    curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key | gpg --dearmor -o /etc/apt/keyrings/jellyfin.gpg; \
    printf 'Types: deb\nURIs: https://repo.jellyfin.org/debian\nSuites: bookworm\nComponents: main\nArchitectures: %s\nSigned-By: /etc/apt/keyrings/jellyfin.gpg\n' "${TARGETARCH:-amd64}" > /etc/apt/sources.list.d/jellyfin.sources; \
    apt-get update; \
    apt-get install -y --no-install-recommends jellyfin-ffmpeg7 || apt-get install -y --no-install-recommends ffmpeg; \
    apt-get purge -y --auto-remove gnupg curl; \
    rm -rf /var/lib/apt/lists/*

ENV FFMPEG_PATH=/usr/lib/jellyfin-ffmpeg/ffmpeg

WORKDIR /app

COPY --from=builder /app/potok-torrent-go .

EXPOSE 5282
EXPOSE 55123/udp

VOLUME ["/app/torrent-cache"]

CMD ["./potok-torrent-go"]
