using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Steamworks;
using SocketIOClient;
using SioClient = SocketIOClient.SocketIO;

const int POLL_INTERVAL_MS = 1 * 60 * 1000; // 1 minute (testing)
bool runOnce = args.Contains("--once");

// --- Steam Init ---
Console.WriteLine("[Steam] Initializing Steamworks...");
if (!SteamAPI.Init())
{
    Console.Error.WriteLine("[Steam] SteamAPI.Init() failed. Is Steam running? Do you own Puck (AppId 2994020)?");
    return 1;
}
Console.WriteLine($"[Steam] Initialized. Logged in as: {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()})");

// --- SQLite Init ---
var dbPath = Path.Combine(AppContext.BaseDirectory, "data", "servers.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
using var db = new SqliteConnection($"Data Source={dbPath}");
db.Open();
InitializeDatabase(db);
Console.WriteLine($"[DB] Database at {dbPath}");

// --- Get initial auth ticket ---
string? ticket = await GetAuthTicket();
if (ticket == null)
{
    Console.Error.WriteLine("[Steam] Failed to get auth ticket");
    SteamAPI.Shutdown();
    return 1;
}
Console.WriteLine($"[Steam] Got auth ticket ({ticket.Length / 2} bytes)");

// --- Create persistent connections ---
var b202Conn = new PersistentConnection("b202", "wss://puck1.nasejevs.com", ticket);
var b312Conn = new PersistentConnection("b312", "wss://puck2.nasejevs.com", ticket);

Console.WriteLine($"PuckServerTracker started (mode: {(runOnce ? "single scan" : "continuous, every 1 min")})");

// --- Connect both ---
await Task.WhenAll(b202Conn.EnsureConnected(), b312Conn.EnsureConnected());

try
{
    do
    {
        SteamAPI.RunCallbacks();
        await Scan(db, b202Conn, b312Conn);

        if (!runOnce)
        {
            Console.WriteLine($"\nNext scan in 1 minute... (press Ctrl+C to exit)");
            for (int i = 0; i < POLL_INTERVAL_MS / 1000; i++)
            {
                await Task.Delay(1000);
                SteamAPI.RunCallbacks();
            }
        }
    } while (!runOnce);
}
finally
{
    await b202Conn.Disconnect();
    await b312Conn.Disconnect();
    SteamAPI.Shutdown();
    Console.WriteLine("[Steam] Shutdown.");
}

return 0;

// =============================================================================
// Scan - uses persistent connections, reconnects if needed
// =============================================================================
async Task Scan(SqliteConnection db, PersistentConnection b202, PersistentConnection b312)
{
    var timestamp = DateTime.UtcNow.ToString("o");
    Console.WriteLine($"\n{"".PadRight(60, '=')}");
    Console.WriteLine($"Scan at {timestamp}");
    Console.WriteLine("".PadRight(60, '='));

    // Ensure both are connected (reconnects if dropped)
    await Task.WhenAll(b202.EnsureConnected(), b312.EnsureConnected());

    // Query both in parallel
    var b202Task = b202.RequestServerList("playerGetServerBrowserServersRequest", ParseB202Response);
    var b312Task = b312.RequestServerList("playerGetServerBrowserEndPointsRequest", ParseB312Response);

    await Task.WhenAll(b202Task, b312Task);

    // Process results sequentially (SQLite)
    await ProcessResult("b202", b202Task, db, timestamp);
    await ProcessResult("b312", b312Task, db, timestamp);
}

async Task ProcessResult(string version, Task<List<ServerInfo>> task, SqliteConnection db, string timestamp)
{
    try
    {
        var servers = await task;
        EnrichWithGeoIP(servers);
        PrintServers(version, servers);
        SaveSnapshot(db, version, servers, timestamp);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{version}] Failed: {ex.Message}");
    }
}

// =============================================================================
// Steam Auth
// =============================================================================
async Task<string?> GetAuthTicket()
{
    var tcs = new TaskCompletionSource<string?>();

    Callback<GetTicketForWebApiResponse_t>.Create(response =>
    {
        var ticketBytes = response.m_rgubTicket;
        var hex = BitConverter.ToString(ticketBytes, 0, ticketBytes.Length).Replace("-", "");
        tcs.TrySetResult(hex);
    });

    SteamUser.GetAuthTicketForWebApi(null);

    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!tcs.Task.IsCompleted && DateTime.UtcNow < deadline)
    {
        SteamAPI.RunCallbacks();
        await Task.Delay(50);
    }

    if (!tcs.Task.IsCompleted)
        tcs.TrySetResult(null);

    return await tcs.Task;
}

