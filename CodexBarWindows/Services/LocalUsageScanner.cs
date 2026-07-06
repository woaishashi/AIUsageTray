using System.Globalization;
using System.Text.Json;

namespace CodexBarWindows.Services;

internal sealed class LocalUsageScanner
{
    public LocalUsageSummary Scan(IEnumerable<string> roots, int days, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var summary = new LocalUsageSummary(days);
        var keyedUsages = new Dictionary<string, TokenUsage>(StringComparer.Ordinal);

        foreach (var root in roots.Select(ExpandHome).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateJsonlFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetLastWriteTime(file) < since)
                {
                    continue;
                }

                summary.FilesScanned++;
                ScanFile(file, summary, keyedUsages, cancellationToken);
            }
        }

        foreach (var usage in keyedUsages.Values)
        {
            summary.Add(usage);
            summary.Requests++;
        }

        return summary;
    }

    private static void ScanFile(
        string file,
        LocalUsageSummary summary,
        Dictionary<string, TokenUsage> keyedUsages,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line) || line.Length > 2_000_000)
                {
                    continue;
                }

                if (!TryParseUsage(line, out var usage, out var key) || usage.TotalTokens <= 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(key))
                {
                    keyedUsages[key] = usage;
                }
                else
                {
                    summary.Add(usage);
                    summary.Requests++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            summary.SkippedFiles++;
        }
    }

    private static bool TryParseUsage(string line, out TokenUsage usage, out string? key)
    {
        usage = default;
        key = null;

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        key = FindStableUsageKey(root);

        if (TryGetUsageFromKnownLocations(root, out usage))
        {
            return true;
        }

        if (TryFindUsageObject(root, out var usageElement))
        {
            usage = ParseUsageObject(usageElement);
            return usage.TotalTokens > 0;
        }

        return false;
    }

    private static bool TryGetUsageFromKnownLocations(JsonElement root, out TokenUsage usage)
    {
        usage = default;

        if (TryGetObject(root, "message", out var message)
            && TryGetObject(message, "usage", out var messageUsage))
        {
            usage = ParseUsageObject(messageUsage);
            return usage.TotalTokens > 0;
        }

        if (TryGetObject(root, "usage", out var rootUsage))
        {
            usage = ParseUsageObject(rootUsage);
            return usage.TotalTokens > 0;
        }

        if (TryGetObject(root, "event_msg", out var eventMessage)
            && TryGetObject(eventMessage, "token_count", out var tokenCount))
        {
            usage = ParseUsageObject(tokenCount);
            return usage.TotalTokens > 0;
        }

        if (TryGetObject(root, "token_count", out var directTokenCount))
        {
            usage = ParseUsageObject(directTokenCount);
            return usage.TotalTokens > 0;
        }

        return false;
    }

    private static bool TryFindUsageObject(JsonElement element, out JsonElement usageElement)
    {
        usageElement = default;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeUsageObject(element))
            {
                usageElement = element;
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindUsageObject(property.Value, out usageElement))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindUsageObject(item, out usageElement))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikeUsageObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasTokenField = false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Name.Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                hasTokenField = true;
            }
        }

        return hasTokenField;
    }

    private static TokenUsage ParseUsageObject(JsonElement element)
    {
        var input = Sum(element,
            "input_tokens",
            "prompt_tokens",
            "cache_creation_input_tokens",
            "cache_read_input_tokens",
            "cached_input_tokens");

        var output = Sum(element,
            "output_tokens",
            "completion_tokens");

        var total = input + output;
        if (total <= 0)
        {
            total = Sum(element, "total_tokens", "total_token_count", "tokens");
        }

        return new TokenUsage(input, output, Math.Max(total, input + output));
    }

    private static long Sum(JsonElement element, params string[] names)
    {
        long total = 0;
        foreach (var name in names)
        {
            if (TryGetNumber(element, name, out var value))
            {
                total += value;
            }
        }

        return total;
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetNumber(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
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

    private static string? FindStableUsageKey(JsonElement root)
    {
        var messageId = TryGetNestedString(root, "message", "id");
        var requestId = TryGetString(root, "requestId") ?? TryGetString(root, "request_id");

        if (!string.IsNullOrWhiteSpace(messageId) || !string.IsNullOrWhiteSpace(requestId))
        {
            return $"{messageId}:{requestId}";
        }

        return TryGetString(root, "id");
    }

    private static string? TryGetNestedString(JsonElement element, string objectName, string propertyName)
    {
        return TryGetObject(element, objectName, out var obj) ? TryGetString(obj, propertyName) : null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static IEnumerable<string> EnumerateJsonlFiles(string root)
    {
        if (File.Exists(root) && root.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            yield return root;
            yield break;
        }

        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(directory, "*.jsonl");
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in directories)
            {
                pending.Push(child);
            }
        }
    }

    private static DateTimeOffset GetLastWriteTime(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
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
}

internal sealed class LocalUsageSummary
{
    public LocalUsageSummary(int days)
    {
        Days = days;
    }

    public int Days { get; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long TotalTokens { get; private set; }

    public long Requests { get; set; }

    public int FilesScanned { get; set; }

    public int SkippedFiles { get; set; }

    public bool HasUsage => TotalTokens > 0 || Requests > 0;

    public void Add(TokenUsage usage)
    {
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        TotalTokens += usage.TotalTokens;
    }
}

internal readonly record struct TokenUsage(
    long InputTokens,
    long OutputTokens,
    long TotalTokens);
