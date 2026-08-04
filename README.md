# PuckTracker

Standalone server population tracker for [Puck](https://store.steampowered.com/app/2994020/Puck/). Polls the game's master servers every 15 minutes, collects player counts, and stores historical data in PostgreSQL.

No game client or Steam client required - authenticates directly via [SteamKit2](https://github.com/SteamRE/SteamKit).

## What it tracks

- All public servers on b1153 (current game build)
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

# Optional: deliver mod alerts to Discord (see Mod alerts below)
echo "DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/..." >> .env

# Single scan
dotnet run -- --once

# Continuous (every 15 minutes)
dotnet run
```

First run prompts for Steam credentials interactively. A refresh token is saved to `steam_credentials.json` for subsequent runs.

## How it works

1. Authenticates to Steam via SteamKit2
2. Connects to `wss://puck2.nasejevs.com` (b1153 master server) via Socket.IO
3. Authenticates with a Steam web API ticket
4. Requests the endpoint list, then queries each server directly via TCP for the preview payload
5. Enriches with GeoIP data
6. Writes to PostgreSQL (`psl_servers`, `psl_server_names`, `psl_snapshots`, `psl_server_snapshots`)

Maintains persistent WebSocket connections and reconnects automatically if dropped.

## Mod alerts

Every scan resolves the Workshop IDs servers require into `psl_mods` (title, creator,
`time_updated`, subscriptions, ban status). The cache refreshes every 24 hours, so a
change to an already-deployed mod is noticed within a day.

**Only mods listed in `psl_mod_watch` raise alerts.** Servers add and drop mods constantly,
so alerting on everything buries the handful worth reading. Every mod is still cached and
tracked in `psl_mods` regardless -- the watchlist gates the alert, not the data.

Five things raise an alert into `psl_mod_alerts`, delivered to `DISCORD_WEBHOOK_URL`:

| Kind | Fires when |
|---|---|
| `new-mod` | A watched Workshop ID never seen on any server before appears |
| `updated` | A watched mod already in use is updated on the Workshop |
| `retitled` | A watched mod's title changes |
| `banned` | A watched mod is banned on the Workshop but servers still require it |
| `watchlist` | A watched mod is live anywhere |

`updated` matters most for supply-chain risk: a mod turning hostile in an update, after
it is already required by dozens of servers, is likelier than a new malicious mod being
widely adopted.

To watch a specific mod:

```sql
INSERT INTO psl_mod_watch (mod_id, reason) VALUES (3776646946, 'why you care');
```

Alerts are throttled to one per kind per mod per day. If `DISCORD_WEBHOOK_URL` is unset,
rows stay `delivered = false` and go out once a webhook is configured. The tables come
from `nextjs/migrations/023_mod_intel.sql`; this process does not create them.

## Deployment

See [DEPLOY-LINUX.md](DEPLOY-LINUX.md) for Ubuntu VPS setup with systemd.

## Related

Frontend server list UI lives in the [puckstats](https://github.com/ckhawks/puckstats) repo at `/app/server-list/`.
