# NzbDavEx

NzbDavEx is a WebDAV server that presents NZB documents as a virtual filesystem. It exposes standard filesystem semantics (browse, read, and random-access seek) over HTTP(S) without requiring full downloads to local disk, and ships a SABnzbd-compatible HTTP API for integration with compatible automation tools.

This is an extended fork of [nzbdav](https://github.com/nzbdav-dev/nzbdav) with additional functionality layered on top: a Watchdog module for unattended file verification, multi-provider NNTP support with per-provider usage accounting, an indexers manager with strict-match filtering, search profiles exposing token-scoped search-API endpoints to external clients, and an updated settings UI.

## Features

- **WebDAV server**: serves a virtual filesystem over HTTP(S)
- **NZB virtualisation**: browse and read NZB-described content without downloading
- **Random-access reads**: seek support, including within RAR/7z and password-protected archives
- **SABnzbd-compatible API**: interoperable with applications that speak the SABnzbd HTTP protocol
- **Multi-provider NNTP**: failover between providers and per-provider usage tracking
- **Indexers manager**: configure indexer sources from the UI
- **Watchdog**: automated verification and re-fetch workflows driven by user-defined rules
- **Search Profiles**: token-scoped search-API endpoints with per-profile indexer selection, consumable by any compatible external client
- **Health checks & repairs**: detect and replace content no longer available at the source

## Getting Started

A pre-built image is published to the GitHub Container Registry. Pull and run it directly:

```bash
docker run --rm -it -p 3000:3000 ghcr.io/qooode/nzbdavex:edge
```

To persist settings, mount a volume at `/config`:

```bash
mkdir -p $(pwd)/data/nzbdavex && \
docker run --rm -it \
  -v $(pwd)/data/nzbdavex:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -p 3000:3000 \
  ghcr.io/qooode/nzbdavex:edge
```

To update, pull the latest image:

```bash
docker pull ghcr.io/qooode/nzbdavex:edge
```

Once running, open the UI on port `3000` and head to **Settings** to configure your NNTP providers and WebDAV credentials.

### Docker Compose

```yaml
services:
  nzbdavex:
    image: ghcr.io/qooode/nzbdavex:edge
    container_name: nzbdavex
    restart: unless-stopped
    healthcheck:
      test: curl -f http://localhost:3000/health || exit 1
      # Check every 1 minute
      interval: 1m
      # If it fails 3 times (3 minutes total), restart it
      retries: 3
      # Give it 5 seconds to boot up
      start_period: 5s
      # If it doesn't answer in 5 seconds, assume it's frozen
      timeout: 5s
    ports:
      - "3000:3000"
    environment:
      # Change these IDs to match your Docker user that you got from above
      - PUID=1000
      - PGID=1000
    volumes:
      - ./data/nzbdavex:/config
      - /mnt:/mnt
```

## Setup Guide

For a more detailed walk-through covering Docker Compose, Rclone sidecar mounting, performance tuning, and integrations, see [docs/setup-guide.md](docs/setup-guide.md).

## License

See [LICENSE](LICENSE).

## Disclaimer

NzbDavEx is a general-purpose WebDAV server and file-mounting utility. It is provided **as-is**, without warranty of any kind, express or implied. The software does not host, distribute, or index any content; it only connects to user-supplied third-party services using credentials supplied by the user.

Users are solely responsible for:

- Ensuring that their use of this software, and of any third-party services configured with it, complies with all applicable laws and regulations in their jurisdiction;
- Complying with the terms of service of any provider they connect to;
- The content they choose to access, store, or transmit through this software.

The authors and contributors accept no liability for any use of this software.
