using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using SocketIOClient;
using SteamKit2;
using SteamKit2.Authentication;
using SioClient = SocketIOClient.SocketIO;

const uint PUCK_APP_ID = 2994020;
const int POLL_INTERVAL_MS = 15 * 60 * 1000; // 15 minutes
const int MAX_EMPTY_SCANS = 3; // ~45 min with no servers from any master -> exit for systemd to restart
// Workshop metadata is cached rather than re-fetched every 15 minutes, but the cache is
// deliberately short: a title change on a mod that is already deployed across dozens of
// servers is exactly the signal we want to catch, so staleness here costs detection time.
const int MOD_CACHE_HOURS = 24;
bool runOnce = args.Contains("--once");
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

// --- Load .env file if present ---
var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (!File.Exists(envPath))
    envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) continue;
        var key = trimmed[..eq].Trim();
        var val = trimmed[(eq + 1)..].Trim();
        if (Environment.GetEnvironmentVariable(key) == null)
            Environment.SetEnvironmentVariable(key, val);
    }
}

// --- PostgreSQL Init ---
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new Exception("DATABASE_URL environment variable is required. Set it or create a .env file.");
var connStr = databaseUrl;
// Handle postgres:// URL format (convert to Npgsql connection string if needed)
if (connStr.StartsWith("postgres://") || connStr.StartsWith("postgresql://"))
{
    var uri = new Uri(connStr);
    var userInfo = uri.UserInfo.Split(':');
    connStr = $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true";
}
var db = NpgsqlDataSource.Create(connStr);
Console.WriteLine($"[DB] Connected to PostgreSQL");

// --- Steam Login via SteamKit2 ---
var steamSession = new SteamSession();
await steamSession.Login();

if (!steamSession.IsLoggedIn)
{
    Console.Error.WriteLine("[Steam] Login failed.");
    return 1;
}

// --- Get auth ticket ---
string? ticket = await steamSession.GetAuthTicket(PUCK_APP_ID);
if (ticket == null)
{
    Console.Error.WriteLine("[Steam] Failed to get auth ticket");
    return 1;
}
Console.WriteLine($"[Steam] Got auth ticket ({ticket.Length / 2} bytes)");

// --- Create persistent connections ---
// Pass a ticket PROVIDER, not a fixed ticket: Steam auth tickets / master-side
// sessions expire after a few days, so every (re)auth must mint a FRESH ticket.
// Otherwise the WebSocket stays "connected" but the master silently stops answering
// server-list requests until the process is restarted. See Authenticate().
//
// nasejevs alternates the live master between puck1 and puck2 on every game build and
// decommissions the outgoing host, so both are kept here and each scan uses whichever
// still answers. Hardcoding the current one turns every rotation into an outage.
//
// The build label is metadata only — nothing in the protocol reports it (the master
// returns bare ip/port, the TCP preview carries no version field) — so it is mapped per
// host here and bumped by hand when a build ships. A stale label never stops a scan.
var masters = new[]
{
    new PersistentConnection("b1235", "wss://puck1.nasejevs.com",
        () => steamSession.GetAuthTicket(PUCK_APP_ID)),
    new PersistentConnection("b1153", "wss://puck2.nasejevs.com",
        () => steamSession.GetAuthTicket(PUCK_APP_ID)),
};

Console.WriteLine($"PuckServerTracker started (mode: {(runOnce ? "single scan" : "continuous, every 15 min")})");

await Task.WhenAll(masters.Select(m => m.EnsureConnected()));

// Proactively rebuild the connection well before the master expires the session
// (~days), so a scan never has to eat the reconnect-on-timeout penalty first.
var lastReconnect = DateTime.UtcNow;
var reconnectEvery = TimeSpan.FromHours(12);

// Reconnecting the socket does not rebuild the Steam login, and a process that has run for
// days can end up where no reconnect helps: the master kicks every auth, or the probe never
// sees a handshake, while a fresh process works on its first try (2026-10-18, 33 h of no data).
// So after enough empty scans in a row, exit and let systemd's Restart=always start clean.
var emptyScans = 0;
var exitCode = 0;

try
{
    do
    {
        steamSession.RunCallbacks();

        if (DateTime.UtcNow - lastReconnect >= reconnectEvery)
        {
            foreach (var m in masters)
            {
                Console.WriteLine($"[{m.Version}] Proactive reconnect (session age >= {reconnectEvery.TotalHours:0}h)...");
                await m.ForceReconnect();
            }
            lastReconnect = DateTime.UtcNow;
        }

        var found = await Scan(db, masters);
        emptyScans = found > 0 ? 0 : emptyScans + 1;
        if (!runOnce && emptyScans >= MAX_EMPTY_SCANS)
        {
            Console.Error.WriteLine($"No servers from any master for {emptyScans} scans in a row, exiting so systemd restarts with a fresh Steam session");
            exitCode = 1;
            break;
        }

        if (!runOnce)
        {
            Console.WriteLine($"\nNext scan in 15 minutes... (press Ctrl+C to exit)");
            for (int i = 0; i < POLL_INTERVAL_MS / 1000; i++)
            {
                await Task.Delay(1000);
                steamSession.RunCallbacks();
            }
        }
    } while (!runOnce);
}
finally
{
    foreach (var m in masters) await m.Disconnect();
    steamSession.Disconnect();
    Console.WriteLine("[Steam] Disconnected.");
}

return exitCode;

// =============================================================================
// Scan
// =============================================================================
async Task<int> Scan(NpgsqlDataSource db, PersistentConnection[] masters)
{
    var timestamp = DateTime.UtcNow.ToString("o");
    Console.WriteLine($"\n{"".PadRight(60, '=')}");
    Console.WriteLine($"Scan at {timestamp}");
    Console.WriteLine("".PadRight(60, '='));

    // Scanned independently: a master that is down — or has just been decommissioned by a
    // rotation — must cost only its own results, never the other host and never the loop.
    var counts = await Task.WhenAll(masters.Select(m => ScanMaster(db, m, timestamp)));
    return counts.Sum();
}

