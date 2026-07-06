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
    private const int MaxFilesToProbe = 8;
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

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = ReadLastRateLimits(file);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
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
                || !payload.TryGetProperty("rate_limits", out var rateLimits))
            {
                return null;
            }

            var observedAt = DateTimeOffset.Now;
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

            return new CodexRateLimits(
                ParseWindow(rateLimits, "primary"),
                ParseWindow(rateLimits, "secondary"),
                planType,
                observedAt);
        }
        catch (JsonException)
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

        double usedPercent = 0;
        if (window.TryGetProperty("used_percent", out var used) && used.ValueKind == JsonValueKind.Number)
        {
            usedPercent = used.GetDouble();
        }

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
