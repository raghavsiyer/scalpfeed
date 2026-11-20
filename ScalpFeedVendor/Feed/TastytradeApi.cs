using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

internal sealed class TastytradeConfigException : Exception
{
    public TastytradeConfigException(string message)
        : base(message)
    {
    }
}

internal sealed class TastytradeApiException : Exception
{
    public TastytradeApiException(string message)
        : base(message)
    {
    }
}

internal sealed record QuoteToken(string Token, string Url, string Level, DateTime FetchedUtc)
{
    /// <summary>tastytrade's real-time level. Anything else is delayed data.</summary>
    public bool IsRealTime => this.Level == "api";
}

/// <summary>Tick size in force below <see cref="Below"/>; infinity for the top band.</summary>
internal readonly record struct TickBand(double Below, double Size);

internal sealed record EquityInfo(string Symbol, string StreamerSymbol, string Description, bool IsEtf, IReadOnlyList<TickBand> TickBands);

internal sealed record SearchHit(string Symbol, string Description);

/// <summary>tastytrade REST calls. Slow by nature, so never used from the data path.</summary>
internal sealed class TastytradeApi : IDisposable
{
    private const string BaseUrl = "https://api.tastyworks.com";
    private const string Agent = "scalpfeed/0.2";

    // Too many failed logins blocks the IP for about 8 hours, and Quantower retries a failed
    // connect every 5.5 seconds. So after a refusal, that same refresh token isn't tried again for
    // a while. Static, because each connect attempt builds a new client. A new token in the file
    // is tried straight away.
    private static readonly TimeSpan RefusedPause = TimeSpan.FromMinutes(5);
    private static string refusedToken;
    private static DateTime refusedUntilUtc;

    private readonly string configPath;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private readonly JsonObject config;
    private string accessToken;
    private DateTime accessTokenExpiryUtc;