async Task<int> ScanMaster(NpgsqlDataSource db, PersistentConnection master, string timestamp)
{
    await master.EnsureConnected();
    if (!master.IsConnected)
    {
        Console.WriteLine($"[{master.Version}] Master not answering, skipped this scan");
        return 0;
    }

    var task = master.RequestServerList("playerGetServerBrowserEndPointsRequest",
        response => ParseEndpointsResponse(master.Version, response));

    return await ProcessResult(master.Version, task, db, timestamp);
}

// Returns how many servers the master listed, even if saving them failed afterwards.
async Task<int> ProcessResult(string version, Task<List<ServerInfo>> task, NpgsqlDataSource db, string timestamp)
{
    var count = 0;
    try
    {
        var servers = await task;
        count = servers.Count;
        EnrichWithGeoIP(servers);
        PrintServers(version, servers);
        await SaveSnapshot(db, version, servers, timestamp);
        await ProcessModIntel(db, servers, DateTime.Parse(timestamp).ToUniversalTime());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{version}] Failed: {ex.Message}");
    }
    return count;
}

// =============================================================================
// Response Parsers
// =============================================================================
async Task<List<ServerInfo>> ParseEndpointsResponse(string version, SocketIOResponse response)
{
    var json = response.GetValue<JsonElement>();
    var endpoints = new List<ServerInfo>();
    JsonElement endpointsArray;

    if (json.TryGetProperty("data", out var data) &&
        data.TryGetProperty("endPoints", out endpointsArray))
    { }
    else if (json.TryGetProperty("endPoints", out endpointsArray))
    { }
    else if (json.ValueKind == JsonValueKind.Array)
    { endpointsArray = json; }
    else
    {
        Console.Error.WriteLine($"[{version}] Unexpected format");
        return endpoints;
    }

    foreach (var ep in endpointsArray.EnumerateArray())
    {
        endpoints.Add(new ServerInfo
        {
            IpAddress = ep.GetProperty("ipAddress").GetString() ?? "",
            Port = ep.GetProperty("port").GetUInt16(),
        });
    }

    Console.WriteLine($"[{version}] Fetching TCP previews for {endpoints.Count} endpoints...");
    await Task.WhenAll(endpoints.Select(TcpPreview));
    Console.WriteLine($"[{version}] Parsed {endpoints.Count} servers");
    return endpoints;
}

async Task TcpPreview(ServerInfo server)
{
    try
    {
        // Single budget covering connect + write + read. NetworkStream.ReadTimeout/
        // WriteTimeout are ignored by the async APIs, so a server that accepts the
        // connection but never replies would hang ReadAsync forever (and stall the
        // whole Task.WhenAll batch). A CancellationToken is honored by the async APIs.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(server.IpAddress, server.Port, cts.Token);

        using var stream = client.GetStream();

        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"type\":0}"), cts.Token);

        // The preview reply is a single JSON object with no length prefix or delimiter,
        // so there is nothing to frame on. Read once (which covers every real response --
        // the largest observed is ~1 KB), and only read again if the JSON won't parse,
        // which means the reply was split across packets. Looping unconditionally would
        // block every well-behaved server until the timeout.
        var buffer = new byte[65536];
        var bytesRead = await stream.ReadAsync(buffer, cts.Token);
        if (bytesRead == 0) return;

        JsonElement msg;
        while (true)
        {
            try
            {
                msg = JsonSerializer.Deserialize<JsonElement>(
                    System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead));
                break;
            }
            catch (JsonException) when (bytesRead < buffer.Length)
            {
                var more = await stream.ReadAsync(buffer.AsMemory(bytesRead), cts.Token);
                if (more == 0) return;
                bytesRead += more;
            }
        }

        if (msg.TryGetProperty("type", out var type) && type.GetInt32() == 1)
        {
            server.Name = msg.TryGetProperty("name", out var n) ? n.GetString() : null;
            server.Players = msg.TryGetProperty("players", out var p) ? p.GetInt32() : null;
            server.MaxPlayers = msg.TryGetProperty("maxPlayers", out var m) ? m.GetInt32() : null;
            server.IsPasswordProtected = msg.TryGetProperty("isPasswordProtected", out var pp) && pp.GetBoolean();
            if (msg.TryGetProperty("clientRequiredModIds", out var mods) && mods.ValueKind == JsonValueKind.Array)
                server.ClientRequiredModIds = mods.EnumerateArray()
                    // The game switched these from JSON numbers to JSON strings around
                    // 2026-05. GetUInt64() throws on a string element, and the throw used
                    // to be swallowed here -- which silently NULLed every mod list for
                    // three months while the rest of the preview kept saving fine.
                    // Accept both shapes so a future flip back costs nothing.
                    .Select(m => m.ValueKind == JsonValueKind.String
                        ? ulong.Parse(m.GetString()!)
                        : m.GetUInt64())
                    .ToArray();
        }
    }
    // Transport-level failures are normal every scan across a few hundred hosts --
    // timeouts, refused connections, and peers that reset mid-read -- so they stay quiet.
    // Anything else means the reply parsed differently than we expect -- i.e. the game's
    // preview format changed -- and must be loud, or it rots undetected.
    catch (OperationCanceledException) { }
    catch (System.Net.Sockets.SocketException) { }
    catch (IOException) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[preview] {server.IpAddress}:{server.Port} {ex.GetType().Name}: {ex.Message}");
    }
}

// =============================================================================
// GeoIP
// =============================================================================
void EnrichWithGeoIP(List<ServerInfo> servers)
{
    var cityDbPath = Path.Combine(AppContext.BaseDirectory, "GeoLite2-City.mmdb");
    var asnDbPath = Path.Combine(AppContext.BaseDirectory, "GeoLite2-ASN.mmdb");

    MaxMind.GeoIP2.DatabaseReader? cityReader = File.Exists(cityDbPath) ? new(cityDbPath) : null;
    MaxMind.GeoIP2.DatabaseReader? asnReader = File.Exists(asnDbPath) ? new(asnDbPath) : null;

    try
    {
        foreach (var s in servers)
        {
            try
            {
                if (cityReader != null)
                {
                    var city = cityReader.City(s.IpAddress);
                    s.Country = city.Country.IsoCode;
                    s.Region = city.MostSpecificSubdivision?.Name;
                    s.City = city.City?.Name;
                    s.Lat = city.Location?.Latitude;
                    s.Lon = city.Location?.Longitude;
                }
            }
            catch { }

            try
            {
                if (asnReader != null)
                {
                    var asn = asnReader.Asn(s.IpAddress);
                    s.Isp = asn.AutonomousSystemOrganization;
                }
            }
            catch { }

            // ISP-based country overrides (GeoIP misattributions)
            if (s.Isp == "Ondrej Vrana")
            {
                s.Country = "CZ";
                s.City = "Czechia";
                s.Lat = 49.8175;
                s.Lon = 15.4730;
            }
        }
    }
    finally
    {
        cityReader?.Dispose();
        asnReader?.Dispose();
    }
}

