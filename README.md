<div align="center">
  <img src="./.github/assets/logo.svg" alt="Potok" width="120" />

  <h1>Potok Backend</h1>

  **English** · [Русский](./README.ru.md)

  ![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-512bd4?logo=dotnet&logoColor=white)
  ![Go](https://img.shields.io/badge/Go-00add8?logo=go&logoColor=white)
  ![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-4169e1?logo=postgresql&logoColor=white)
  ![Image](https://img.shields.io/badge/ghcr.io-potok--media-181717?logo=docker&logoColor=white)
</div>

Server side of the **Potok** media service — three microservices behind a single API:

- **Gateway** (BFF, ASP.NET Core) — client entry point, auth, TMDB/Trakt proxy, orchestration.
- **SearchEngine** (ASP.NET Core) — torrent search across trackers.
- **TorrentGo** (Go) — BitTorrent streaming engine.

Gateway and SearchEngine share one PostgreSQL (separate schemas); TorrentGo is stateless.
Engine addresses are managed by an external plugin, not by env.

## Architecture

```mermaid
flowchart LR
    Client["Web client"] --> GW["potok-gateway<br/>:5000"]
    GW --> SE["potok-searchengine<br/>:6000"]
    GW --> TG["potok-torrentgo<br/>:5282"]
    GW --> DB[("PostgreSQL")]
    SE --> DB
```

## Quick start (Docker)

```bash
cp .env.example .env                              # fill GATEWAY_TMDB_API_KEY and DB credentials
cp src/Potok.Backend.SearchEngine/config.yml .    # SearchEngine config (required) — edit trackers
docker compose up -d --build
```

This brings up all three services and a PostgreSQL instance. PostgreSQL is **required**; to
use an external/shared one, set `DB_HOST` and remove the bundled `db` service from
`docker-compose.yml`.

<details>
<summary><code>docker-compose.yml</code></summary>

```yaml
services:
  # 🌐 API gateway / BFF (Gateway)
  potok-gateway:
    image: ghcr.io/potok-media/potok-gateway:latest
    container_name: potok-gateway
    restart: unless-stopped
    ports:
      - "${GATEWAY_PORT:-5000}:${GATEWAY_PORT:-5000}"
    environment:
      - PORT=${GATEWAY_PORT:-5000}
      # Connection string is assembled from the DB_* parts (single source of truth).
      - ConnectionStrings__DefaultConnection=Host=${DB_HOST:-db};Port=${DB_PORT:-5432};Database=${DB_NAME:-potok};Username=${DB_USER:-potok};Password=${DB_PASSWORD:-potok};Timeout=30;CommandTimeout=60;
      - Gateway__TmdbApiKey=${GATEWAY_TMDB_API_KEY}
      - Gateway__MultiUserMode=${GATEWAY_MULTI_USER_MODE:-false}
    depends_on:
      db:
        condition: service_healthy

  # 🔍 Tracker search engine (SearchEngine)
  potok-searchengine:
    image: ghcr.io/potok-media/potok-searchengine:latest
    container_name: potok-searchengine
    restart: unless-stopped
    ports:
      - "${SEARCH_ENGINE_PORT:-6000}:${SEARCH_ENGINE_PORT:-6000}"
    environment:
      - PORT=${SEARCH_ENGINE_PORT:-6000}
      - ConnectionStrings__DefaultConnection=Host=${DB_HOST:-db};Port=${DB_PORT:-5432};Database=${DB_NAME:-potok};Username=${DB_USER:-potok};Password=${DB_PASSWORD:-potok};Timeout=30;CommandTimeout=60;
    volumes:
      # Mount the tracker config so it can be edited on the host without rebuilding.
      - ./config.yml:/app/config.local.yml
    depends_on:
      db:
        condition: service_healthy

  # 🌊 BitTorrent streaming engine (TorrentGo)
  potok-torrentgo:
    image: ghcr.io/potok-media/potok-torrentgo:latest
    container_name: potok-torrentgo
    restart: unless-stopped
    ports:
      - "${TORRENTGO_PORT:-5282}:${TORRENTGO_PORT:-5282}"
      # Inbound BitTorrent UDP port (DHT / peer listen). Behind NAT/Tailscale without port
      # forwarding, leave it commented out — TorrentGo falls back to outbound-only, which is
      # enough for streaming.
      # - "55123:55123/udp"
    environment:
      - PORT=${TORRENTGO_PORT:-5282}

  # 🗄️ PostgreSQL (bundled — required by Gateway and SearchEngine).
  # To use an external/shared database instead, point DB_HOST at it and remove this service.
  db:
    image: postgres:16-alpine
    container_name: potok-db
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${DB_NAME:-potok}
      POSTGRES_USER: ${DB_USER:-potok}
      POSTGRES_PASSWORD: ${DB_PASSWORD:-potok}
    expose:
      - "5432"
    ports:
      - "${DB_PORT:-5432}:5432"
    volumes:
      - potok-db:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${DB_USER:-potok} -d ${DB_NAME:-potok}"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 30s

volumes:
  potok-db:
    name: potok_db
```

</details>

## Services & ports

| Service | Stack | Default port |
|---|---|---|
| `potok-gateway` | ASP.NET Core | `5000` |
| `potok-searchengine` | ASP.NET Core | `6000` |
| `potok-torrentgo` | Go | `5282` |
| `db` (bundled) | PostgreSQL 16 | `5432` |

## Configuration

Set via `.env`. The DB connection string is assembled in `docker-compose.yml` from the
`DB_*` parts, so there is no separate `DATABASE_URL` to keep in sync.

| Variable | Description | Default |
|---|---|---|
| `GATEWAY_TMDB_API_KEY` | TMDB API key (required) | — |
| `GATEWAY_MULTI_USER_MODE` | Allow self-registration of new users | `false` |
| `DB_HOST` / `DB_PORT` | PostgreSQL host/port (`db` = bundled container) | `db` / `5432` |
| `DB_NAME` / `DB_USER` / `DB_PASSWORD` | Database name and credentials | `potok` / `potok` / — |
| `GATEWAY_PORT` / `SEARCH_ENGINE_PORT` / `TORRENTGO_PORT` | Service ports | `5000` / `6000` / `5282` |

### SearchEngine config (`config.yml`)

SearchEngine also needs its own YAML config — **it won't start without it**. The compose mounts
`./config.yml` (next to `docker-compose.yml`) into the container as `config.local.yml`, so you
can edit it on the host without rebuilding. It configures the server, result merging, cache,
periodic refresh, ffprobe, and the **trackers** (which to search, popularity crawling, and
per-tracker credentials where required).

Grab the sample to start from:

```bash
cp src/Potok.Backend.SearchEngine/config.yml ./config.yml   # then edit trackers/credentials
```

> [!NOTE]
> Behind NAT/Tailscale without port forwarding, leave TorrentGo's inbound UDP port commented
> out — it falls back to outbound-only, which is enough for streaming.

## Part of Potok

The backend powers the **Potok** ecosystem:

- ⚙️ **Backend** — this repository (Gateway · SearchEngine · TorrentGo)
- 🌐 **Web** — client
- 🧩 **Plugins & SDK** — extend clients via `PotokSDK`

🔗 [Live](https://potok.rip) · [Wiki](https://potok.rip/wiki) · [GitHub](https://github.com/potok-media)
