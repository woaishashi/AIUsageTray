# AIUsageTray

Unofficial Windows notification-area MVP inspired by [steipete/CodexBar](https://github.com/steipete/CodexBar).

The upstream app is a macOS menu bar app. AIUsageTray is a separate .NET WinForms tray app with no third-party dependencies.

## Run

```powershell
dotnet run --project .\CodexBarWindows\CodexBarWindows.csproj
```

After launch, AIUsageTray appears in the Windows notification area. Right-click the tray icon for:

- `Refresh`
- `Providers`
- `Open config folder`
- `Exit`

## Providers

- Codex: checks `codex --version` and whether `auth.json` exists under `%CODEX_HOME%` or `%USERPROFILE%\.codex`. The file contents are never read or displayed.
- Codex local usage: scans recent `.jsonl` session logs under `%CODEX_HOME%\sessions`, `%CODEX_HOME%\archived_sessions`, and `%USERPROFILE%\.pi\agent\sessions` to show 7-day and 30-day token totals.
- Claude: checks `claude --version` and common Claude config locations. Missing CLI/config is reported without crashing the app.
- Claude local usage: scans recent `.jsonl` project logs under `%CLAUDE_CONFIG_DIR%\projects`, `%USERPROFILE%\.config\claude\projects`, `%USERPROFILE%\.claude\projects`, and `%USERPROFILE%\.pi\agent\sessions`.
- OpenAI: uses `OPENAI_ADMIN_KEY` first, then compatible config `providers[].apiKey` for provider `openai`. When configured, it calls the OpenAI Admin API costs and completions usage endpoints and displays only aggregate totals.

## Compatible Config Lookup

Config resolution follows the MVP-compatible order:

1. `CODEXBAR_CONFIG`
2. `%USERPROFILE%\.config\codexbar\config.json`
3. `%USERPROFILE%\.codexbar\config.json`

The app reads provider settings from config but does not print, log, or write secrets. Opening Settings creates a safe default `config.json` and `README.txt` if the folder is empty, then opens `config.json`.

## Current Limits

- This is a tray-only MVP; there is no settings window yet.
- Codex and Claude quota windows are not fetched yet; local token usage from `.jsonl` logs is shown instead.
- OpenAI Admin API errors are intentionally summarized without response bodies to avoid exposing sensitive data.
- No installer, auto-start entry, custom icon, or release packaging is included yet.

## TODO

- Add a settings window for provider enablement and config editing with secret-safe controls.
- Add Codex OAuth/API or CLI app-server usage window fetching.
- Add Claude OAuth/Web/CLI usage fetching.
- Add a custom tray icon and richer provider status presentation.
- Add packaging and startup integration for Windows.
