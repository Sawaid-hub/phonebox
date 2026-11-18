using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record SteamFriend(string Id, string Name, int State, string? Game, string? GameId, string? Avatar, long? LastLogoff);
public sealed record SteamActivityEvent(string Id, string Name, string? Avatar, string Kind, string? Game, long At);
public sealed record SteamSnapshot(bool Connected, string? Error, int TotalFriends, int OnlineCount,
    long? Updated, IReadOnlyList<SteamFriend> Friends, IReadOnlyList<SteamActivityEvent> Activity);

/// <summary>Reads a user's friend list and presence from Steam's Web API while an enabled page contains the widget.</summary>
public sealed class SteamMonitor(ConfigStore config, ILogger<SteamMonitor> log) : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient AvatarHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FriendsEvery = TimeSpan.FromMinutes(15);
    private const int SummaryBatchSize = 100;
    private const int AvatarMaxBytes = 512 * 1024;
    private const long AvatarCacheLimit = 8 * 1024 * 1024;

    private volatile SteamSnapshot _snapshot = Empty(null);
    private string? _steamId;
    private string? _apiKey;
    private string? _resolvedAccountId;
    private string[] _friendIds = [];
    private DateTime _friendsAt = DateTime.MinValue;
    private DateTime _nextPoll = DateTime.MinValue;
    private readonly Dictionary<string, (int State, string? Game, string? GameId)> _lastPresence = new(StringComparer.Ordinal);
    private readonly List<SteamActivityEvent> _activity = [];
    private bool _hasPresenceBaseline;
    private readonly ConcurrentDictionary<string, (byte[] Data, string Type)> _avatars = new(StringComparer.Ordinal);
    private long _avatarBytes;

    private static SteamSnapshot Empty(string? error) => new(false, error, 0, 0, null, [], []);

    internal static bool WidgetWanted(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type is "steam" or "steam-activity");

    public SteamSnapshot Snapshot()
    {
        var cfg = config.Get();
        return cfg.Steam.Enabled && WidgetWanted(cfg) ? _snapshot : Empty(null);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = config.Get();
            var steam = cfg.Steam;
            if (!steam.Enabled || !WidgetWanted(cfg))
            {
                _snapshot = Empty(null);
                _steamId = _apiKey = null;
                _resolvedAccountId = null;
                _friendIds = [];
                _friendsAt = DateTime.MinValue;
                _nextPoll = DateTime.MinValue;
                _lastPresence.Clear();
                _activity.Clear();
                _hasPresenceBaseline = false;
                _avatars.Clear();
                Interlocked.Exchange(ref _avatarBytes, 0);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            var id = steam.SteamId?.Trim() ?? "";
            var key = steam.ApiKey?.Trim() ?? "";
            if (id != _steamId || key != _apiKey)
            {
                _steamId = id;
                _apiKey = key;
                _resolvedAccountId = null;
                _friendIds = [];
                _friendsAt = DateTime.MinValue;
                _nextPoll = DateTime.MinValue;
                _lastPresence.Clear();
                _activity.Clear();
                _hasPresenceBaseline = false;
                _avatars.Clear();
                Interlocked.Exchange(ref _avatarBytes, 0);
                _snapshot = Empty(null);
            }

            if (id.Length == 0 || key.Length == 0)
            {
                _snapshot = Empty("Add your SteamID64 and Web API key in Settings › General › Steam");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            if (DateTime.UtcNow >= _nextPoll)
            {
                try
                {
                    _resolvedAccountId ??= await ResolveAccountId(id, key, ct);
                    await Refresh(_resolvedAccountId, key, ct);
                    _nextPoll = DateTime.UtcNow + PollEvery;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var message = ex is HttpRequestException ? "Could not reach Steam" : ex.Message;
                    _snapshot = _snapshot with { Connected = false, Error = message };
                    _nextPoll = DateTime.UtcNow.AddMinutes(2);
                    log.LogInformation("steam: {Msg}", message);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task Refresh(string steamId, string apiKey, CancellationToken ct)
    {
        if (_friendsAt == DateTime.MinValue || DateTime.UtcNow - _friendsAt >= FriendsEvery)
        {
            var list = await Request("ISteamUser/GetFriendList/v1/", $"steamid={Uri.EscapeDataString(steamId)}&relationship=friend", apiKey, ct);
            var ids = list["friendslist"]?["friends"]?.AsArray()
                .Select(f => Text(f?["steamid"]))
                .OfType<string>()
                .Where(s => s.Length == 17 && s.All(char.IsAsciiDigit))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            _friendIds = ids;
            _friendsAt = DateTime.UtcNow;
        }

        var friends = new List<SteamFriend>();
        for (var i = 0; i < _friendIds.Length; i += SummaryBatchSize)
        {
            var batch = _friendIds.Skip(i).Take(SummaryBatchSize);
            var ids = string.Join(',', batch.Select(Uri.EscapeDataString));
            var summaries = await Request("ISteamUser/GetPlayerSummaries/v2/", $"steamids={ids}", apiKey, ct);
            foreach (var p in summaries["response"]?["players"]?.AsArray() ?? [])
            {
                var id = Text(p?["steamid"]);
                if (id is null) continue;
                var state = Number(p?["personastate"]);
                var lastLogoff = Number(p?["lastlogoff"]);
                friends.Add(new(id,
                    Text(p?["personaname"]) ?? "Steam user",
                    state,
                    Text(p?["gameextrainfo"]),
                    Text(p?["gameid"]),
                    AvatarUrl(p),
                    lastLogoff > 0 ? lastLogoff : null));
            }
        }

        var ordered = friends
            .OrderByDescending(f => f.State > 0)
            .ThenByDescending(f => !string.IsNullOrWhiteSpace(f.Game))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RecordActivity(ordered);
        var online = ordered.Count(f => f.State > 0);
        _snapshot = new(true, null, _friendIds.Length, online,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ordered, _activity.ToArray());
    }

    private void RecordActivity(IReadOnlyList<SteamFriend> friends)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var friend in friends)
        {
            var current = (friend.State, friend.Game, friend.GameId);
            if (_hasPresenceBaseline && _lastPresence.TryGetValue(friend.Id, out var previous))
            {
                var changedGame = !string.IsNullOrWhiteSpace(friend.Game)
                    && (friend.Game != previous.Game || friend.GameId != previous.GameId);
                var kind = changedGame
                    ? "playing"
                    : previous.State == 0 && friend.State > 0 ? "online"
                    : previous.State > 0 && friend.State == 0 ? "offline"
                    : null;
                if (kind is not null)
                {
                    _activity.Insert(0, new(friend.Id, friend.Name, friend.Avatar, kind, friend.Game, now));
                    if (_activity.Count > 20) _activity.RemoveRange(20, _activity.Count - 20);
                }
            }
            _lastPresence[friend.Id] = current;
        }
        _hasPresenceBaseline = true;
    }

    /// <summary>Fetch only an avatar URL reported by the current Steam snapshot; the phone never contacts the image CDN.</summary>
    public async Task<(byte[] Data, string Type)?> AvatarAsync(string id, CancellationToken ct)
    {
        var cfg = config.Get();
        if (!cfg.Steam.Enabled || !WidgetWanted(cfg) || id.Length != 17 || !id.All(char.IsAsciiDigit)) return null;
        var url = _snapshot.Friends.FirstOrDefault(f => f.Id == id)?.Avatar;
        if (url is null || !IsSafeAvatarUrl(url)) return null;
        if (_avatars.TryGetValue(url, out var hit)) return hit;
        try
        {
            using var response = await AvatarHttp.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > AvatarMaxBytes) return null;
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")) return null;
            var data = await response.Content.ReadAsByteArrayAsync(ct);
            if (data.Length is 0 or > AvatarMaxBytes) return null;
            if (Interlocked.Add(ref _avatarBytes, data.Length) > AvatarCacheLimit)
            {
                _avatars.Clear();
                Interlocked.Exchange(ref _avatarBytes, data.Length);
            }
            return _avatars[url] = (data, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    private static string? SafeAvatarUrl(string? value) => IsSafeAvatarUrl(value) ? value : null;

    private static string? AvatarUrl(JsonNode? player)
    {
        foreach (var key in new[] { "avatarfull", "avatarmedium", "avatar" })
            if (SafeAvatarUrl(Text(player?[key])) is { } url) return url;
        return null;
    }

    private static bool IsSafeAvatarUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase));

    private static async Task<string> ResolveAccountId(string input, string apiKey, CancellationToken ct)
    {
        if (input.Length == 17 && input.All(char.IsAsciiDigit)) return input;
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Enter a SteamID64 or a steamcommunity.com profile link");
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("profiles", StringComparison.OrdinalIgnoreCase)
                && parts[1].Length == 17 && parts[1].All(char.IsAsciiDigit)) return parts[1];
            if (parts.Length >= 2 && parts[0].Equals("id", StringComparison.OrdinalIgnoreCase)) value = Uri.UnescapeDataString(parts[1]);
            else throw new InvalidOperationException("That Steam profile link does not contain a public profile ID");
        }
        if (value.Length == 0 || value.Contains('/') || value.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Enter a SteamID64 or a Steam custom profile link");
        var resolved = await Request("ISteamUser/ResolveVanityURL/v1/", $"vanityurl={Uri.EscapeDataString(value)}", apiKey, ct);
        var result = resolved["response"];
        var id = Text(result?["steamid"]);
        if (Number(result?["success"]) != 1 || id is not { Length: 17 } || !id.All(char.IsAsciiDigit))
            throw new InvalidOperationException("Steam could not find that custom profile URL");
        return id;
    }

    private static async Task<JsonNode> Request(string method, string query, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steampowered.com/{method}?{query}");
        request.Headers.TryAddWithoutValidation("x-webapi-key", apiKey);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (method.Contains("GetFriendList", StringComparison.Ordinal))
                throw new InvalidOperationException("Steam could not read the friend list. Check the API key and profile, and set your Steam friend list to Public.");
            throw new InvalidOperationException("Steam rejected the Web API key. Check it in Settings › General › Steam.");
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Steam Web API returned HTTP {(int)response.StatusCode}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException("Steam returned an empty response");
    }

    private static string? Text(JsonNode? value)
    {
        try { return value?.GetValue<string>(); }
        catch { return value?.ToString(); }
    }

    private static int Number(JsonNode? value)
    {
        try { return value?.GetValue<int>() ?? 0; }
        catch { return int.TryParse(value?.ToString(), out var n) ? n : 0; }
    }
}
