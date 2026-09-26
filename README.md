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

## Overview and Codex data

- The glass-style overview shows Codex and Claude side by side, including all returned model-specific quota windows. Scroll to see additional providers or overflow rows.
- Codex quota windows are classified by their duration: 300 minutes is a session window, 10,080 minutes is a weekly window. A primary window can be weekly; an absent session quota is explicitly shown as not provided.
- Codex reads the latest valid quota event by event timestamp from the 32 most recently modified local session logs. Missing percentages are not treated as zero. Null and partially written events are skipped.
- The most recently recorded `turn_context.model` is displayed separately from model-specific quota windows. It represents the latest local turn, not every open task.
- Codex usage is a local log snapshot, not a live account fetch. The record time is shown; records older than 15 minutes or past reset are marked for attention. No credentials are read for Codex.
- Claude continues to use the existing OAuth usage client. If its login has expired, sign in through Claude Code and refresh this app.
- The glass appearance uses layered gradients and translucent cards; it does not blur the desktop behind the window.

## Verification

```powershell
dotnet run --project tests/AIUsageTray.Tests.csproj
# Optional: verify local Codex data and the configured Claude account, and render a preview.
dotnet run --project tests/AIUsageTray.Tests.csproj -- --live
```

Preview images are written under the ignored `.tmp/` folder.

## Current Limits

- This is a tray-only MVP; there is no settings window yet.
- Codex quotas require local session logs; Claude quotas require a valid Claude Code OAuth login.
- OpenAI Admin API errors are intentionally summarized without response bodies to avoid exposing sensitive data.
- No installer, auto-start entry, custom icon, or release packaging is included yet.

## TODO

- Add a settings window for provider enablement and config editing with secret-safe controls.
- Add live Codex CLI app-server usage fetching in addition to local snapshots.
- Add a custom tray icon and richer provider status presentation.
- Add packaging and startup integration for Windows.
