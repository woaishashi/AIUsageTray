using System.Diagnostics;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _showStatusItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _providersItem;
    private readonly ToolStripMenuItem _openConfigFolderItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly StatusForm _statusForm;
    private readonly ProviderCoordinator _providerCoordinator;
    private readonly ConfigLocator _configLocator;
    private System.Windows.Forms.Timer? _autoRefreshTimer;
    private System.Windows.Forms.Timer? _resetNotificationTimer;
    private readonly Dictionary<string, DateTimeOffset> _notifiedResetKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? _refreshCancellation;
    private IReadOnlyList<ProviderSnapshot> _snapshots = Array.Empty<ProviderSnapshot>();
    private Icon? _trayIcon;
    private bool _isRefreshing;

    public TrayApplicationContext()
    {
        _configLocator = new ConfigLocator();
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        var providers = BuildProviders(_configLocator, httpClient);
        _providerCoordinator = new ProviderCoordinator(providers);

        _showStatusItem = new ToolStripMenuItem(UiText.ShowStatus, null, (_, _) => ShowStatusWindow());
        _refreshItem = new ToolStripMenuItem(UiText.Refresh, null, (_, _) => _ = RefreshProvidersAsync());
        _providersItem = new ToolStripMenuItem(UiText.ProviderStatus);
        _openConfigFolderItem = new ToolStripMenuItem(UiText.OpenConfigFolder, null, (_, _) => OpenConfigFolder());
        _exitItem = new ToolStripMenuItem(UiText.Exit, null, (_, _) => ExitApplication());

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_showStatusItem);
        _menu.Items.Add(_refreshItem);
        _menu.Items.Add(_providersItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_openConfigFolderItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "AIUsageTray - " + UiText.Starting,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowStatusWindow();
            }
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusWindow();

        _statusForm = new StatusForm(
            () => _ = RefreshProvidersAsync(),
            OpenConfigFolder,
            ExitApplication);

        _snapshots = providers
            .Select(provider => ProviderSnapshot.Pending(provider.Id, provider.DisplayName))
            .ToArray();

        SetTrayIcon(_snapshots);
        UpdateProvidersMenu(_snapshots);
        _statusForm.SetSnapshots(_snapshots, UiText.FetchingProviders);

        var startupTimer = new System.Windows.Forms.Timer
        {
            Interval = 300
        };
        startupTimer.Tick += (_, _) =>
        {
            startupTimer.Stop();
            startupTimer.Dispose();
            ShowStatusWindow();
        };
        startupTimer.Start();

        _autoRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 5 * 60 * 1000
        };
        _autoRefreshTimer.Tick += (_, _) => _ = RefreshProvidersAsync();
        _autoRefreshTimer.Start();

        _resetNotificationTimer = new System.Windows.Forms.Timer
        {
            Interval = 30 * 1000
        };
        _resetNotificationTimer.Tick += (_, _) => CheckResetNotifications();
        _resetNotificationTimer.Start();

        _ = RefreshProvidersAsync();
    }

    private static IReadOnlyList<IUsageProvider> BuildProviders(ConfigLocator configLocator, HttpClient httpClient)
    {
        var resolved = configLocator.Resolve();

        bool IsEnabled(string id, bool defaultValue)
        {
            return resolved.Config?.GetProvider(id)?.Enabled ?? defaultValue;
        }

        var providers = new List<IUsageProvider>();
        if (IsEnabled("codex", true))
        {
            providers.Add(new CodexUsageProvider());
        }

        if (IsEnabled("claude", true))
        {
            providers.Add(new ClaudeUsageProvider(httpClient));
        }

        if (IsEnabled("openai", true))
        {
            providers.Add(new OpenAIUsageProvider(configLocator, httpClient));
        }

        if (providers.Count == 0)
        {
            providers.Add(new CodexUsageProvider());
        }

        return providers;
    }

    private async Task RefreshProvidersAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();

        _refreshItem.Enabled = false;
        _refreshItem.Text = UiText.Refreshing;
        SetTrayText("AIUsageTray - " + UiText.Refreshing);
        _statusForm.SetSnapshots(_snapshots, UiText.Refreshing);

        try
        {
            _snapshots = await _providerCoordinator.RefreshAllAsync(_refreshCancellation.Token);
            UpdateProvidersMenu(_snapshots);
            SetTrayIcon(_snapshots);
            UpdateTrayText(_snapshots);
            _statusForm.SetSnapshots(_snapshots, UiText.Refreshed);
            CheckResetNotifications();
        }
        catch (OperationCanceledException)
        {
            SetTrayText("AIUsageTray - " + UiText.RefreshCanceled);
            _statusForm.SetSnapshots(_snapshots, UiText.RefreshCanceled);
        }
        finally
        {
            _refreshItem.Text = UiText.Refresh;
            _refreshItem.Enabled = true;
            _isRefreshing = false;
        }
    }

    private void UpdateProvidersMenu(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        _providersItem.DropDownItems.Clear();

        foreach (var snapshot in snapshots)
        {
            var providerItem = new ToolStripMenuItem(FormatProviderLine(snapshot))
            {
                ToolTipText = snapshot.Details
            };

            if (snapshot.Windows.Count == 0)
            {
                providerItem.DropDownItems.Add(new ToolStripMenuItem(UiText.NoUsageWindows)
                {
                    Enabled = false
                });
            }
            else
            {
                foreach (var window in snapshot.Windows)
                {
                    providerItem.DropDownItems.Add(new ToolStripMenuItem(FormatUsageWindow(window))
                    {
                        Enabled = false
                    });
                }
            }

            providerItem.DropDownItems.Add(new ToolStripSeparator());
            providerItem.DropDownItems.Add(new ToolStripMenuItem($"{UiText.UpdatedAt}: {snapshot.RefreshedAt.LocalDateTime:g}")
            {
                Enabled = false
            });

            _providersItem.DropDownItems.Add(providerItem);
        }
    }

    private void UpdateTrayText(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        var ok = snapshots.Count(snapshot => snapshot.Status == ProviderStatus.Available);
        var warnings = snapshots.Count(snapshot => snapshot.Status == ProviderStatus.Warning);
        var errors = snapshots.Count(snapshot => snapshot.Status == ProviderStatus.Error);
        var unavailable = snapshots.Count(snapshot => snapshot.Status == ProviderStatus.Unavailable);

        SetTrayText($"AIUsageTray - {UiText.Normal} {ok}, {UiText.Warning} {warnings}, {UiText.NotConfigured} {unavailable}, {UiText.Error} {errors}");
    }

    private void OpenConfigFolder()
    {
        try
        {
            var resolved = _configLocator.EnsureScaffold();

            // 関連付けに依存すると「何も起きない」ように見えることがあるため、
            // エクスプローラーで設定フォルダーを開いて config.json を選択状態にする
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{resolved.ConfigPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                SecretSafeText.ForDisplay(ex.Message),
                "AIUsageTray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ExitApplication()
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer?.Dispose();
        _resetNotificationTimer?.Stop();
        _resetNotificationTimer?.Dispose();
        _refreshCancellation?.Cancel();
        _statusForm.AllowClose();
        _statusForm.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon?.Dispose();
        _menu.Dispose();
        ExitThread();
    }

    private void ShowStatusWindow()
    {
        _statusForm.ShowStatus();
    }

    private void CheckResetNotifications()
    {
        var now = DateTimeOffset.Now;

        foreach (var snapshot in _snapshots)
        {
            foreach (var window in snapshot.Windows)
            {
                if (window.ResetsAt is null)
                {
                    continue;
                }

                var resetAt = window.ResetsAt.Value;
                if (resetAt > now || resetAt < now.AddMinutes(-2))
                {
                    continue;
                }

                var key = $"{snapshot.ProviderId}|{window.Kind}|{window.Name}|{resetAt.UtcDateTime:O}";
                if (_notifiedResetKeys.ContainsKey(key))
                {
                    continue;
                }

                _notifiedResetKeys[key] = resetAt;
                _notifyIcon.ShowBalloonTip(
                    10_000,
                    "AIUsageTray",
                    $"{snapshot.DisplayName} {window.Name} {UiText.ResetCompleted}",
                    ToolTipIcon.Info);

                _ = RefreshProvidersAsync();
            }
        }

        foreach (var key in _notifiedResetKeys
                     .Where(item => item.Value < now.AddDays(-1))
                     .Select(item => item.Key)
                     .ToArray())
        {
            _notifiedResetKeys.Remove(key);
        }
    }

    private void SetTrayIcon(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        var oldIcon = _trayIcon;
        _trayIcon = TrayIconRenderer.Create(snapshots);
        _notifyIcon.Icon = _trayIcon;
        oldIcon?.Dispose();
    }

    private void SetTrayText(string text)
    {
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private static string FormatProviderLine(ProviderSnapshot snapshot)
    {
        return $"{StatusLabel(snapshot.Status)} {snapshot.DisplayName}: {snapshot.Summary}";
    }

    private static string FormatUsageWindow(UsageWindow window)
    {
        return window.ResetsAt is null
            ? $"{window.Name}: {window.Summary}"
            : $"{window.Name}: {window.Summary}; {UiText.Reset} {window.ResetsAt.Value.LocalDateTime:g}";
    }

    private static string StatusLabel(ProviderStatus status)
    {
        return status switch
        {
            ProviderStatus.Available => "[" + UiText.Normal + "]",
            ProviderStatus.Warning => "[" + UiText.Warning + "]",
            ProviderStatus.Unavailable => "[" + UiText.NotConfigured + "]",
            ProviderStatus.Error => "[" + UiText.Error + "]",
            _ => "[" + UiText.Checking + "]"
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoRefreshTimer?.Dispose();
            _resetNotificationTimer?.Dispose();
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _statusForm.AllowClose();
            _statusForm.Dispose();
            _notifyIcon.Dispose();
            _trayIcon?.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }
}
