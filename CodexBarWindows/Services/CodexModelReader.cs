using System.Text.Json;

namespace CodexBarWindows.Services;

internal static class CodexModelReader
{
    // Read only model metadata; never surface prompts or session contents.
    public static string? ReadLatest(string codexHome, CancellationToken cancellationToken)
    {
        var root = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(root)) return null;
        try
        {
            string? model = null;
            var latest = DateTimeOffset.MinValue;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                         .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).Take(32))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!line.Contains("\"turn_context\"", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var item = doc.RootElement;
                            if (item.GetProperty("type").GetString() != "turn_context") continue;
                            var payload = item.GetProperty("payload");
                            if (!payload.TryGetProperty("model", out var value) || value.ValueKind != JsonValueKind.String) continue;
                            if (!DateTimeOffset.TryParse(item.GetProperty("timestamp").GetString(), out var time) || time <= latest) continue;
                            var name = value.GetString();
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            latest = time;
                            model = name;
                        }
                        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