// =============================================================================
// Print
// =============================================================================
void PrintServers(string version, List<ServerInfo> servers)
{
    if (servers.Count == 0)
    {
        Console.WriteLine($"\n[{version}] No servers found.");
        return;
    }

    var totalPlayers = servers.Sum(s => s.Players ?? 0);
    var rows = servers.OrderByDescending(s => s.Players ?? -1).Select(s => new
    {
        Name = s.Name != null ? StripRichText(s.Name) : "(endpoint only)",
        Players = s.Players.HasValue ? $"{s.Players}/{s.MaxPlayers}" : "?",
        Addr = $"{s.IpAddress}:{s.Port}",
        Loc = s.Country != null ? $"{s.City ?? "?"}, {s.Country}" : "Unknown",
        Isp = s.Isp ?? "",
    }).ToList();

    var nameW = Math.Max(12, rows.Max(r => r.Name.Length) + 2);

    Console.WriteLine($"\n[{version}] {servers.Count} servers | {totalPlayers} total players");
    Console.WriteLine(new string('-', nameW + 10 + 24 + 22 + 24));
    Console.WriteLine($"{"SERVER NAME".PadRight(nameW)}{"PLAYERS",-10}{"IP:PORT",-24}{"LOCATION",-22}{"ISP",-24}");
    Console.WriteLine(new string('-', nameW + 10 + 24 + 22 + 24));

    foreach (var r in rows)
    {
        Console.WriteLine($"{r.Name.PadRight(nameW)}{r.Players,-10}{r.Addr,-24}{r.Loc,-22}{r.Isp,-24}");
    }
}

