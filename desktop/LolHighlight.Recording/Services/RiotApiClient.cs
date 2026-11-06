using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface IRiotApiClient
{
    Task<Dictionary<string, object>> GetGameStatsAsync(CancellationToken cancellationToken = default);
    Task<PlayerIdentity> GetActivePlayerAsync(CancellationToken cancellationToken = default);
    Task<Dictionary<string, object>> GetActivePlayerDataAsync(CancellationToken cancellationToken = default);
    Task<List<Dictionary<string, object>>> GetPlayerListAsync(CancellationToken cancellationToken = default);
    Task<List<Dictionary<string, object>>> GetEventsAsync(int? nextEventId = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, object>> GetOpenApiSummaryAsync(CancellationToken cancellationToken = default);
}

public interface ILiveGameSnapshotSource
{
    Task<LiveGameData> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class RiotApiClient : IRiotApiClient, ILiveGameSnapshotSource, IDisposable
{
    private const string BaseUrl = "https://127.0.0.1:2999";
    private readonly HttpClient _client;

    public RiotApiClient() : this(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        // The Live Client API uses a self-signed certificate on loopback only.
        ServerCertificateCustomValidationCallback = (request, _, _, _) =>
            request.RequestUri is { IsLoopback: true, Port: 2999 }
    })
    { }

    internal RiotApiClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
    }

    private async Task<T> GetAsync<T>(string path, Dictionary<string, string>? query = null, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}{path}";
        if (query != null)
        {
            var queryString = string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{queryString}";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");

            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = System.Text.Json.JsonSerializer.Deserialize<T>(content);

            if (result == null)
                throw new InvalidOperationException($"Deserialization returned null for {path}");

            if (result is Dictionary<string, object> dictionary)
                return (T)(object)NormalizeDictionary(dictionary);
            if (result is List<Dictionary<string, object>> dictionaries)
                return (T)(object)dictionaries.Select(NormalizeDictionary).ToList();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new RiotClientUnavailable($"{path}: {ex.Message}", ex);
        }
    }

    public async Task<Dictionary<string, object>> GetGameStatsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<Dictionary<string, object>>("/liveclientdata/gamestats", cancellationToken: cancellationToken);
    }

    public async Task<PlayerIdentity> GetActivePlayerAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetActivePlayerDataAsync(cancellationToken);
        return ParsePlayer(data);
    }

    internal static PlayerIdentity ParsePlayer(Dictionary<string, object> data)
    {
        var riotId = data.GetValueOrDefault("riotId")?.ToString()
            ?? data.GetValueOrDefault("summonerName")?.ToString()
            ?? "";

        var gameName = data.GetValueOrDefault("riotIdGameName")?.ToString()
            ?? riotId.Split('#')[0];

        var tagLine = data.GetValueOrDefault("riotIdTagLine")?.ToString() ?? "";

        if (string.IsNullOrEmpty(gameName))
            throw new RiotClientUnavailable("active player identity is missing");

        return new PlayerIdentity(riotId, gameName, tagLine);
    }

    public async Task<LiveGameData> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => new(await GetAsync<Dictionary<string, object>>("/liveclientdata/allgamedata", cancellationToken: cancellationToken));

    public async Task<Dictionary<string, object>> GetActivePlayerDataAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<Dictionary<string, object>>("/liveclientdata/activeplayer", cancellationToken: cancellationToken);
    }

    public async Task<List<Dictionary<string, object>>> GetPlayerListAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<List<Dictionary<string, object>>>("/liveclientdata/playerlist", cancellationToken: cancellationToken);
    }

    public async Task<List<Dictionary<string, object>>> GetEventsAsync(int? nextEventId = null, CancellationToken cancellationToken = default)
    {
        var query = nextEventId.HasValue
            ? new Dictionary<string, string> { { "eventID", nextEventId.Value.ToString() } }
            : null;

        var data = await GetAsync<Dictionary<string, object>>("/liveclientdata/eventdata", query, cancellationToken);

        if (data.TryGetValue("Events", out var eventsObj) && eventsObj is IEnumerable<object> events)
        {
            return events
                .OfType<Dictionary<string, object>>()
                .ToList();
        }

        return new List<Dictionary<string, object>>();
    }

    public async Task<Dictionary<string, object>> GetOpenApiSummaryAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetAsync<Dictionary<string, object>>("/swagger/v3/openapi.json", cancellationToken: cancellationToken);

        var paths = data.GetValueOrDefault("paths") as Dictionary<string, object> ?? new Dictionary<string, object>();
        var info = data.GetValueOrDefault("info") as Dictionary<string, object> ?? new Dictionary<string, object>();

        return new Dictionary<string, object>
        {
            ["openapi"] = data.GetValueOrDefault("openapi")!,
            ["title"] = info.GetValueOrDefault("title")!,
            ["path_count"] = paths.Count
        };
    }

    private static Dictionary<string, object> NormalizeDictionary(Dictionary<string, object> source)
        => source.ToDictionary(pair => pair.Key,
            pair => pair.Value is System.Text.Json.JsonElement element ? JsonValues.Normalize(element)! : pair.Value);

    public void Dispose() => _client.Dispose();
}

public class RiotClientUnavailable : Exception
{
    public RiotClientUnavailable(string message) : base(message) { }
    public RiotClientUnavailable(string message, Exception inner) : base(message, inner) { }
}