    public TastytradeApi(string configPath)
    {
        this.configPath = configPath;
        if (!File.Exists(configPath))
            throw new TastytradeConfigException($"Credentials file not found: {configPath}");

        try
        {
            this.config = JsonNode.Parse(File.ReadAllText(configPath)).AsObject();
        }
        catch (JsonException)
        {
            throw new TastytradeConfigException($"Credentials file isn't valid JSON: {configPath}");
        }

        foreach (var key in new[] { "client_id", "client_secret", "refresh_token" })
        {
            if (string.IsNullOrEmpty((string)this.config[key]))
                throw new TastytradeConfigException($"Credentials file has no {key}: {configPath}");
        }

        Log.AddSecret((string)this.config["client_secret"]);
        Log.AddSecret((string)this.config["refresh_token"]);
        this.http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Agent);
    }

    public void Dispose() => this.http.Dispose();

    public async Task<QuoteToken> GetQuoteTokenAsync(CancellationToken ct)
    {
        var json = await this.GetJsonAsync("/api-quote-tokens", ct).ConfigureAwait(false);
        var data = json["data"];
        var token = (string)data?["token"];
        var url = (string)data?["dxlink-url"];
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(url))
            throw new TastytradeApiException("Quote token response had no token or dxlink-url");

        Log.AddSecret(token);
        return new QuoteToken(token, url, (string)data["level"] ?? "unknown", DateTime.UtcNow);
    }

    public async Task<EquityInfo> GetEquityAsync(string symbol, CancellationToken ct)
    {
        var json = await this.GetJsonAsync("/instruments/equities/" + Uri.EscapeDataString(symbol), ct).ConfigureAwait(false);
        var d = json["data"];
        if (d == null)
            return null;

        var bands = new List<TickBand>();
        if (d["tick-sizes"] is JsonArray ticks)
        {
            foreach (var t in ticks)
            {
                double size = ParseDouble((string)t?["value"]);
                double below = ParseDouble((string)t?["threshold"]);
                if (!double.IsNaN(size))
                    bands.Add(new TickBand(double.IsNaN(below) ? double.PositiveInfinity : below, size));
            }
        }

        bands.Sort((a, b) => a.Below.CompareTo(b.Below));
        return new EquityInfo(
            (string)d["symbol"] ?? symbol,
            (string)d["streamer-symbol"] ?? symbol,
            (string)d["description"] ?? string.Empty,
            d["is-etf"]?.GetValue<bool>() ?? false,
            bands);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string filter, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(filter))
            return hits;

        var json = await this.GetJsonAsync("/symbols/search/" + Uri.EscapeDataString(filter.Trim()), ct).ConfigureAwait(false);
        if (json["data"]?["items"] is not JsonArray items)
            return hits;

        foreach (var item in items)
        {
            // Search also returns futures, crypto and the like. This connection is stocks only.
            var symbol = (string)item?["symbol"];
            if (!string.IsNullOrEmpty(symbol) && (string)item["instrument-type"] == "Equity")
                hits.Add(new SearchHit(symbol, (string)item["description"] ?? string.Empty));
        }

        return hits;
    }

    /// <summary>
    /// Where a stock trades now, from tastytrade's REST market data: the last trade, else the mark, else the previous
    /// close. NaN when none is given. It picks the stock's one tick size (PriceBands.TickAt).
    /// </summary>
    public async Task<double> GetPriceAsync(string symbol, CancellationToken ct)
    {
        var json = await this.GetJsonAsync("/market-data/by-type?equity=" + Uri.EscapeDataString(symbol), ct).ConfigureAwait(false);
        if (json["data"]?["items"] is not JsonArray items)
            return double.NaN;

        foreach (var item in items)
        {
            if (!string.Equals((string)item?["symbol"], symbol, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var field in new[] { "last", "mark", "prev-close" })
            {
                double price = ParseDouble((string)item[field]);
                if (price > 0)
                    return price;
            }
        }

        return double.NaN;
    }

    /// <summary>Raw response for one path, for diagnostics in the test harness.</summary>
    public async Task<string> GetRawAsync(string path, CancellationToken ct) =>
        (await this.GetJsonAsync(path, ct).ConfigureAwait(false)).ToJsonString();

    /// <summary>What the access token says it may do, for diagnostics. Never returns the token.</summary>
    public async Task<string> GetTokenScopeAsync(CancellationToken ct)
    {
        var parts = (await this.GetAccessTokenAsync(ct).ConfigureAwait(false)).Split('.');
        if (parts.Length != 3)
            return "(the token doesn't carry readable claims)";

        var claims = JsonNode.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1])).AsObject();
        return claims["scope"]?.ToJsonString() ?? $"(no scope claim, only: {string.Join(", ", claims.Select(c => c.Key))})";
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

    private async Task<JsonNode> GetJsonAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await this.GetAccessTokenAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await this.http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                this.accessToken = null;
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new TastytradeApiException($"GET {path} returned {(int)response.StatusCode}: {Clip(Log.Scrub(text))}");

            return JsonNode.Parse(text);
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await this.tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.accessToken != null && DateTime.UtcNow < this.accessTokenExpiryUtc)
                return this.accessToken;

            var refreshToken = (string)this.config["refresh_token"];
            if (refreshToken == refusedToken && DateTime.UtcNow < refusedUntilUtc)
                throw new TastytradeApiException($"tastytrade refused these credentials, so not trying again until {refusedUntilUtc.ToLocalTime():HH:mm:ss} to avoid an IP block. Update {this.configPath} to try sooner.");

            var body = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = (string)this.config["refresh_token"],
                ["client_id"] = (string)this.config["client_id"],
                ["client_secret"] = (string)this.config["client_secret"]
            };

            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await this.http.PostAsync(BaseUrl + "/oauth/token", content, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Server trouble says nothing about the credentials, so those keep retrying.
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    refusedToken = refreshToken;
                    refusedUntilUtc = DateTime.UtcNow + RefusedPause;
                }

                throw new TastytradeApiException($"OAuth token request returned {(int)response.StatusCode}: {Clip(Log.Scrub(text))}");
            }

            refusedToken = null;
            var json = JsonNode.Parse(text);
            this.accessToken = (string)json["access_token"];
            Log.AddSecret(this.accessToken);
            int lifetime = json["expires_in"]?.GetValue<int>() ?? 900;
            this.accessTokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(30, lifetime - 60));

            var rotated = (string)json["refresh_token"];
            if (!string.IsNullOrEmpty(rotated) && rotated != (string)this.config["refresh_token"])
            {
                this.config["refresh_token"] = rotated;
                Log.AddSecret(rotated);
                File.WriteAllText(this.configPath, this.config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Log.Info("tastytrade rotated the refresh token; saved the new one");
            }

            return this.accessToken;
        }
        finally
        {
            this.tokenGate.Release();
        }
    }

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "...";
}