// =============================================================================
// Response Parsers
// =============================================================================
Task<List<ServerInfo>> ParseB202Response(SocketIOResponse response)
{
    var json = response.GetValue<JsonElement>();
    var servers = new List<ServerInfo>();
    JsonElement serversArray;

    if (json.TryGetProperty("data", out var data) &&
        data.TryGetProperty("serverBrowserServers", out serversArray))
    { }
    else if (json.TryGetProperty("serverBrowserServers", out serversArray))
    { }
    else if (json.TryGetProperty("servers", out serversArray))
    { }
    else if (json.ValueKind == JsonValueKind.Array)
    { serversArray = json; }
    else
    {
        var raw = json.ToString();
        Console.Error.WriteLine($"[b202] Unexpected format: {raw[..Math.Min(200, raw.Length)]}");
        return Task.FromResult(servers);
    }

    foreach (var s in serversArray.EnumerateArray())
    {
        servers.Add(new ServerInfo
        {
            IpAddress = s.GetProperty("ipAddress").GetString() ?? "",
            Port = s.GetProperty("port").GetUInt16(),
            Name = s.TryGetProperty("name", out var n) ? n.GetString() : null,
            Players = s.TryGetProperty("players", out var p) ? p.GetInt32() : null,
            MaxPlayers = s.TryGetProperty("maxPlayers", out var m) ? m.GetInt32() : null,
            IsPasswordProtected = s.TryGetProperty("isPasswordProtected", out var pp) && pp.GetBoolean(),
        });
    }

    Console.WriteLine($"[b202] Parsed {servers.Count} servers");
    return Task.FromResult(servers);
}

async Task<List<ServerInfo>> ParseB312Response(SocketIOResponse response)
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
        Console.Error.WriteLine($"[b312] Unexpected format");
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

    // TCP preview each endpoint to get server details
    Console.WriteLine($"[b312] Fetching TCP previews for {endpoints.Count} endpoints...");
    await Task.WhenAll(endpoints.Select(TcpPreview));
    Console.WriteLine($"[b312] Parsed {endpoints.Count} servers");
    return endpoints;
}

