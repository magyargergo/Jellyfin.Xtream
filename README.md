# Jellyfin.Xtream

![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/Kevinjil/Jellyfin.Xtream/total)
![GitHub Downloads (all assets, latest release)](https://img.shields.io/github/downloads/Kevinjil/Jellyfin.Xtream/latest/total)
![GitHub commits since latest release](https://img.shields.io/github/commits-since/Kevinjil/Jellyfin.Xtream/latest)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2FKevinjil%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=targetAbi&label=Jellyfin%20ABI)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2FKevinjil%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=framework&label=.NET%20framework)

The Jellyfin.Xtream plugin integrates content from [Xtream-compatible APIs](https://xtream-ui.org/api-xtreamui-xtreamcode/) into your [Jellyfin](https://jellyfin.org/) instance.

## Acknowledgments

This plugin was originally created by [Kevinjil](https://github.com/Kevinjil). Thank you for the excellent foundation and continued work on this project!

## Features

- **Multi-Provider Support**: Configure multiple Xtream providers with automatic failover and quality-based channel deduplication
- **Live TV, VOD, Series & Catch-up**: Full support for all Xtream content types
- **Smart Restreaming**: Single connection to provider with multi-client broadcasting (solves HTTP 406 errors)
- **EPG Support**: Multiple EPG providers with fallback chain and external XMLTV support
- **Stream Quality Monitoring**: Real-time MPEG-TS quality analysis with TR 101 290 compliance
- **Discord Notifications**: Stream health alerts, EPG refresh status, and periodic health reports
- **Proxy Support**: Route traffic through HTTP proxy with authentication
- **User-Agent Rotation**: Rotate through realistic browser User-Agents to avoid WAF blocking
- **Rate Limiting**: Token bucket rate limiting to prevent provider bans
- **Connection Management**: State tracking, connection limits, and auto-kill of oldest streams

## Installation

The plugin can be installed using a custom plugin repository.

### Add Repository

1. Open your admin dashboard and navigate to `Plugins`.
2. Select the `Repositories` tab on the top of the page.
3. Click the `+` symbol to add a repository.
4. Enter `Jellyfin.Xtream` as the repository name.
5. Enter [`https://kevinjil.github.io/Jellyfin.Xtream/repository.json`](https://kevinjil.github.io/Jellyfin.Xtream/repository.json) as the repository URL.
6. Click save.

### Install Plugin

1. Open your admin dashboard and navigate to `Plugins`.
2. Select the `Catalog` tab on the top of the page.
3. Under `Live TV`, select `Jellyfin Xtream`.
4. (Optional) Select the desired plugin version.
5. Click `Install`.
6. Restart your Jellyfin server to complete the installation.

## Configuration

### Provider Setup

Configure one or more Xtream-compatible providers in the plugin settings.

| Property | Description |
|----------|-------------|
| Base URL | The API endpoint URL (excluding trailing slash), including protocol (http/https) |
| Username | The username for API authentication |
| Password | The password for API authentication |

### Content Selection

#### Live TV

1. Open the `Live TV` configuration tab.
2. Select the categories or individual channels you want available.
3. Click `Save`.
4. (Optional) Open `TV Overrides` to customize channel numbers, names, and icons.

#### Video On-Demand

1. Open the `Video On-Demand` configuration tab.
2. Enable `Show this channel to users`.
3. Select the categories or individual videos.
4. Click `Save`.

#### Series

1. Open the `Series` configuration tab.
2. Enable `Show this channel to users`.
3. Select the categories or individual series.
4. Click `Save`.

#### TV Catch-up

1. Open the `Live TV` configuration tab.
2. Enable `Show the catch-up channel to users`.
3. Click `Save`.

### Advanced Configuration

#### Multi-Provider Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Merge Duplicate Channels | Combine same-name channels from different providers | Off |
| Enable Provider Failover | Automatically switch to backup provider on failure | Off |
| Max Failover Attempts | Maximum retry attempts before giving up | 3 |

#### Proxy Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Proxy | Route traffic through HTTP proxy | Off |
| Proxy Address | Proxy server hostname or IP | - |
| Proxy Port | Proxy server port | 8080 |
| Proxy Username | Optional proxy authentication | - |
| Proxy Password | Optional proxy authentication | - |
| Bypass Local | Skip proxy for local addresses | On |

#### User-Agent Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Custom User-Agent | Override with specific User-Agent string | - |
| Enable Rotation | Rotate through browser User-Agents | Off |
| Random Selection | Use random (weighted by market share) vs sequential | On |

#### Rate Limiting

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Rate Limiting | Limit API requests to prevent bans | Off |
| Requests Per Second | Maximum requests per second | 5 |
| Burst Size | Initial burst allowance | 20 |

#### Connection Limits

| Setting | Description | Default |
|---------|-------------|---------|
| Enforce Connection Limit | Limit concurrent streams | Off |
| Max Concurrent Streams | Maximum active streams (0 = use provider limit) | 0 |
| Auto-Kill Oldest | Terminate oldest stream when limit reached | On |

#### EPG Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable External EPG | Use external XMLTV source as fallback | Off |
| External EPG URL | XMLTV source URL | - |
| Logo Base URL | Base URL for logo fallback | - |
| Use Logo Fallback | Fall back to external logos when missing | On |

#### Discord Notifications

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Notifications | Send Discord webhook notifications | Off |
| Webhook URL | Discord webhook endpoint | - |
| Notify on Buffer Overflow | Alert on restream buffer issues | On |
| Notify on Stream Error | Alert on stream failures | On |
| Notify on Stream Killed | Alert when streams are terminated | On |
| Notify on Quality Violation | Alert on TR 101 290 violations | On |
| Notify on A/V Drift | Alert on audio/video sync issues | On |
| Notify on EPG Refresh | Alert on EPG refresh completion | On |
| Periodic Health Reports | Send regular health summaries | Off |
| Health Report Interval | Minutes between health reports | 60 |

## Technical Details

### Restreaming Architecture

The plugin maintains a single HTTP connection to each provider stream and broadcasts to multiple Jellyfin clients using an optimized circular buffer. This solves the common HTTP 406 error caused by providers limiting simultaneous connections.

**Buffer sizes by quality:**
- SD streams: 32 MB
- HD streams: 64 MB
- 4K/UHD streams: 128 MB

### Stream Quality Monitoring

Real-time MPEG-TS analysis following TR 101 290 standards:
- Transport Error Indicator (TEI) detection
- Continuity Counter discontinuities
- PCR jitter monitoring
- PAT/PMT version changes
- Audio/Video synchronization tracking

### EPG Provider Chain

EPG data is fetched using a priority-based fallback system:
1. Xtream API (`get_short_epg` / `get_simple_data_table`)
2. Local XMLTV file
3. External XMLTV source (e.g., epg.ovh)

## Known Issues

### Loss of Confidentiality

Jellyfin publishes remote paths in the API and default user interface. As Xtream format paths include credentials, anyone with library access can see your provider credentials. Use this plugin with caution on shared servers.

## Troubleshooting

### Network Configuration

Ensure your [Jellyfin networking](https://jellyfin.org/docs/general/networking/) is correctly configured:

1. Open your admin dashboard and navigate to `Networking`.
2. Configure your `Published server URIs` correctly.
   Example: `all=https://jellyfin.example.com`

### Debug Logging

Enable `Debug Logging` in the plugin settings for verbose diagnostics when troubleshooting issues.

## License

This project is licensed under the GNU General Public License v3.0 - see the [LICENSE](LICENSE) file for details.
