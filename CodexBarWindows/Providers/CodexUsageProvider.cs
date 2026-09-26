using System.Globalization;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

internal sealed class CodexUsageProvider : IUsageProvider
{
    private readonly LocalUsageScanner _localUsageScanner = new();
    private readonly CodexRateLimitReader _rateLimitReader = new();

    public string Id => "codex";

    public string DisplayName => "Codex";

    public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Fetch(cancellationToken), cancellationToken);
    }

    private ProviderSnapshot Fetch(CancellationToken cancellationToken)
    {
        var codexHome = GetCodexHome();
        var authJsonExists = File.Exists(Path.Combine(codexHome, "auth.json"));
        var rateLimits = _rateLimitReader.ReadLatest(codexHome, cancellationToken);
        var usage30d = _localUsageScanner.Scan(GetCodexUsageRoots(codexHome), 30, cancellationToken);

        var windows = BuildWindows(rateLimits);
        var model = CodexModelReader.ReadLatest(codexHome, cancellationToken);

        var costLine = usage30d.HasUsage
            ? string.Format(CultureInfo.CurrentCulture, UiText.Last30dTokensFormat, FormatShortTokens(usage30d.TotalTokens))
            : UiText.LocalChecksOnly;

        ProviderStatus status;
        string summary;
        if (rateLimits is not null)
        {
            var stale = DateTimeOffset.Now - rateLimits.ObservedAt > TimeSpan.FromMinutes(15)
                || windows.Any(w => w.ResetsAt <= DateTimeOffset.Now);
            status = stale ? ProviderStatus.Warning : ProviderStatus.Available;
            summary = stale ? "過去のログ記録 · Codex利用後に更新" : "ローカルログの使用量";
        }
        else if (authJsonExists)
        {
            status = ProviderStatus.Warning;
            summary = UiText.NoRateLimitData;
        }
        else
        {
            status = ProviderStatus.Unavailable;
            summary = UiText.NotConfigured;
        }

        var details = rateLimits is null
            ? (authJsonExists ? "auth.json found; no rate_limits events in session logs" : "auth.json not found")
            : $"rate_limits from session log at {rateLimits.ObservedAt.LocalDateTime:g}";

        return new ProviderSnapshot(
            Id,
            DisplayName,
            status,
            summary,
            details,
            windows,
            rateLimits?.ObservedAt ?? DateTimeOffset.Now,
            PrettyPlan(rateLimits?.PlanType),
            costLine,
            model);
    }

    internal static List<UsageWindow> BuildWindows(CodexRateLimits? limits)
    {
        var result = new List<UsageWindow>();
        foreach (var window in new[] { limits?.Primary, limits?.Secondary }.OfType<CodexRateLimitWindow>())
        {
            var (name, kind) = window.WindowMinutes switch
            {
                300 => ("セッション · 5時間", WindowKind.Session),
                10080 => (UiText.Weekly, WindowKind.Weekly),
                _ => ($"利用枠 · {window.WindowMinutes}分", WindowKind.Info)
            };
            result.Add(new UsageWindow(name, UsedText(window.UsedPercent), window.ResetsAt, window.UsedPercent, kind));
        }
        return result;
    }

    private static string UsedText(double usedPercent)
    {
        return string.Format(CultureInfo.CurrentCulture, UiText.PercentUsedFormat, Math.Clamp(usedPercent, 0, 100));
    }

    private static string? PrettyPlan(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return null;
        }

        return planType.ToLowerInvariant() switch
        {
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            _ => char.ToUpperInvariant(planType[0]) + planType[1..]
        };
    }

    private static IEnumerable<string> GetCodexUsageRoots(string codexHome)
    {
        yield return Path.Combine(codexHome, "sessions");
        yield return Path.Combine(codexHome, "archived_sessions");
    }

    private static string GetCodexHome()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Path.GetFullPath(ExpandHome(codexHome));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
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
