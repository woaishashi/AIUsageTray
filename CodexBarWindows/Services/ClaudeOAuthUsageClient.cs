using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexBarWindows.Services;

internal sealed record ClaudeUsageWindow(string Key, double Utilization, DateTimeOffset? ResetsAt);

internal sealed record ClaudeUsageResult(
    IReadOnlyList<ClaudeUsageWindow> Windows,
    string? Plan,
    string? ExtraUsageSummary);

internal enum ClaudeUsageFailure
{
    NoCredentials,
    TokenExpired,
    Unauthorized,
    RequestFailed
}

internal sealed record ClaudeUsageOutcome(
    ClaudeUsageResult? Result,
    ClaudeUsageFailure? Failure,
    string? FailureDetail);

/// <summary>
/// Claude Code の OAuth トークン (~/.claude/.credentials.json) を使って
/// Anthropic の usage エンドポイントから利用状況を取得する。
/// トークンは Anthropic 公式 API 以外へは送らず、表示・ログにも出さない。
/// </summary>
internal sealed class ClaudeOAuthUsageClient
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBetaHeader = "oauth-2025-04-20";

    private readonly HttpClient _httpClient;

    public ClaudeOAuthUsageClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ClaudeUsageOutcome> FetchAsync(CancellationToken cancellationToken)
    {
        var credentials = ReadCredentials();
        if (credentials is null)
        {
            return new ClaudeUsageOutcome(null, ClaudeUsageFailure.NoCredentials, ".credentials.json not found");
        }

        if (credentials.ExpiresAt is not null && credentials.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return new ClaudeUsageOutcome(null, ClaudeUsageFailure.TokenExpired, "access token expired");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new ClaudeUsageOutcome(null, ClaudeUsageFailure.RequestFailed, SecretSafeText.ForDisplay(ex.Message));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ClaudeUsageOutcome(null, ClaudeUsageFailure.RequestFailed, "request timed out");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ClaudeUsageOutcome(null, ClaudeUsageFailure.Unauthorized, $"HTTP {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ClaudeUsageOutcome(null, ClaudeUsageFailure.RequestFailed, $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                return new ClaudeUsageOutcome(ParseUsage(json, credentials.SubscriptionType), null, null);
            }
            catch (JsonException ex)
            {
                return new ClaudeUsageOutcome(null, ClaudeUsageFailure.RequestFailed, SecretSafeText.ForDisplay(ex.Message));
            }
        }
    }

    private sealed record Credentials(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType);

    private static Credentials? ReadCredentials()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        var envRoots = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();
        foreach (var root in envRoots)
        {
            candidates.Add(Path.Combine(root, ".credentials.json"));
        }

        candidates.Add(Path.Combine(profile, ".claude", ".credentials.json"));
        candidates.Add(Path.Combine(profile, ".config", "claude", ".credentials.json"));

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                DateTimeOffset? expiresAt = null;
                if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number
                    && expires.TryGetInt64(out var expiresMs))
                {
                    expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresMs);
                }

                string? subscription = null;
                if (oauth.TryGetProperty("subscriptionType", out var sub) && sub.ValueKind == JsonValueKind.String)
                {
                    subscription = sub.GetString();
                }

                var accessToken = token.GetString();
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    return new Credentials(accessToken, expiresAt, subscription);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 次の候補を試す
            }
        }

        return null;
    }

    private static ClaudeUsageResult ParseUsage(string json, string? subscriptionFromCredentials)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var windows = new List<ClaudeUsageWindow>();

        foreach (var key in new[] { "five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus" })
        {
            var window = ParseWindow(root, key);
            if (window is not null)
            {
                windows.Add(window);
            }
        }

        string? plan = null;
        if (root.TryGetProperty("subscriptionType", out var planElement) && planElement.ValueKind == JsonValueKind.String)
        {
            plan = planElement.GetString();
        }
        else if (root.TryGetProperty("subscription_type", out var planSnake) && planSnake.ValueKind == JsonValueKind.String)
        {
            plan = planSnake.GetString();
        }
        else if (root.TryGetProperty("rate_limit_tier", out var tier) && tier.ValueKind == JsonValueKind.String)
        {
            plan = tier.GetString();
        }

        plan ??= subscriptionFromCredentials;

        string? extraUsage = null;
        if (root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            extraUsage = SummarizeExtraUsage(extra);
        }

        return new ClaudeUsageResult(windows, plan, extraUsage);
    }

    private static ClaudeUsageWindow? ParseWindow(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double utilization = 0;
        if (window.TryGetProperty("utilization", out var util) && util.ValueKind == JsonValueKind.Number)
        {
            utilization = util.GetDouble();
        }
        else if (window.TryGetProperty("used_percent", out var used) && used.ValueKind == JsonValueKind.Number)
        {
            utilization = used.GetDouble();
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resets))
        {
            if (resets.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(resets.GetString(), out var parsed))
            {
                resetsAt = parsed.ToLocalTime();
            }
            else if (resets.ValueKind == JsonValueKind.Number && resets.TryGetInt64(out var unixSeconds))
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
            }
        }

        return new ClaudeUsageWindow(key, Math.Clamp(utilization, 0, 100), resetsAt);
    }

    private static string? SummarizeExtraUsage(JsonElement extra)
    {
        double? used = null;
        double? limit = null;

        foreach (var property in extra.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var name = property.Name.ToLowerInvariant();
            if (used is null && (name.Contains("used") || name.Contains("spend")))
            {
                used = property.Value.GetDouble();
            }
            else if (limit is null && (name.Contains("limit") || name.Contains("cap")))
            {
                limit = property.Value.GetDouble();
            }
        }

        if (used is null && limit is null)
        {
            return null;
        }

        var usedText = used is null ? "?" : FormatCents(used.Value);
        return limit is null ? usedText : $"{usedText} / {FormatCents(limit.Value)}";
    }

    private static string FormatCents(double value)
    {
        // API はセント単位のことが多いが確証がないため、大きい値だけドル換算する
        return value >= 1000 ? $"${value / 100d:0.00}" : $"${value:0.00}";
    }
}
