# Build stage
FROM golang:1.23-alpine AS builder

WORKDIR /app

# Install git and ca-certificates
RUN apk add --no-cache git ca-certificates

# Copy go modules manifests and download dependencies
COPY go.mod go.sum ./
RUN go mod download

# Copy the rest of the application source code
COPY . .

# Build the application as a static binary
RUN CGO_ENABLED=0 GOOS=linux go build -ldflags="-w -s" -o potok-torrent-go .

# Final runtime stage
FROM alpine:latest

# Install ca-certificates, tzdata, and ffmpeg (includes ffprobe) for media processing
RUN apk add --no-cache ca-certificates tzdata ffmpeg

WORKDIR /app

# Copy the compiled binary from the builder stage
COPY --from=builder /app/potok-torrent-go .

# Default HTTP server port
EXPOSE 5282

# BitTorrent peer listen port (UDP)
EXPOSE 55123/udp

# Mount volume for downloading torrents and keeping cache
VOLUME ["/app/torrent-cache"]

# Run the application
CMD ["./potok-torrent-go"]
