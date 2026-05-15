# PuckTracker

Standalone server population tracker for [Puck](https://store.steampowered.com/app/2994020/Puck/). Polls the game's master servers every 15 minutes, collects player counts, and stores historical data in PostgreSQL.

No game client or Steam client required - authenticates directly via [SteamKit2](https://github.com/SteamRE/SteamKit).

## What it tracks

- All public servers on b323 (current game build)
- Player counts, max players, password status
- Server names (with change history)
- GeoIP location and ISP (via MaxMind GeoLite2)
- Required client mods

## Requirements

- .NET 9
- PostgreSQL database with `psl_` tables (see migration in your frontend repo)
- Steam account that owns Puck (free-to-play)
- MaxMind GeoLite2 databases (optional, for location/ISP data)

## Usage

```bash
# Set database connection
echo "DATABASE_URL=postgresql://user:pass@host:port/db" > .env

# Single scan
dotnet run -- --once

# Continuous (every 15 minutes)
dotnet run
```

First run prompts for Steam credentials interactively. A refresh token is saved to `steam_credentials.json` for subsequent runs.

## How it works

1. Authenticates to Steam via SteamKit2
2. Connects to `wss://puck2.nasejevs.com` (b323 master server) via Socket.IO
3. Authenticates with a Steam web API ticket
4. Requests the endpoint list, then queries each server directly via TCP for the preview payload
5. Enriches with GeoIP data
6. Writes to PostgreSQL (`psl_servers`, `psl_server_names`, `psl_snapshots`, `psl_server_snapshots`)

Maintains persistent WebSocket connections and reconnects automatically if dropped.

## Deployment

See [DEPLOY-LINUX.md](DEPLOY-LINUX.md) for Ubuntu VPS setup with systemd.

## Related

Frontend server list UI lives in the [puckstats](https://github.com/ckhawks/puckstats) repo at `/app/server-list/`.
