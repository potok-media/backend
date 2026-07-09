<div align="center">
  <img src="./.github/assets/logo.svg" alt="Potok" width="120" />

  <h1>Potok Backend</h1>

  **English** · [Русский](./README.ru.md)

  ![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-512bd4?logo=dotnet&logoColor=white)
  ![Go](https://img.shields.io/badge/Go-00add8?logo=go&logoColor=white)
  ![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-4169e1?logo=postgresql&logoColor=white)
  ![Image](https://img.shields.io/badge/ghcr.io-potok--media-181717?logo=docker&logoColor=white)
</div>

Server side of the **Potok** media service — three deployable services:

- **Gateway** (BFF, ASP.NET Core) — client entry point: auth, sync, media (TMDB/Trakt), plugin bundler sidecar, CORS proxy for plugins.
- **SearchEngine** (ASP.NET Core) — torrent search and overrides (for `torrents-plugin`).
- **TorrentGo** (Go) — BitTorrent streaming engine (for `torrents-plugin`).

Gateway and SearchEngine share one PostgreSQL (separate schemas); TorrentGo is stateless.
The client talks to Gateway via `gatewayURL`; the torrents plugin calls SearchEngine and TorrentGo directly.

## Architecture

```mermaid
flowchart LR
    Client["Web client"] --> GW["potok-gateway<br/>:5000"]
    Plugin["torrents-plugin"] --> SE["potok-searchengine<br/>:6000"]
    Plugin --> TG["potok-torrentgo<br/>:5282"]
    GW --> DB[("PostgreSQL")]
    SE --> DB
```

## Quick start (Docker)

Create `docker-compose.yml`, `.env`, and (for torrents) `config.yml` in one folder — see [wiki install guide](https://potok.rip/wiki). Then:

```bash
docker compose up -d --build
```

Minimal `.env`:

```env
GATEWAY_PORT=5000
GATEWAY_TMDB_API_KEY=your_tmdb_key
GATEWAY_JWT_SECRET=change-me-in-production-32chars-min
DB_HOST=db
DB_PASSWORD=changeme
SEARCH_ENGINE_PORT=6000
TORRENTGO_PORT=5282
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
      - Gateway__JwtSecret=${GATEWAY_JWT_SECRET:-default-fallback-gateway-jwt-secret-key-32-chars-long}
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

| Variable | Maps to / used by | Description | Default |
|---|---|---|---|
| `GATEWAY_TMDB_API_KEY` | `Gateway__TmdbApiKey` | TMDB API key (**required**) | — |
| `GATEWAY_MULTI_USER_MODE` | `Gateway__MultiUserMode` | Allow self-registration | `false` |
| `GATEWAY_JWT_SECRET` | `Gateway__JwtSecret` | JWT signing secret (change in production) | change in production |
| `DB_HOST` / `DB_PORT` | connection string | PostgreSQL host/port (`db` = bundled) | `db` / `5432` |
| `DB_NAME` / `DB_USER` / `DB_PASSWORD` | connection string + `db` service | Database credentials | `potok` / `potok` / — |
| `GATEWAY_PORT` | `PORT` in gateway | Host publish port | `5000` |
| `SEARCH_ENGINE_PORT` | `PORT` in searchengine | Host publish port; set plugin `searchEngineURL` to match | `6000` |
| `TORRENTGO_PORT` | `PORT` in torrentgo | Host publish port; set plugin `torrentGoURL` to match | `5282` |
| `GPU_DEVICE` | compose `devices:` | GPU passthrough for TorrentGo (**not** a process env var) | `/dev/null` noop |
| `POTOK_DISABLE_HWACCEL` | TorrentGo env (add manually) | Force software transcoding when set to `1` | off |

**Not configured via `.env`:** SearchEngine tracker credentials — use `config.yml` (mounted as `config.local.yml`). SearchEngine listen port is `PORT` / `SEARCH_ENGINE_PORT`, not `config.yml`. Plugin URLs (`searchEngineURL`, `torrentGoURL`) are set in the potok-torrents plugin, not in Gateway env.

### SearchEngine config (`config.yml`)

SearchEngine needs `./config.yml` next to `docker-compose.yml` (mounted as `config.local.yml`).
Create it on the host and fill in trackers — no copy step from the repo.

Full guide: [SearchEngine & TorrentGo](https://potok.rip/wiki) (wiki sidebar). Sample structure:
[`src/Potok.Backend.SearchEngine/config.yml`](src/Potok.Backend.SearchEngine/config.yml).

> [!NOTE]
> Behind NAT/Tailscale without port forwarding, leave TorrentGo's inbound UDP port commented
> out — it falls back to outbound-only, which is enough for streaming.

## Part of Potok

The backend powers the **Potok** ecosystem:

- ⚙️ **Backend** — this repository (Gateway · SearchEngine · TorrentGo)
- 🌐 **Web** — client
- 🧩 **Plugins & SDK** — extend clients via `PotokSDK`

🔗 [Live](https://potok.rip) · [Wiki](https://potok.rip/wiki) · [GitHub](https://github.com/potok-media)
