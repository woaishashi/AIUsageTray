using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWindows.Services;

internal sealed class ConfigLocator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public ResolvedConfig Resolve()
    {
        var candidate = GetCandidatePath();
        var directory = Path.GetDirectoryName(candidate) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!File.Exists(candidate))
        {
            return new ResolvedConfig(candidate, directory, null, null);
        }

        try
        {
            var json = File.ReadAllText(candidate);
            var config = JsonSerializer.Deserialize<CodexBarConfigFile>(json, JsonOptions) ?? new CodexBarConfigFile();
            return new ResolvedConfig(candidate, directory, config, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ResolvedConfig(
                candidate,
                directory,
                null,
                SecretSafeText.ForDisplay(ex.Message));
        }
    }

    public ResolvedConfig EnsureScaffold()
    {
        var resolved = Resolve();
        Directory.CreateDirectory(resolved.ConfigDirectory);

        if (!File.Exists(resolved.ConfigPath))
        {
            File.WriteAllText(resolved.ConfigPath, DefaultConfigJson);
        }

        var readmePath = Path.Combine(resolved.ConfigDirectory, "README.txt");
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(readmePath, ConfigReadmeText);
        }

        return Resolve();
    }

    private static string GetCandidatePath()
    {
        var configured = Environment.GetEnvironmentVariable("CODEXBAR_CONFIG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(ExpandHome(configured));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathFullyQualified(xdgConfigHome))
        {
            return Path.Combine(xdgConfigHome, "codexbar", "config.json");
        }

        var xdgPath = Path.Combine(profile, ".config", "codexbar", "config.json");
        var legacyPath = Path.Combine(profile, ".codexbar", "config.json");

        return File.Exists(xdgPath) || !File.Exists(legacyPath)
            ? xdgPath
            : legacyPath;
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

    private const string DefaultConfigJson = """
{
  "version": 1,
  "providers": [
    {
      "id": "codex",
      "enabled": true,
      "source": "auto"
    },
    {
      "id": "claude",
      "enabled": true,
      "source": "auto"
    },
    {
      "id": "openai",
      "enabled": false,
      "source": "api",
      "apiKey": null,
      "workspaceID": null
    }
  ]
}
""";

    private const string ConfigReadmeText = """
AIUsageTray config

This folder was created by AIUsageTray.

Usage notes:
- Codex and Claude local token usage is read from local jsonl session logs.
- OpenAI API usage requires OPENAI_ADMIN_KEY or providers[].apiKey for id "openai".
- Do not commit config.json if you add API keys, cookies, or tokens.
- Secrets are never displayed by the app.

Config lookup order:
1. CODEXBAR_CONFIG
2. XDG_CONFIG_HOME/codexbar/config.json
3. %USERPROFILE%/.config/codexbar/config.json
4. %USERPROFILE%/.codexbar/config.json
""";
}

internal sealed record ResolvedConfig(
    string ConfigPath,
    string ConfigDirectory,
    CodexBarConfigFile? Config,
    string? LoadError);

internal sealed class CodexBarConfigFile
{
    public int? Version { get; set; }

    public List<ProviderConfig> Providers { get; set; } = new();

    public ProviderConfig? GetProvider(string id)
    {
        return Providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class ProviderConfig
{
    public string? Id { get; set; }

    public bool? Enabled { get; set; }

    public string? Source { get; set; }

    public string? ApiKey { get; set; }

    [JsonPropertyName("workspaceID")]
    public string? WorkspaceID { get; set; }
}
