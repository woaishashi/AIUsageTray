using System.Text;
using System.Text.Json;

namespace CodexBarWindows.Services;

internal sealed record CodexRateLimitWindow(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);

internal sealed record CodexRateLimits(
    CodexRateLimitWindow? Primary,
    CodexRateLimitWindow? Secondary,
    string? PlanType,
    DateTimeOffset ObservedAt);

/// <summary>
/// Codex CLI のセッションログ (.jsonl) 末尾にある token_count イベントの
/// rate_limits スナップショットを読み取る。ネットワークアクセスは行わない。
/// </summary>
internal sealed class CodexRateLimitReader
{
    private const int MaxFilesToProbe = 32;
    private const int TailBytesToRead = 512 * 1024;

    public CodexRateLimits? ReadLatest(string codexHome, CancellationToken cancellationToken)
    {
        var sessionsRoot = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return null;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(MaxFilesToProbe)
                .Select(info => info.FullName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        CodexRateLimits? latest = null;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = ReadLastRateLimits(file);
            if (parsed is not null && (latest is null || parsed.ObservedAt > latest.ObservedAt))
            {
                latest = parsed;
            }
        }

        return latest;
    }

    private static CodexRateLimits? ReadLastRateLimits(string file)
    {
        string tail;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - TailBytesToRead);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            tail = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var lines = tail.Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index];
            if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = TryParseLine(line);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static CodexRateLimits? TryParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("rate_limits", out var rateLimits)
                || rateLimits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (rateLimits.TryGetProperty("limit_id", out var limitId)
                && limitId.ValueKind == JsonValueKind.String && limitId.GetString() is string id && id != "codex")
                return null;

            var observedAt = DateTimeOffset.MinValue;
            if (root.TryGetProperty("timestamp", out var timestamp)
                && timestamp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(timestamp.GetString(), out var parsedTimestamp))
            {
                observedAt = parsedTimestamp;
            }

            string? planType = null;
            if (rateLimits.TryGetProperty("plan_type", out var plan) && plan.ValueKind == JsonValueKind.String)
            {
                planType = plan.GetString();
            }

            var primary = ParseWindow(rateLimits, "primary");
            var secondary = ParseWindow(rateLimits, "secondary");
            if (primary is null && secondary is null) return null;
            return new CodexRateLimits(
                primary,
                secondary,
                planType,
                observedAt);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static CodexRateLimitWindow? ParseWindow(JsonElement rateLimits, string name)
    {
        if (!rateLimits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!window.TryGetProperty("used_percent", out var used) || !used.TryGetDouble(out var usedPercent)
            || !double.IsFinite(usedPercent)) return null;

        var windowMinutes = 0;
        if (window.TryGetProperty("window_minutes", out var minutes) && minutes.ValueKind == JsonValueKind.Number)
        {
            windowMinutes = minutes.GetInt32();
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resets))
        {
            if (resets.ValueKind == JsonValueKind.Number && resets.TryGetInt64(out var unixSeconds))
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
            }
            else if (resets.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(resets.GetString(), out var parsedReset))
            {
                resetsAt = parsedReset;
            }
        }

        return new CodexRateLimitWindow(Math.Clamp(usedPercent, 0, 100), windowMinutes, resetsAt);
    }
}