// =============================================================================
// PostgreSQL (psl_ tables)
// =============================================================================
async Task SaveSnapshot(NpgsqlDataSource db, string version, List<ServerInfo> servers, string timestamp)
{
    await using var conn = await db.OpenConnectionAsync();
    await using var txn = await conn.BeginTransactionAsync();

    // Create snapshot
    await using var insertSnapshot = new NpgsqlCommand(
        "INSERT INTO psl_snapshots (timestamp, version) VALUES ($1, $2) RETURNING id", conn, txn);
    insertSnapshot.Parameters.AddWithValue(DateTime.Parse(timestamp).ToUniversalTime());
    insertSnapshot.Parameters.AddWithValue(version);
    var snapshotId = (int)(await insertSnapshot.ExecuteScalarAsync())!;

    foreach (var s in servers)
    {
        var strippedName = s.Name != null ? StripRichText(s.Name) : null;
        var ts = DateTime.Parse(timestamp).ToUniversalTime();

        // Upsert server identity
        await using var upsertServer = new NpgsqlCommand(@"
            INSERT INTO psl_servers (ip_address, port, country, region, city, lat, lon, isp, game_version, first_seen, last_seen)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $10)
            ON CONFLICT(ip_address, port) DO UPDATE SET
                country = EXCLUDED.country, region = EXCLUDED.region, city = EXCLUDED.city,
                lat = EXCLUDED.lat, lon = EXCLUDED.lon, isp = EXCLUDED.isp,
                game_version = EXCLUDED.game_version, last_seen = EXCLUDED.last_seen
            RETURNING id", conn, txn);
        upsertServer.Parameters.AddWithValue(s.IpAddress);
        upsertServer.Parameters.AddWithValue((int)s.Port);
        upsertServer.Parameters.AddWithValue((object?)s.Country ?? DBNull.Value);
        upsertServer.Parameters.AddWithValue((object?)s.Region ?? DBNull.Value);
        upsertServer.Parameters.AddWithValue((object?)s.City ?? DBNull.Value);
        upsertServer.Parameters.AddWithValue((object?)s.Lat ?? DBNull.Value);
        upsertServer.Parameters.AddWithValue((object?)s.Lon ?? DBNull.Value);
        upsertServer.Parameters.AddWithValue((object?)s.Isp ?? DBNull.Value);
        upsertServer.Parameters.AddWithValue(version);
        upsertServer.Parameters.AddWithValue(ts);
        var serverId = (int)(await upsertServer.ExecuteScalarAsync())!;

        // Check if name changed
        await using var getLastName = new NpgsqlCommand(@"
            SELECT id, name FROM psl_server_names
            WHERE server_id = $1 ORDER BY id DESC LIMIT 1", conn, txn);
        getLastName.Parameters.AddWithValue(serverId);
        await using var nameReader = await getLastName.ExecuteReaderAsync();

        if (await nameReader.ReadAsync())
        {
            var lastNameId = nameReader.GetInt32(0);
            var lastName = nameReader.IsDBNull(1) ? null : nameReader.GetString(1);
            await nameReader.CloseAsync();

            if (lastName == strippedName)
            {
                await using var updateName = new NpgsqlCommand(
                    "UPDATE psl_server_names SET last_seen = $1 WHERE id = $2", conn, txn);
                updateName.Parameters.AddWithValue(ts);
                updateName.Parameters.AddWithValue(lastNameId);
                await updateName.ExecuteNonQueryAsync();
            }
            else
            {
                await using var insertName = new NpgsqlCommand(@"
                    INSERT INTO psl_server_names (server_id, name, raw_name, first_seen, last_seen)
                    VALUES ($1, $2, $3, $4, $4)", conn, txn);
                insertName.Parameters.AddWithValue(serverId);
                insertName.Parameters.AddWithValue((object?)strippedName ?? DBNull.Value);
                insertName.Parameters.AddWithValue((object?)s.Name ?? DBNull.Value);
                insertName.Parameters.AddWithValue(ts);
                await insertName.ExecuteNonQueryAsync();
            }
        }
        else
        {
            await nameReader.CloseAsync();
            await using var insertName = new NpgsqlCommand(@"
                INSERT INTO psl_server_names (server_id, name, raw_name, first_seen, last_seen)
                VALUES ($1, $2, $3, $4, $4)", conn, txn);
            insertName.Parameters.AddWithValue(serverId);
            insertName.Parameters.AddWithValue((object?)strippedName ?? DBNull.Value);
            insertName.Parameters.AddWithValue((object?)s.Name ?? DBNull.Value);
            insertName.Parameters.AddWithValue(ts);
            await insertName.ExecuteNonQueryAsync();
        }

        // Insert snapshot data
        await using var insertSnap = new NpgsqlCommand(@"
            INSERT INTO psl_server_snapshots (snapshot_id, server_id, players, max_players, is_password_protected, client_required_mod_ids)
            VALUES ($1, $2, $3, $4, $5, $6)", conn, txn);
        insertSnap.Parameters.AddWithValue(snapshotId);
        insertSnap.Parameters.AddWithValue(serverId);
        insertSnap.Parameters.AddWithValue((object?)s.Players ?? DBNull.Value);
        insertSnap.Parameters.AddWithValue((object?)s.MaxPlayers ?? DBNull.Value);
        insertSnap.Parameters.AddWithValue(s.IsPasswordProtected);
        insertSnap.Parameters.AddWithValue(s.ClientRequiredModIds is { Length: > 0 }
            ? (object)JsonSerializer.Serialize(s.ClientRequiredModIds)
            : DBNull.Value);
        await insertSnap.ExecuteNonQueryAsync();
    }

    await txn.CommitAsync();
    Console.WriteLine($"[DB] Saved {servers.Count} servers for {version}");
}

// =============================================================================
// Mod Intel - Workshop metadata cache, watchlist, and change alerts
// =============================================================================
// psl_mods, psl_mod_watch and psl_mod_alerts are owned by the puckstats schema and
// created by nextjs/migrations/023_mod_intel.sql. This process deliberately does not
// create them: schema changes belong in one place, and a tracker that can create tables
// in production on startup is how they drift out of the migration history in the first
// place.
async Task ProcessModIntel(NpgsqlDataSource db, List<ServerInfo> servers, DateTime ts)
{
    // Which servers run which mod, for alert context
    var usage = new Dictionary<ulong, List<string>>();
    foreach (var s in servers)
        foreach (var id in s.ClientRequiredModIds ?? [])
            usage.TryAdd(id, []);
    foreach (var s in servers)
        foreach (var id in s.ClientRequiredModIds ?? [])
            usage[id].Add(StripRichText(s.Name ?? $"{s.IpAddress}:{s.Port}"));

    if (usage.Count == 0) return;
    var seen = usage.Keys.ToArray();

    await using var conn = await db.OpenConnectionAsync();

    // An empty cache means this is the first run against this database. Every mod would
    // otherwise look brand new and fire a few dozen alerts at once, which trains you to
    // ignore them. Seed silently instead and start alerting from the next scan.
    await using var countCmd = new NpgsqlCommand("SELECT count(*) FROM psl_mods", conn);
    var seeding = (long)(await countCmd.ExecuteScalarAsync())! == 0;

    var known = new Dictionary<ulong, (string? Title, DateTime? Updated, DateTime? Resolved, bool Banned)>();
    await using (var cmd = new NpgsqlCommand(
        "SELECT mod_id, title, time_updated, last_resolved, banned FROM psl_mods WHERE mod_id = ANY($1)", conn))
    {
        cmd.Parameters.AddWithValue(seen.Select(x => (long)x).ToArray());
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            known[(ulong)r.GetInt64(0)] = (
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDateTime(2),
                r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.GetBoolean(4));
    }

    // Explicit watchlist. Loaded up front because nothing outside it is worth alerting on:
    // servers add and drop mods constantly, so alerting on every new ID buries the handful
    // we actually care about. Everything below still updates psl_mods for the site's
    // history -- only the alert rows are filtered.
    var watched = new Dictionary<ulong, string>();
    await using (var cmd = new NpgsqlCommand(
        "SELECT mod_id, reason FROM psl_mod_watch WHERE mod_id = ANY($1)", conn))
    {
        cmd.Parameters.AddWithValue(seen.Select(x => (long)x).ToArray());
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            watched[(ulong)r.GetInt64(0)] = r.IsDBNull(1) ? "no reason recorded" : r.GetString(1);
    }

    var alerts = new List<(ulong Id, string Kind, string Detail)>();

    // A mod ID nobody has ever deployed before, and that we already watch -- someone
    // deploying a mod we flagged is the whole point of the list.
    foreach (var id in seen.Where(id => !known.ContainsKey(id)))
        alerts.Add((id, "new-mod", $"First appearance, on {usage[id].Count} server(s): {Sample(usage[id])}"));

    await using (var upsert = new NpgsqlCommand(@"
        INSERT INTO psl_mods (mod_id, first_seen, last_seen)
        VALUES ($1, $2, $2)
        ON CONFLICT (mod_id) DO UPDATE SET last_seen = $2", conn))
    {
        upsert.Parameters.Add(new NpgsqlParameter());
        upsert.Parameters.AddWithValue(ts);
        foreach (var id in seen)
        {
            upsert.Parameters[0].Value = (long)id;
            await upsert.ExecuteNonQueryAsync();
        }
    }

    // Refresh anything never resolved or older than the cache window
    var stale = seen.Where(id => !known.TryGetValue(id, out var k)
                                 || k.Resolved == null
                                 || k.Resolved < ts.AddHours(-MOD_CACHE_HOURS)).ToList();

    foreach (var chunk in stale.Chunk(200))
    {
        var (ok, details) = await ResolveWorkshop(chunk);

        // Deleted, hidden and never-existed items come back with result != 1 and are not
        // cached, so without this they would keep a NULL last_resolved and be re-requested
        // on every scan forever. Stamp the whole batch as attempted so they back off to
        // the normal 24h cadence -- but only when the call actually succeeded, or a Steam
        // outage would silently freeze every mod for a day.
        if (ok)
        {
            await using var stamp = new NpgsqlCommand(
                "UPDATE psl_mods SET last_resolved = $2 WHERE mod_id = ANY($1) AND last_resolved IS DISTINCT FROM $2", conn);
            stamp.Parameters.AddWithValue(chunk.Select(x => (long)x).ToArray());
            stamp.Parameters.AddWithValue(ts);
            await stamp.ExecuteNonQueryAsync();
        }

        foreach (var (id, d) in details)
        {
            if (known.TryGetValue(id, out var old) && !seeding)
            {
                if (old.Title != null && d.Title != old.Title)
                    alerts.Add((id, "retitled", $"Title changed from \"{old.Title}\" to \"{d.Title}\""));
                if (old.Updated != null && d.Updated > old.Updated)
                    alerts.Add((id, "updated", $"\"{d.Title}\" was updated on the Workshop, live on {usage[id].Count} server(s): {Sample(usage[id])}"));
                if (d.Banned && !old.Banned)
                    alerts.Add((id, "banned", $"\"{d.Title}\" is now banned on the Workshop but still required by {usage[id].Count} server(s): {Sample(usage[id])}"));
            }

            await using var cmd = new NpgsqlCommand(@"
                UPDATE psl_mods SET title = $2, creator_steam_id = $3, time_created = $4,
                       time_updated = $5, subscriptions = $6, banned = $7, last_resolved = $8
                WHERE mod_id = $1", conn);
            cmd.Parameters.AddWithValue((long)id);
            cmd.Parameters.AddWithValue((object?)d.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)d.Creator ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)d.Created ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)d.Updated ?? DBNull.Value);
            cmd.Parameters.AddWithValue(d.Subscriptions);
            cmd.Parameters.AddWithValue(d.Banned);
            cmd.Parameters.AddWithValue(ts);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    foreach (var (id, reason) in watched)
        alerts.Add((id, "watchlist", $"Watched mod is live on {usage[id].Count} server(s) ({reason}): {Sample(usage[id])}"));

    if (seeding)
    {
        Console.WriteLine($"[mods] Seeded cache with {seen.Length} mods, alerting starts next scan");
        return;
    }

    // A watchlisted mod stays live indefinitely, so an un-throttled alert would fire every
    // 15 minutes forever. One per kind per mod per day is enough to stay informed.
    var fired = 0;
    foreach (var (id, kind, detail) in alerts.Where(a => watched.ContainsKey(a.Id)))
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO psl_mod_alerts (mod_id, kind, detail, created_at)
            SELECT $1, $2, $3, $4
            WHERE NOT EXISTS (
                SELECT 1 FROM psl_mod_alerts
                WHERE mod_id = $1 AND kind = $2 AND created_at > $4 - interval '24 hours')", conn);
        cmd.Parameters.AddWithValue((long)id);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(detail);
        cmd.Parameters.AddWithValue(ts);
        fired += await cmd.ExecuteNonQueryAsync();
    }

    if (fired > 0) Console.WriteLine($"[mods] {fired} new alert(s)");
    await DeliverAlerts(conn);
}

// Returns Ok = false when the call itself failed, so the caller can tell a Steam outage
// apart from a batch where every item is legitimately unresolvable.
async Task<(bool Ok, Dictionary<ulong, (string? Title, string? Creator, DateTime? Created, DateTime? Updated, int Subscriptions, bool Banned)> Details)>
    ResolveWorkshop(ulong[] ids)
{
    var result = new Dictionary<ulong, (string?, string?, DateTime?, DateTime?, int, bool)>();
    try
    {
        var form = new List<KeyValuePair<string, string>> { new("itemcount", ids.Length.ToString()) };
        for (var i = 0; i < ids.Length; i++)
            form.Add(new($"publishedfileids[{i}]", ids[i].ToString()));

        var resp = await http.PostAsync(
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
            new FormUrlEncodedContent(form));
        resp.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var d in doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
        {
            if (!ulong.TryParse(d.GetProperty("publishedfileid").GetString(), out var id)) continue;
            // result != 1 means removed, hidden, or never existed -- there is nothing to
            // cache, and overwriting a good title with null would look like a retitle.
            if (!d.TryGetProperty("result", out var ok) || ok.GetInt32() != 1) continue;

            DateTime? Epoch(string prop) =>
                d.TryGetProperty(prop, out var v) && v.GetInt64() > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(v.GetInt64()).UtcDateTime
                    : null;

            result[id] = (
                d.TryGetProperty("title", out var t) ? t.GetString() : null,
                d.TryGetProperty("creator", out var c) ? c.GetString() : null,
                Epoch("time_created"),
                Epoch("time_updated"),
                d.TryGetProperty("subscriptions", out var s) ? s.GetInt32() : 0,
                d.TryGetProperty("banned", out var b) && b.GetInt32() != 0);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[mods] Workshop lookup failed: {ex.Message}");
        return (false, result);
    }
    return (true, result);
}

async Task DeliverAlerts(NpgsqlConnection conn)
{
    var webhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");

    var pending = new List<(int Id, string Line)>();
    await using (var cmd = new NpgsqlCommand(@"
        SELECT a.id, a.mod_id, a.kind, a.detail, m.title
        FROM psl_mod_alerts a
        LEFT JOIN psl_mods m ON m.mod_id = a.mod_id
        WHERE NOT a.delivered
        ORDER BY a.id", conn))
    await using (var r = await cmd.ExecuteReaderAsync())
        while (await r.ReadAsync())
        {
            var modId = r.GetInt64(1);
            // The DB keeps the raw text; escaping happens here, at the delivery boundary
            var title = r.IsDBNull(4) ? "(unresolved)" : DiscordSafe(r.GetString(4));
            pending.Add((r.GetInt32(0),
                $"**[{r.GetString(2)}]** {title} (`{modId}`)\n{(r.IsDBNull(3) ? "" : DiscordSafe(r.GetString(3)))}\n" +
                $"<https://steamcommunity.com/sharedfiles/filedetails/?id={modId}>"));
        }

    if (pending.Count == 0) return;

    if (string.IsNullOrWhiteSpace(webhook))
    {
        // No webhook configured: leave the rows undelivered so they go out once one is,
        // rather than silently marking them done.
        Console.Error.WriteLine($"[mods] {pending.Count} alert(s) undelivered, DISCORD_WEBHOOK_URL is not set");
        return;
    }

    // Discord caps a message at 2000 characters
    var sent = new List<int>();
    var batch = new List<string>();
    var length = 0;

    async Task Flush()
    {
        if (batch.Count == 0) return;
        var body = JsonSerializer.Serialize(new { content = string.Join("\n\n", batch) });
        var resp = await http.PostAsync(webhook, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Discord returned {(int)resp.StatusCode}");
        batch.Clear();
        length = 0;
    }

    try
    {
        foreach (var (id, line) in pending)
        {
            if (length + line.Length + 2 > 1900) await Flush();
            batch.Add(line);
            length += line.Length + 2;
            sent.Add(id);
        }
        await Flush();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[mods] Discord delivery failed: {ex.Message}");
        // Only mark what actually made it into a successful POST
        sent = sent.Take(sent.Count - batch.Count).ToList();
    }

    if (sent.Count == 0) return;
    await using var mark = new NpgsqlCommand(
        "UPDATE psl_mod_alerts SET delivered = true WHERE id = ANY($1)", conn);
    mark.Parameters.AddWithValue(sent.ToArray());
    await mark.ExecuteNonQueryAsync();
    Console.WriteLine($"[mods] Delivered {sent.Count} alert(s) to Discord");
}

// Server names and mod titles are attacker-controlled text going into a Discord message.
// Two things go wrong if it is passed through raw: Unity rich-text colour tags that
// StripRichText misses (it only matches 6-8 hex digits, so <#f00> survives) are read by
// Discord as <#channelid> and render as a broken channel mention, and any markdown in a
// name reformats the alert. Strip every remaining tag, then escape markdown.
string DiscordSafe(string text) =>
    Regex.Replace(Regex.Replace(text, @"<[^>]*>", ""), @"([\\`*_~|>])", @"\$1").Trim();

string Sample(List<string> names) =>
    names.Count <= 3
        ? string.Join(", ", names)
        : string.Join(", ", names.Take(3)) + $", and {names.Count - 3} more";

// =============================================================================
// Utilities
// =============================================================================
string StripRichText(string text) =>
    Regex.Replace(
        Regex.Replace(text, @"<#[0-9a-fA-F]{6,8}>", ""), // <#FFD700> style
        @"<\/?[a-zA-Z][^>]*>", "" // <b>, <color=...>, </color> style
    ).Trim();

// =============================================================================
// Data Model
// =============================================================================
class ServerInfo
{
    public string IpAddress { get; set; } = "";
    public ushort Port { get; set; }
    public string? Name { get; set; }
    public int? Players { get; set; }
    public int? MaxPlayers { get; set; }
    public bool IsPasswordProtected { get; set; }
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? Isp { get; set; }
    public ulong[]? ClientRequiredModIds { get; set; }
}

// =============================================================================
// SteamKit2 Session - replaces Steamworks.NET, no Steam client needed
// =============================================================================
class SteamSession
{
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _user;
    private readonly SteamApps _apps;
    private readonly SteamAuthTicket _authTicket;

    private bool _isRunning;
    private SteamUser.LogOnDetails? _logOnDetails;
    private TaskCompletionSource _licensesReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _pumpThread;
    public bool IsLoggedIn { get; private set; }

    private readonly string _credentialsPath = Path.Combine(AppContext.BaseDirectory, "steam_credentials.json");

    public SteamSession()
    {
        _client = new SteamClient();
        _callbacks = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _authTicket = _client.GetHandler<SteamAuthTicket>()!;

        _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
    }

    public async Task Login()
    {
        Console.WriteLine("[Steam] Connecting to Steam...");
        _isRunning = true;
        StartPumpThread();
        _client.Connect();

        // Wait for connection (pump thread handles callbacks)
        var connectWait = await Task.WhenAny(_connected.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (connectWait != _connected.Task)
        {
            Console.Error.WriteLine("[Steam] Failed to connect to Steam network");
            return;
        }

        // Try saved credentials first
        var savedCreds = LoadCredentials();
        if (savedCreds != null)
        {
            Console.WriteLine($"[Steam] Logging in as {savedCreds.Username} (saved credentials)...");
            _logOnDetails = new SteamUser.LogOnDetails
            {
                Username = savedCreds.Username,
                AccessToken = savedCreds.RefreshToken,
            };
            _user.LogOn(_logOnDetails);
        }
        else
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("[Steam] No saved credentials and stdin is not a TTY (running under systemd?).");
                Console.Error.WriteLine("[Steam] Stop the service, run interactively once to mint a refresh token, then restart the service:");
                Console.Error.WriteLine("[Steam]   sudo systemctl stop pucktracker && ./PuckTracker --once && sudo systemctl start pucktracker");
                return;
            }

            // Interactive login
            Console.Write("[Steam] Username: ");
            var username = Console.ReadLine()?.Trim() ?? "";
            Console.Write("[Steam] Password: ");
            var password = ReadPassword();
            Console.WriteLine();

            // Start auth session for Steam Guard support
            var authSession = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    Authenticator = new UserConsoleAuthenticator(),
                });

            var pollResult = await authSession.PollingWaitForResultAsync();

            Console.WriteLine($"[Steam] Got refresh token, logging in...");
            _logOnDetails = new SteamUser.LogOnDetails
            {
                Username = pollResult.AccountName,
                AccessToken = pollResult.RefreshToken,
            };
            _user.LogOn(_logOnDetails);

            // Save for next time
            SaveCredentials(new SavedCredentials
            {
                Username = pollResult.AccountName,
                RefreshToken = pollResult.RefreshToken,
            });
        }

        // Wait for login result
        await Task.WhenAny(_loggedOn.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    }

    public async Task<string?> GetAuthTicket(uint appId)
    {
        // Wait for the license list — GetAuthTicketForWebApi is canceled internally
        // if it's called before the account's licenses have been received.
        var waitTask = _licensesReceived.Task;
        if (!waitTask.IsCompleted)
        {
            Console.WriteLine("[Steam] Waiting for license list before requesting auth ticket...");
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != waitTask)
            {
                Console.Error.WriteLine("[Steam] Timed out waiting for license list");
                return null;
            }
        }

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Console.WriteLine($"[Steam] Requesting auth ticket for app {appId} (attempt {attempt})...");
                var ticketInfo = await _authTicket.GetAuthTicketForWebApi(appId, "puck");
                return Convert.ToHexString(ticketInfo.Ticket);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Steam] Auth ticket failed (attempt {attempt}): {ex.Message}");
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
        // Don't delete saved credentials here — logon succeeded, so the refresh token
        // is fine. Ticket failures are transient (network / GC not ready).
        return null;
    }

    public void RunCallbacks()
    {
        // No-op: callbacks are pumped by the background thread started in Login().
    }

    private void StartPumpThread()
    {
        if (_pumpThread != null) return;
        _pumpThread = new Thread(() =>
        {
            while (_isRunning)
            {
                try { _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
                catch (Exception ex) { Console.Error.WriteLine($"[Steam] Pump error: {ex.Message}"); }
            }
        })
        { IsBackground = true, Name = "SteamKit-Pump" };
        _pumpThread.Start();
    }

    public void Disconnect()
    {
        _isRunning = false;
        _client.Disconnect();
        _pumpThread?.Join(TimeSpan.FromSeconds(2));
    }

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        Console.WriteLine("[Steam] Connected to Steam network");
        _connected.TrySetResult();
        if (_logOnDetails != null)
        {
            Console.WriteLine($"[Steam] Re-issuing logon as {_logOnDetails.Username}...");
            _user.LogOn(_logOnDetails);
        }
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        if (!_isRunning) return;
        Console.WriteLine("[Steam] Disconnected from Steam, reconnecting in 5s...");
        IsLoggedIn = false;
        // Reset gates so future awaits block until the next reconnect/logon completes.
        _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loggedOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _licensesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Delay(5000).ContinueWith(_ => { if (_isRunning) _client.Connect(); });
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
        {
            Console.Error.WriteLine($"[Steam] Login failed: {cb.Result}");
            if (cb.Result is EResult.InvalidPassword or EResult.Expired or EResult.AccessDenied)
            {
                // Saved refresh token is no longer valid — stop retrying so we don't
                // hammer Steam with a token it keeps rejecting. Operator must restart
                // the process and re-auth interactively to mint a new refresh token.
                Console.Error.WriteLine("[Steam] Refresh token rejected. Deleting saved credentials and exiting reconnect loop — restart and re-auth interactively.");
                if (File.Exists(_credentialsPath))
                    File.Delete(_credentialsPath);
                _logOnDetails = null;
                _isRunning = false;
                _client.Disconnect();
            }
            else if (cb.Result is EResult.TryAnotherCM or EResult.ServiceUnavailable)
            {
                // Force disconnect so SteamKit2 picks a different CM server on reconnect
                _client.Disconnect();
            }
            return;
        }

        Console.WriteLine($"[Steam] Logged in as {_client.SteamID}");
        IsLoggedIn = true;
        _loggedOn.TrySetResult();
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Console.WriteLine($"[Steam] Logged off: {cb.Result}");
        IsLoggedIn = false;
        // Reset so the next logon waits for a fresh LicenseListCallback before issuing tickets.
        _licensesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void OnLicenseList(SteamApps.LicenseListCallback cb)
    {
        Console.WriteLine($"[Steam] License list received ({cb.LicenseList.Count} licenses)");
        _licensesReceived.TrySetResult();
    }

    private SavedCredentials? LoadCredentials()
    {
        if (!File.Exists(_credentialsPath)) return null;
        try
        {
            var json = File.ReadAllText(_credentialsPath);
            return JsonSerializer.Deserialize<SavedCredentials>(json);
        }
        catch { return null; }
    }

    private void SaveCredentials(SavedCredentials creds)
    {
        File.WriteAllText(_credentialsPath, JsonSerializer.Serialize(creds));
        Console.WriteLine("[Steam] Credentials saved for next run.");
    }

    private static string ReadPassword()
    {
        try
        {
            // Try masked input
            var password = "";
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                    password = password[..^1];
                else if (!char.IsControl(key.KeyChar))
                    password += key.KeyChar;
            }
            return password;
        }
        catch (InvalidOperationException)
        {
            // Fallback for redirected input (will echo password)
            return Console.ReadLine()?.Trim() ?? "";
        }
    }
}

class SavedCredentials
{
    public string Username { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

// =============================================================================
// Persistent WebSocket Connection
// =============================================================================
class PersistentConnection
{
    public string Version { get; }
    private readonly string _url;
    private readonly Func<Task<string?>> _ticketProvider;
    private SioClient? _socket;
    private bool _authenticated;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // SocketIOClient 3.1.1's ConnectAsync() takes no CancellationToken, and with
    // Reconnection enabled it retries a dead host forever. Nothing below is awaited
    // without a ceiling, or one decommissioned master freezes every scan (it did:
    // 28 hours of no data, process alive at 0% CPU with every thread parked).
    private static readonly TimeSpan ConnectCeiling = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectCeiling = TimeSpan.FromSeconds(5);

    // Same expired-certificate bypass the websocket needs, see InsecureClientWebSocket.
    private static readonly HttpClient _probeHttp = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true
        })
    { Timeout = TimeSpan.FromSeconds(8) };

    public bool IsConnected => _socket?.Connected == true && _authenticated;

    public PersistentConnection(string version, string url, Func<Task<string?>> ticketProvider)
    {
        Version = version;
        _url = url;
        _ticketProvider = ticketProvider;
    }

    public async Task EnsureConnected()
    {
        if (IsConnected) return;

        await _connectLock.WaitAsync();
        try
        {
            if (IsConnected) return;

            await Teardown();

            // A decommissioned master still resolves in DNS and can still accept TCP, so
            // "connect and see" is not safe here -- ConnectAsync would retry it forever
            // while holding _connectLock, stalling this scan and every one after it.
            // Probe the handshake first, with a real timeout, and skip the host instead.
            if (!await IsReachable())
            {
                _authenticated = false;
                return;
            }

            _socket = new SioClient(_url, new SocketIOOptions
            {
                AutoUpgrade = false,
                Reconnection = true,
                ReconnectionDelay = 5000,
                ReconnectionDelayMax = 30000,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
            });

            // SSL cert bypass for the master server's expired cert
            _socket.ClientWebSocketProvider = () => new InsecureClientWebSocket();
            var defaultHttp = new SocketIOClient.Transport.Http.DefaultHttpClient();
            var httpField = defaultHttp.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(System.Net.Http.HttpClient));
            httpField?.SetValue(defaultHttp, new System.Net.Http.HttpClient(
                new System.Net.Http.HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true
                }));
            _socket.HttpClient = defaultHttp;

            _socket.OnDisconnected += (sender, reason) =>
            {
                Console.WriteLine($"[{Version}] Disconnected: {reason}");
                _authenticated = false;
            };

            _socket.OnReconnected += async (sender, attempt) =>
            {
                Console.WriteLine($"[{Version}] Reconnected (attempt {attempt}), re-authenticating...");
                await Authenticate();
            };

            _socket.OnError += (sender, error) =>
            {
                Console.Error.WriteLine($"[{Version}] Socket error: {error}");
            };

            try
            {
                // Belt and braces behind the probe: a host that passes the handshake can
                // still stall the websocket upgrade, and that must not become permanent.
                var connect = _socket.ConnectAsync();
                if (await Task.WhenAny(connect, Task.Delay(ConnectCeiling)) != connect)
                {
                    Console.Error.WriteLine($"[{Version}] Connect exceeded {ConnectCeiling.TotalSeconds:0}s, abandoning this attempt");
                    await Teardown();
                    return;
                }
                await connect;
                Console.WriteLine($"[{Version}] Connected to {_url}");
                await Authenticate();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{Version}] Connection failed: {ex.Message}");
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // Disposing is what actually stops the library's internal reconnect loop, and it must
    // happen even when DisconnectAsync hangs against a host that stopped answering.
    private async Task Teardown()
    {
        if (_socket == null) return;
        try
        {
            var disconnect = _socket.DisconnectAsync();
            await Task.WhenAny(disconnect, Task.Delay(DisconnectCeiling));
        }
        catch { }
        try { _socket.Dispose(); } catch { }
        _socket = null;
        _authenticated = false;
    }

    // A live master answers the socket.io polling handshake with 0{"sid":"..."}; a
    // decommissioned one refuses the connection or never replies.
    private async Task<bool> IsReachable()
    {
        var probeUrl = _url.Replace("wss://", "https://").Replace("ws://", "http://").TrimEnd('/')
            + "/socket.io/?EIO=4&transport=polling";
        try
        {
            using var resp = await _probeHttp.GetAsync(probeUrl);
            var body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("\"sid\"")) return true;

            // Answered, but not with a handshake. Log what it said, or "not answering" is all we get.
            var snippet = body.Length > 200 ? body[..200] : body;
            Console.Error.WriteLine($"[{Version}] Master {_url} answered without a handshake: HTTP {(int)resp.StatusCode} {snippet}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Version}] Master {_url} not answering: {ex.GetType().Name}");
            return false;
        }
    }

    private async Task Authenticate()
    {
        if (_socket == null || !_socket.Connected) return;

        // Mint a FRESH ticket on every (re)auth — a ticket reused across days is
        // what causes the master to silently stop answering (see the ctor comment).
        var ticket = await _ticketProvider();
        if (string.IsNullOrEmpty(ticket))
        {
            Console.Error.WriteLine($"[{Version}] Could not obtain a fresh auth ticket");
            _authenticated = false;
            return;
        }

        var tcs = new TaskCompletionSource<bool>();
        var authData = new Dictionary<string, object> { { "ticket", ticket } };
        Action<SocketIOResponse> authAck = response =>
        {
            Console.WriteLine($"[{Version}] Authenticated");
            _authenticated = true;
            tcs.TrySetResult(true);
        };

        await _socket.EmitAsync("playerAuthenticateRequest", authAck, authData);

        if (await Task.WhenAny(tcs.Task, Task.Delay(10000)) != tcs.Task)
        {
            Console.Error.WriteLine($"[{Version}] Auth timed out");
            _authenticated = false;
        }
    }

    // Tear down the socket entirely so the next EnsureConnected() builds a brand-new
    // connection and re-authenticates with a fresh ticket — i.e. exactly what a process
    // restart does. Used to recover a session the master has silently expired.
    public async Task ForceReconnect()
    {
        await _connectLock.WaitAsync();
        try
        {
            await Teardown();
        }
        finally
        {
            _connectLock.Release();
        }
        await EnsureConnected();
    }

    public async Task<List<ServerInfo>> RequestServerList(
        string requestEvent, Func<SocketIOResponse, Task<List<ServerInfo>>> parser)
    {
        // Two attempts: if the first request times out, the master has likely expired
        // our session, so force a full reconnect (fresh ticket) and try once more.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (!IsConnected)
            {
                await EnsureConnected();
                if (!IsConnected)
                    return new List<ServerInfo>();
            }

            var tcs = new TaskCompletionSource<List<ServerInfo>>();

            Action<SocketIOResponse> ackHandler = response =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var parsed = await parser(response);
                        tcs.TrySetResult(parsed);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[{Version}] Parse error: {ex.Message}");
                        tcs.TrySetResult(new List<ServerInfo>());
                    }
                });
            };

            Console.WriteLine($"[{Version}] Requesting server list...");
            await _socket!.EmitAsync(requestEvent, ackHandler, (object?)null);

            if (await Task.WhenAny(tcs.Task, Task.Delay(15000)) == tcs.Task)
                return await tcs.Task;

            if (attempt == 1)
            {
                Console.Error.WriteLine($"[{Version}] Server list request timed out — forcing fresh reconnect + re-auth");
                await ForceReconnect();
            }
            else
            {
                Console.Error.WriteLine($"[{Version}] Server list request timed out after reconnect");
            }
        }

        return new List<ServerInfo>();
    }

    public async Task Disconnect()
    {
        // Bounded via Teardown: this runs on the exit-for-restart path, and an unbounded
        // DisconnectAsync against a wedged socket would keep systemd from ever restarting us.
        if (_socket != null)
        {
            await Teardown();
            Console.WriteLine($"[{Version}] Disconnected.");
        }
    }
}

// SSL cert bypass for the master server's expired cert
class InsecureClientWebSocket : SocketIOClient.Transport.WebSockets.DefaultClientWebSocket
{
    public InsecureClientWebSocket()
    {
        foreach (var field in typeof(SocketIOClient.Transport.WebSockets.DefaultClientWebSocket)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            if (field.FieldType == typeof(System.Net.WebSockets.ClientWebSocket))
            {
                if (field.GetValue(this) is System.Net.WebSockets.ClientWebSocket ws)
                    ws.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true;
                break;
            }
        }
    }
}
