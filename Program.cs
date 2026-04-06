using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using SocketIOClient;
using SteamKit2;
using SteamKit2.Authentication;
using SioClient = SocketIOClient.SocketIO;

const uint PUCK_APP_ID = 2994020;
const int POLL_INTERVAL_MS = 15 * 60 * 1000; // 15 minutes
bool runOnce = args.Contains("--once");

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
var b202Conn = new PersistentConnection("b202", "wss://puck1.nasejevs.com", ticket);
var b312Conn = new PersistentConnection("b312", "wss://puck2.nasejevs.com", ticket);

Console.WriteLine($"PuckServerTracker started (mode: {(runOnce ? "single scan" : "continuous, every 15 min")})");

await Task.WhenAll(b202Conn.EnsureConnected(), b312Conn.EnsureConnected());

try
{
    do
    {
        steamSession.RunCallbacks();
        await Scan(db, b202Conn, b312Conn);

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
    await b202Conn.Disconnect();
    await b312Conn.Disconnect();
    steamSession.Disconnect();
    Console.WriteLine("[Steam] Disconnected.");
}

return 0;

// =============================================================================
// Scan
// =============================================================================
async Task Scan(NpgsqlDataSource db, PersistentConnection b202, PersistentConnection b312)
{
    var timestamp = DateTime.UtcNow.ToString("o");
    Console.WriteLine($"\n{"".PadRight(60, '=')}");
    Console.WriteLine($"Scan at {timestamp}");
    Console.WriteLine("".PadRight(60, '='));

    await Task.WhenAll(b202.EnsureConnected(), b312.EnsureConnected());

    var b202Task = b202.RequestServerList("playerGetServerBrowserServersRequest", ParseB202Response);
    var b312Task = b312.RequestServerList("playerGetServerBrowserEndPointsRequest", ParseB312Response);

    await Task.WhenAll(b202Task, b312Task);

    await ProcessResult("b202", b202Task, db, timestamp);
    await ProcessResult("b312", b312Task, db, timestamp);
}

async Task ProcessResult(string version, Task<List<ServerInfo>> task, NpgsqlDataSource db, string timestamp)
{
    try
    {
        var servers = await task;
        EnrichWithGeoIP(servers);
        PrintServers(version, servers);
        await SaveSnapshot(db, version, servers, timestamp);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{version}] Failed: {ex.Message}");
    }
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
    private readonly SteamAuthTicket _authTicket;

    private bool _isRunning;
    public bool IsLoggedIn { get; private set; }

    private readonly string _credentialsPath = Path.Combine(AppContext.BaseDirectory, "steam_credentials.json");

    public SteamSession()
    {
        _client = new SteamClient();
        _callbacks = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _authTicket = _client.GetHandler<SteamAuthTicket>()!;

        _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
    }

    public async Task Login()
    {
        Console.WriteLine("[Steam] Connecting to Steam...");
        _isRunning = true;
        _client.Connect();

        // Wait for connection
        while (_isRunning && !_client.IsConnected)
        {
            _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
        }

        if (!_client.IsConnected)
        {
            Console.Error.WriteLine("[Steam] Failed to connect to Steam network");
            return;
        }

        // Try saved credentials first
        var savedCreds = LoadCredentials();
        if (savedCreds != null)
        {
            Console.WriteLine($"[Steam] Logging in as {savedCreds.Username} (saved credentials)...");
            _user.LogOn(new SteamUser.LogOnDetails
            {
                Username = savedCreds.Username,
                AccessToken = savedCreds.RefreshToken,
            });
        }
        else
        {
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
            _user.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResult.AccountName,
                AccessToken = pollResult.RefreshToken,
            });

            // Save for next time
            SaveCredentials(new SavedCredentials
            {
                Username = pollResult.AccountName,
                RefreshToken = pollResult.RefreshToken,
            });
        }

        // Wait for login result
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!IsLoggedIn && DateTime.UtcNow < deadline)
        {
            _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
        }
    }

    public async Task<string?> GetAuthTicket(uint appId)
    {
        try
        {
            Console.WriteLine($"[Steam] Requesting auth ticket for app {appId}...");
            var ticketInfo = await _authTicket.GetAuthTicketForWebApi(appId, "puck");
            return Convert.ToHexString(ticketInfo.Ticket);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Steam] Auth ticket failed: {ex.Message}");
            // If using saved creds that expired, delete them and prompt re-login
            if (File.Exists(_credentialsPath))
            {
                Console.Error.WriteLine("[Steam] Deleting saved credentials, please restart and login again.");
                File.Delete(_credentialsPath);
            }
            return null;
        }
    }

    public void RunCallbacks()
    {
        _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(1));
    }

    public void Disconnect()
    {
        _isRunning = false;
        _client.Disconnect();
    }

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        Console.WriteLine("[Steam] Connected to Steam network");
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        if (!_isRunning) return;
        Console.WriteLine("[Steam] Disconnected from Steam, reconnecting in 5s...");
        IsLoggedIn = false;
        Task.Delay(5000).ContinueWith(_ => _client.Connect());
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
        {
            Console.Error.WriteLine($"[Steam] Login failed: {cb.Result}");
            if (cb.Result is EResult.InvalidPassword or EResult.Expired or EResult.AccessDenied)
            {
                // Delete bad saved credentials
                if (File.Exists(_credentialsPath))
                    File.Delete(_credentialsPath);
            }
            return;
        }

        Console.WriteLine($"[Steam] Logged in as {_client.SteamID}");
        IsLoggedIn = true;
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Console.WriteLine($"[Steam] Logged off: {cb.Result}");
        IsLoggedIn = false;
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

// SSL cert bypass for puck1's expired cert
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
