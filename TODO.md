# PuckServerTracker TODO

## Analytics (needs data accumulation)
- [ ] Server/network uptime % - % of snapshots with >0 players
- [ ] Average fill rate - avg(players / min(max_players, 12)) over time
- [ ] Peak concurrent players - highest player count seen per server/network
- [ ] Prime time dominance - % of total players during peak hours (7pm-midnight per region)
- [ ] Network comparison table/chart with these metrics side-by-side

## Frontend
- [ ] Geolocate not working on localhost (ip-api.com returns server IP, not client)
- [ ] Network share chart: make visually fit better alongside player activity chart
- [ ] Server detail: show ISP in the info cards row
- [ ] Mobile responsiveness pass on server list table
- [ ] Country dropdown: sort by most servers first, not alphabetically?

## Tracker
- [ ] Handle Steam refresh token expiry (re-prompt or alert)
- [ ] Clean up old snapshots (auto-prune data older than X months?)
- [ ] Update GeoIP databases periodically (MaxMind updates weekly)
- [ ] Consider storing ping_port for b202 servers

## Data quality
- [ ] More ISP → country overrides as needed (like Ondrej Vrana → CZ)
- [ ] Auto-detect network groupings by IP range or server name patterns?
- [ ] Flag servers that haven't been seen in N days as inactive

## Deployment
- [ ] Set up PuckServerTracker GitHub repo
- [ ] CI/CD: auto-publish on push
- [ ] Alerting if tracker stops reporting data
