using System.Globalization;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

internal sealed class ClaudeUsageProvider : IUsageProvider
{
    private readonly LocalUsageScanner _localUsageScanner = new();
    private readonly ClaudeOAuthUsageClient _usageClient;

    public ClaudeUsageProvider(HttpClient httpClient)
    {
        _usageClient = new ClaudeOAuthUsageClient(httpClient);
    }

    public string Id => "claude";

    public string DisplayName => "Claude";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var outcome = await _usageClient.FetchAsync(cancellationToken);
        var usage30d = _localUsageScanner.Scan(GetClaudeUsageRoots(), 30, cancellationToken);

        var costLine = usage30d.HasUsage
            ? string.Format(CultureInfo.CurrentCulture, UiText.Last30dTokensFormat, FormatShortTokens(usage30d.TotalTokens))
            : UiText.LocalChecksOnly;

        if (outcome.Result is null)
        {
            var (status, summary) = outcome.Failure switch
            {
                ClaudeUsageFailure.NoCredentials => (ProviderStatus.Unavailable, UiText.ClaudeNoCredentials),
                ClaudeUsageFailure.TokenExpired => (ProviderStatus.Warning, UiText.ClaudeReauthNeeded),
                ClaudeUsageFailure.Unauthorized => (ProviderStatus.Warning, UiText.ClaudeReauthNeeded),
                _ => (ProviderStatus.Error, UiText.FetchFailed)
            };

            return new ProviderSnapshot(
                Id,
                DisplayName,
                status,
                summary,
                outcome.FailureDetail ?? string.Empty,
                Array.Empty<UsageWindow>(),
                DateTimeOffset.Now,
                null,
                costLine);
        }

        var result = outcome.Result;
        var windows = new List<UsageWindow>();
        foreach (var window in result.Windows)
        {
            var (name, kind) = window.Key switch
            {
                "five_hour" => (UiText.Session, WindowKind.Session),
                "seven_day" => (UiText.Weekly, WindowKind.Weekly),
                "seven_day_sonnet" => (UiText.WeeklySonnet, WindowKind.ModelWeekly),
                "seven_day_opus" => (UiText.WeeklyOpus, WindowKind.ModelWeekly),
                _ => (window.Key, WindowKind.Info)
            };

            windows.Add(new UsageWindow(
                name,
                string.Format(CultureInfo.CurrentCulture, UiText.PercentUsedFormat, Math.Clamp(window.Utilization, 0, 100)),
                window.ResetsAt,
                window.Utilization,
                kind));
        }

        if (!string.IsNullOrWhiteSpace(result.ExtraUsageSummary))
        {
            windows.Add(new UsageWindow(UiText.ExtraUsage, result.ExtraUsageSummary, null, null, WindowKind.Extra));
        }

        var session = result.Windows.FirstOrDefault(window => window.Key == "five_hour");
        var summaryText = session is null
            ? UiText.Ready
            : $"{UiText.Session} {string.Format(CultureInfo.CurrentCulture, UiText.PercentUsedFormat, Math.Clamp(session.Utilization, 0, 100))}";

        return new ProviderSnapshot(
            Id,
            DisplayName,
            ProviderStatus.Available,
            summaryText,
            "oauth usage api",
            windows,
            DateTimeOffset.Now,
            PrettyPlan(result.Plan),
            costLine);
    }

    private static string? PrettyPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return null;
        }

        return plan.ToLowerInvariant() switch
        {
            "max" => "Max",
            "pro" => "Pro",
            "team" => "Team",
            "enterprise" => "Enterprise",
            "default_claude_ai" => "Free",
            _ => char.ToUpperInvariant(plan[0]) + plan[1..]
        };
    }

    private static IEnumerable<string> GetClaudeUsageRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var envRoots = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();

        foreach (var root in envRoots)
        {
            yield return Path.Combine(ExpandHome(root), "projects");
        }

        yield return Path.Combine(profile, ".config", "claude", "projects");
        yield return Path.Combine(profile, ".claude", "projects");
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return path;
    }

    private static string FormatShortTokens(long value)
    {
        return value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.0}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
            >= 1_000 => $"{value / 1_000d:0.0}K",
            _ => value.ToString("N0")
        };
    }
}
