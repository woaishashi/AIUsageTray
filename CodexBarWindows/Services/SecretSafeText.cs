using System.Text.RegularExpressions;

namespace CodexBarWindows.Services;

internal static partial class SecretSafeText
{
    public static string ForDisplay(string? value, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var redacted = BearerTokenRegex().Replace(value, "Bearer [redacted]");
        redacted = SecretTokenRegex().Replace(redacted, "[redacted]");
        redacted = ApiKeyJsonRegex().Replace(redacted, "$1[redacted]$3");
        redacted = WhitespaceRegex().Replace(redacted, " ").Trim();

        return redacted.Length <= maxLength ? redacted : redacted[..maxLength];
    }

    [GeneratedRegex("Bearer\\s+[^\\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("sk-[A-Za-z0-9_\\-]{8,}")]
    private static partial Regex SecretTokenRegex();

    [GeneratedRegex("(\"apiKey\"\\s*:\\s*\")([^\"]+)(\")", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyJsonRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