async Task TcpPreview(ServerInfo server)
{
    try
    {
        using var client = new System.Net.Sockets.TcpClient();
        var connectTask = client.ConnectAsync(server.IpAddress, server.Port);
        if (await Task.WhenAny(connectTask, Task.Delay(2000)) != connectTask)
            return;

        using var stream = client.GetStream();
        stream.ReadTimeout = 2000;
        stream.WriteTimeout = 2000;

        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"type\":0}"));

        var buffer = new byte[4096];
        var bytesRead = await stream.ReadAsync(buffer);
        if (bytesRead == 0) return;

        var msg = JsonSerializer.Deserialize<JsonElement>(
            System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead));

        if (msg.TryGetProperty("type", out var type) && type.GetInt32() == 1)
        {
            server.Name = msg.TryGetProperty("name", out var n) ? n.GetString() : null;
            server.Players = msg.TryGetProperty("players", out var p) ? p.GetInt32() : null;
            server.MaxPlayers = msg.TryGetProperty("maxPlayers", out var m) ? m.GetInt32() : null;
            server.IsPasswordProtected = msg.TryGetProperty("isPasswordProtected", out var pp) && pp.GetBoolean();
            if (msg.TryGetProperty("clientRequiredModIds", out var mods) && mods.ValueKind == JsonValueKind.Array)
                server.ClientRequiredModIds = mods.EnumerateArray().Select(m => m.GetUInt64()).ToArray();
        }
    }
    catch { }
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
// SQLite
// =============================================================================
void InitializeDatabase(SqliteConnection db)
{
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS snapshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            version TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS servers (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
            ip_address TEXT NOT NULL,
            port INTEGER NOT NULL,
            name TEXT,
            raw_name TEXT,
            players INTEGER,
            max_players INTEGER,
            is_password_protected INTEGER DEFAULT 0,
            country TEXT, region TEXT, city TEXT,
            lat REAL, lon REAL,
            isp TEXT,
            client_required_mod_ids TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_servers_snapshot ON servers(snapshot_id);
        CREATE INDEX IF NOT EXISTS idx_snapshots_timestamp ON snapshots(timestamp);
    ";
    cmd.ExecuteNonQuery();
}

void SaveSnapshot(SqliteConnection db, string version, List<ServerInfo> servers, string timestamp)
{
    using var txn = db.BeginTransaction();

    using var insertSnapshot = db.CreateCommand();
    insertSnapshot.CommandText = "INSERT INTO snapshots (timestamp, version) VALUES ($ts, $ver); SELECT last_insert_rowid();";
    insertSnapshot.Parameters.AddWithValue("$ts", timestamp);
    insertSnapshot.Parameters.AddWithValue("$ver", version);
    var snapshotId = (long)insertSnapshot.ExecuteScalar()!;

    foreach (var s in servers)
    {
        using var ins = db.CreateCommand();
        ins.CommandText = @"
            INSERT INTO servers (snapshot_id, ip_address, port, name, raw_name, players, max_players,
                is_password_protected, country, region, city, lat, lon, isp, client_required_mod_ids)
            VALUES ($sid, $ip, $port, $name, $raw_name, $players, $max, $pw, $country, $region, $city, $lat, $lon, $isp, $mods)";
        ins.Parameters.AddWithValue("$sid", snapshotId);
        ins.Parameters.AddWithValue("$ip", s.IpAddress);
        ins.Parameters.AddWithValue("$port", s.Port);
        ins.Parameters.AddWithValue("$name", s.Name != null ? (object)StripRichText(s.Name) : DBNull.Value);
        ins.Parameters.AddWithValue("$raw_name", (object?)s.Name ?? DBNull.Value);
        ins.Parameters.AddWithValue("$players", (object?)s.Players ?? DBNull.Value);
        ins.Parameters.AddWithValue("$max", (object?)s.MaxPlayers ?? DBNull.Value);
        ins.Parameters.AddWithValue("$pw", s.IsPasswordProtected ? 1 : 0);
        ins.Parameters.AddWithValue("$country", (object?)s.Country ?? DBNull.Value);
        ins.Parameters.AddWithValue("$region", (object?)s.Region ?? DBNull.Value);
        ins.Parameters.AddWithValue("$city", (object?)s.City ?? DBNull.Value);
        ins.Parameters.AddWithValue("$lat", (object?)s.Lat ?? DBNull.Value);
        ins.Parameters.AddWithValue("$lon", (object?)s.Lon ?? DBNull.Value);
        ins.Parameters.AddWithValue("$isp", (object?)s.Isp ?? DBNull.Value);
        ins.Parameters.AddWithValue("$mods", s.ClientRequiredModIds is { Length: > 0 }
            ? (object)JsonSerializer.Serialize(s.ClientRequiredModIds)
            : DBNull.Value);
        ins.ExecuteNonQuery();
    }

    txn.Commit();
    Console.WriteLine($"[DB] Saved {servers.Count} servers for {version}");
}

// =============================================================================
// Utilities
// =============================================================================
string StripRichText(string text) =>
    Regex.Replace(text, @"<\/?[a-zA-Z][^>]*>", "").Trim();

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
// Persistent WebSocket Connection
// =============================================================================
class PersistentConnection
{
    public string Version { get; }
    private readonly string _url;
    private readonly string _ticket;
    private SioClient? _socket;
    private bool _authenticated;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public bool IsConnected => _socket?.Connected == true && _authenticated;

    public PersistentConnection(string version, string url, string ticket)
    {
        Version = version;
        _url = url;
        _ticket = ticket;
    }

    public async Task EnsureConnected()
    {
        if (IsConnected) return;

        await _connectLock.WaitAsync();
        try
        {
            if (IsConnected) return;

            // Clean up old socket
            if (_socket != null)
            {
                try { await _socket.DisconnectAsync(); } catch { }
                _socket.Dispose();
                _socket = null;
                _authenticated = false;
            }

            _socket = new SioClient(_url, new SocketIOOptions
            {
                AutoUpgrade = false,
                Reconnection = true,
                ReconnectionDelay = 5000,
                ReconnectionDelayMax = 30000,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
            });

            // SSL cert bypass for puck1's expired cert
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

            // Connect
            try
            {
                await _socket.ConnectAsync();
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

    private async Task Authenticate()
    {
        if (_socket == null || !_socket.Connected) return;

        var tcs = new TaskCompletionSource<bool>();
        var authData = new Dictionary<string, object> { { "ticket", _ticket } };
        Action<SocketIOResponse> authAck = response =>
        {
            Console.WriteLine($"[{Version}] Authenticated");
            _authenticated = true;
            tcs.TrySetResult(true);
        };

        await _socket.EmitAsync("playerAuthenticateRequest", authAck, authData);

        // Wait up to 10 seconds for auth
        if (await Task.WhenAny(tcs.Task, Task.Delay(10000)) != tcs.Task)
        {
            Console.Error.WriteLine($"[{Version}] Auth timed out");
            _authenticated = false;
        }
    }

    public async Task<List<ServerInfo>> RequestServerList(
        string requestEvent, Func<SocketIOResponse, Task<List<ServerInfo>>> parser)
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

        if (await Task.WhenAny(tcs.Task, Task.Delay(15000)) != tcs.Task)
        {
            Console.Error.WriteLine($"[{Version}] Server list request timed out");
            return new List<ServerInfo>();
        }

        return await tcs.Task;
    }

    public async Task Disconnect()
    {
        if (_socket != null)
        {
            try { if (_socket.Connected) await _socket.DisconnectAsync(); } catch { }
            _socket.Dispose();
            _socket = null;
            _authenticated = false;
            Console.WriteLine($"[{Version}] Disconnected.");
        }
    }
}

// DefaultClientWebSocket subclass that skips SSL certificate validation
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
