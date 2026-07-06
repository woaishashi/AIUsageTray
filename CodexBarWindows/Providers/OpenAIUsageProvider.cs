using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

internal sealed class OpenAIUsageProvider : IUsageProvider
{
    private readonly ConfigLocator _configLocator;
    private readonly HttpClient _httpClient;

    public OpenAIUsageProvider(ConfigLocator configLocator, HttpClient httpClient)
    {
        _configLocator = configLocator;
        _httpClient = httpClient;
    }

    public string Id => "openai";

    public string DisplayName => "OpenAI";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var resolvedConfig = _configLocator.Resolve();
        var openAiProvider = resolvedConfig.Config?.GetProvider("openai");
        var keySource = TryGetAdminKey(openAiProvider, out var adminKey);
        var projectId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("OPENAI_PROJECT_ID"),
            openAiProvider?.WorkspaceID);

        if (string.IsNullOrWhiteSpace(adminKey))
        {
            var details = resolvedConfig.LoadError is null
                ? "No OPENAI_ADMIN_KEY or openai.apiKey found in compatible config."
                : $"No OPENAI_ADMIN_KEY found. Config was not usable: {resolvedConfig.LoadError}";

            return new ProviderSnapshot(
                Id,
                DisplayName,
                ProviderStatus.Unavailable,
                "Admin API key not configured",
                SecretSafeText.ForDisplay(details),
                Array.Empty<UsageWindow>(),
                DateTimeOffset.Now);
        }

        var startedAt = DateTimeOffset.UtcNow.AddDays(-30);
        CostSummary? costSummary = null;
        UsageSummary? usageSummary = null;
        var errors = new List<string>();

        try
        {
            costSummary = await FetchCostsAsync(adminKey, projectId, startedAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"costs: {SecretSafeText.ForDisplay(ex.Message)}");
        }

        try
        {
            usageSummary = await FetchUsageAsync(adminKey, projectId, startedAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"usage: {SecretSafeText.ForDisplay(ex.Message)}");
        }

        if (costSummary is null && usageSummary is null)
        {
            return new ProviderSnapshot(
                Id,
                DisplayName,
                ProviderStatus.Error,
                "Admin API requests failed",
                string.Join("; ", errors),
                Array.Empty<UsageWindow>(),
                DateTimeOffset.Now);
        }

        var windows = new List<UsageWindow>();
        if (costSummary is not null)
        {
            windows.Add(new UsageWindow("Cost today", FormatMoney(costSummary.TodayCost, costSummary.Currency)));
            windows.Add(new UsageWindow("Cost 30d", FormatMoney(costSummary.TotalCost, costSummary.Currency)));
        }

        if (usageSummary is not null)
        {
            windows.Add(new UsageWindow("Requests 30d", usageSummary.Requests.ToString("N0", CultureInfo.CurrentCulture)));
            windows.Add(new UsageWindow("Tokens 30d", usageSummary.TotalTokens.ToString("N0", CultureInfo.CurrentCulture)));
        }

        var summaryParts = new List<string>();
        if (costSummary is not null)
        {
            summaryParts.Add($"{FormatMoney(costSummary.TotalCost, costSummary.Currency)} / 30d");
        }

        if (usageSummary is not null)
        {
            summaryParts.Add($"{usageSummary.Requests:N0} requests");
        }

        var sourceLabel = keySource == AdminKeySource.Environment ? "env key" : "config key";
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            sourceLabel += ", project scoped";
        }

        var status = errors.Count == 0 ? ProviderStatus.Available : ProviderStatus.Warning;
        var detailsText = errors.Count == 0
            ? $"Fetched OpenAI Admin API usage and costs using {sourceLabel}."
            : $"Partial OpenAI Admin API result using {sourceLabel}. {string.Join("; ", errors)}";

        return new ProviderSnapshot(
            Id,
            DisplayName,
            status,
            string.Join("; ", summaryParts),
            detailsText,
            windows,
            DateTimeOffset.Now);
    }

    private async Task<CostSummary> FetchCostsAsync(
        string adminKey,
        string? projectId,
        DateTimeOffset startTime,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            BuildAdminUri("/v1/organization/costs", startTime, projectId, "line_item"),
            adminKey,
            cancellationToken);

        return ParseCostSummary(document.RootElement);
    }

    private async Task<UsageSummary> FetchUsageAsync(
        string adminKey,
        string? projectId,
        DateTimeOffset startTime,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            BuildAdminUri("/v1/organization/usage/completions", startTime, projectId, "model"),
            adminKey,
            cancellationToken);

        return ParseUsageSummary(document.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(
        Uri uri,
        string adminKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static Uri BuildAdminUri(
        string path,
        DateTimeOffset startTime,
        string? projectId,
        string groupBy)
    {
        var builder = new UriBuilder("https", "api.openai.com")
        {
            Path = path
        };

        var query = new List<string>
        {
            $"start_time={startTime.ToUnixTimeSeconds()}",
            "bucket_width=1d",
            "limit=31",
            $"group_by={Uri.EscapeDataString(groupBy)}"
        };

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            query.Add($"project_ids={Uri.EscapeDataString(projectId)}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static CostSummary ParseCostSummary(JsonElement root)
    {
        var total = 0m;
        var today = 0m;
        var currency = "USD";
        var todayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeSeconds();

        foreach (var bucket in GetDataBuckets(root))
        {
            var isToday = TryGetInt64(bucket, "start_time", out var startTime) && startTime >= todayStart;
            if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (amount.TryGetProperty("currency", out var currencyElement)
                    && currencyElement.ValueKind == JsonValueKind.String)
                {
                    currency = currencyElement.GetString() ?? currency;
                }

                if (TryGetDecimal(amount, "value", out var value))
                {
                    total += value;
                    if (isToday)
                    {
                        today += value;
                    }
                }
            }
        }

        return new CostSummary(total, today, currency);
    }

    private static UsageSummary ParseUsageSummary(JsonElement root)
    {
        long inputTokens = 0;
        long outputTokens = 0;
        long requests = 0;

        foreach (var bucket in GetDataBuckets(root))
        {
            if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                inputTokens += GetInt64OrZero(result, "input_tokens");
                outputTokens += GetInt64OrZero(result, "output_tokens");
                requests += GetInt64OrZero(result, "num_model_requests");
            }
        }

        return new UsageSummary(inputTokens, outputTokens, requests);
    }

    private static IEnumerable<JsonElement> GetDataBuckets(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var bucket in data.EnumerateArray())
        {
            yield return bucket;
        }
    }

    private static long GetInt64OrZero(JsonElement element, string propertyName)
    {
        return TryGetInt64(element, propertyName, out var value) ? value : 0;
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static AdminKeySource TryGetAdminKey(ProviderConfig? openAiProvider, out string? adminKey)
    {
        adminKey = FirstNonEmpty(Environment.GetEnvironmentVariable("OPENAI_ADMIN_KEY"));
        if (!string.IsNullOrWhiteSpace(adminKey))
        {
            return AdminKeySource.Environment;
        }

        adminKey = FirstNonEmpty(openAiProvider?.ApiKey);
        return string.IsNullOrWhiteSpace(adminKey)
            ? AdminKeySource.None
            : AdminKeySource.Config;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static string FormatMoney(decimal value, string currency)
    {
        return string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase)
            ? value.ToString("$0.00", CultureInfo.InvariantCulture)
            : $"{currency.ToUpperInvariant()} {value:0.00}";
    }

    private enum AdminKeySource
    {
        None,
        Environment,
        Config
    }

    private sealed record CostSummary(decimal TotalCost, decimal TodayCost, string Currency);

    private sealed record UsageSummary(long InputTokens, long OutputTokens, long Requests)
    {
        public long TotalTokens => InputTokens + OutputTokens;
    }
}
