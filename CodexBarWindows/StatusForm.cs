using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using CodexBarWindows.Providers;

namespace CodexBarWindows;

internal sealed class StatusForm : Form
{
    private const int WmExitSizeMove = 0x0232;

    private readonly PopoverView _view;
    private bool _allowClose;
    private bool _userMoved;

    public StatusForm(Action refresh, Action openConfigFolder, Action exit)
    {
        Text = "AIUsageTray";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        BackColor = CodexColors.PanelBottom;
        Opacity = 1.0;
        Size = new Size(360, 760);
        MinimumSize = Size;
        MaximumSize = Size;

        _view = new PopoverView(refresh, openConfigFolder, exit)
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_view);

        KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape)
            {
                WindowState = FormWindowState.Minimized;
            }
            else if (args.Control && args.KeyCode == Keys.R)
            {
                refresh();
            }
        };

        FormClosing += (_, args) =>
        {
            if (!_allowClose)
            {
                args.Cancel = true;
                WindowState = FormWindowState.Minimized;
            }
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int csDropShadow = 0x00020000;
            var createParams = base.CreateParams;
            createParams.ClassStyle |= csDropShadow;
            return createParams;
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmExitSizeMove)
        {
            _userMoved = true;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = RoundedRectanglePath(new Rectangle(Point.Empty, Size), 14);
        Region?.Dispose();
        Region = new Region(path);
    }

    public void SetSnapshots(IReadOnlyList<ProviderSnapshot> snapshots, string message)
    {
        _view.SetSnapshots(snapshots, message);
    }

    public void ShowStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        if (!_userMoved)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                Math.Max(area.Left + 12, area.Right - Width - 18),
                Math.Max(area.Top + 12, area.Bottom - Height - 18));
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        TopMost = false;
        TopMost = true;
        BringToFront();
        Activate();
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private static GraphicsPath RoundedRectanglePath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter - 1, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter - 1, bounds.Bottom - diameter - 1, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter - 1, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class PopoverView : Control
{
    private readonly Action _refresh;
    private readonly Action _openConfigFolder;
    private readonly Action _exit;
    private readonly List<HitTarget> _hitTargets = new();
    private IReadOnlyList<ProviderSnapshot> _snapshots = Array.Empty<ProviderSnapshot>();
    private string _message = string.Empty;
    private string _selectedProviderId = string.Empty;

    public PopoverView(Action refresh, Action openConfigFolder, Action exit)
    {
        _refresh = refresh;
        _openConfigFolder = openConfigFolder;
        _exit = exit;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    public void SetSnapshots(IReadOnlyList<ProviderSnapshot> snapshots, string message)
    {
        _snapshots = snapshots;
        _message = message;

        if (_snapshots.Count > 0
            && _snapshots.All(snapshot => !string.Equals(snapshot.ProviderId, _selectedProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedProviderId = _snapshots[0].ProviderId;
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _hitTargets.Clear();

        using (var background = new LinearGradientBrush(ClientRectangle, CodexColors.PanelTop, CodexColors.PanelBottom, 90f))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        using (var border = new Pen(CodexColors.Border, 1f))
        {
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        DrawWindowControls(g);

        var selected = SelectedSnapshot();
        var y = DrawTabs(g, 40);
        y = DrawHeader(g, selected, y + 10);
        y = DrawMeters(g, selected, y);
        y = DrawUsageAndCost(g, selected, y + 6);
        DrawFooter(g, selected);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _hitTargets.Any(target => target.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var target = _hitTargets.FirstOrDefault(hitTarget => hitTarget.Bounds.Contains(e.Location));
        if (target is not null)
        {
            target.Action();
            return;
        }

        // ボタン以外を左ドラッグしたら、タイトルバー掴み扱いにしてウィンドウごと移動できるようにする。
        if (e.Button == MouseButtons.Left)
        {
            var form = FindForm();
            if (form is not null)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(form.Handle, NativeMethods.WmNcLButtonDown, NativeMethods.HtCaption, IntPtr.Zero);
            }
        }
    }

    private void DrawWindowControls(Graphics g)
    {
        using var iconFont = new Font("Segoe UI", 11f, FontStyle.Regular);
        using var iconBrush = new SolidBrush(CodexColors.MutedText);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var closeRect = new Rectangle(Width - 38, 8, 24, 24);
        var minimizeRect = new Rectangle(Width - 68, 8, 24, 24);

        g.DrawString("−", iconFont, iconBrush, minimizeRect, center);
        g.DrawString("×", iconFont, iconBrush, closeRect, center);

        _hitTargets.Add(new HitTarget(minimizeRect, () =>
        {
            var form = FindForm();
            if (form is not null)
            {
                form.WindowState = FormWindowState.Minimized;
            }
        }));
        _hitTargets.Add(new HitTarget(closeRect, () =>
        {
            var form = FindForm();
            if (form is not null)
            {
                form.WindowState = FormWindowState.Minimized;
            }
        }));
    }

    private int DrawTabs(Graphics g, int y)
    {
        if (_snapshots.Count == 0)
        {
            return y + 54;
        }

        var count = _snapshots.Count;
        var tabWidth = (Width - 28) / count;
        var x = 14;

        using var labelFont = new Font("Yu Gothic UI", 8.4f, FontStyle.Bold);
        using var center = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        foreach (var snapshot in _snapshots)
        {
            var rect = new Rectangle(x, y, tabWidth, 46);
            var selected = string.Equals(snapshot.ProviderId, _selectedProviderId, StringComparison.OrdinalIgnoreCase);

            if (selected)
            {
                using var selectedBrush = new SolidBrush(CodexColors.SelectedBlue);
                FillRounded(g, selectedBrush, rect, 9);
            }

            using var textBrush = new SolidBrush(selected ? Color.White : CodexColors.Text);
            g.DrawString(snapshot.DisplayName, labelFont, textBrush, new RectangleF(rect.Left + 4, rect.Top + 7, rect.Width - 8, 22), center);

            var session = snapshot.Windows.FirstOrDefault(window => window.Kind == WindowKind.Session);
            var usedRatio = session?.UsedPercent is double used ? Math.Clamp(used / 100.0, 0, 1) : 0;
            var barRect = new Rectangle(rect.Left + 10, rect.Bottom - 10, rect.Width - 20, 4);
            using var trackBrush = new SolidBrush(selected ? Color.FromArgb(95, Color.White) : CodexColors.Track);
            FillRounded(g, trackBrush, barRect, 2);
            if (usedRatio > 0)
            {
                using var fillBrush = new SolidBrush(selected ? Color.White : StatusColor(session?.UsedPercent));
                FillRounded(g, fillBrush, new Rectangle(barRect.Left, barRect.Top, Math.Max(4, (int)(barRect.Width * usedRatio)), barRect.Height), 2);
            }

            var providerId = snapshot.ProviderId;
            _hitTargets.Add(new HitTarget(rect, () =>
            {
                _selectedProviderId = providerId;
                Invalidate();
            }));

            x += tabWidth;
        }

        return y + 54;
    }

    private int DrawHeader(Graphics g, ProviderSnapshot snapshot, int y)
    {
        using var metaFont = new Font("Yu Gothic UI", 8.2f);
        using var chipFont = new Font("Yu Gothic UI", 8.2f);
        using var textBrush = new SolidBrush(CodexColors.Text);
        using var mutedBrush = new SolidBrush(CodexColors.MutedText);
        using var divider = new Pen(CodexColors.Divider, 1f);

        g.DrawString(UpdatedLabel(snapshot.RefreshedAt), metaFont, mutedBrush, 20, y + 5);

        if (!string.IsNullOrWhiteSpace(snapshot.Plan))
        {
            var chip = new Rectangle(Width - 72, y, 52, 24);
            using var chipBrush = new SolidBrush(Color.FromArgb(70, Color.White));
            using var chipBorder = new Pen(Color.FromArgb(80, CodexColors.MutedText), 1f);
            FillRounded(g, chipBrush, chip, 12);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(snapshot.Plan, chipFont, textBrush, chip, center);
            g.DrawRoundedRectangle(chipBorder, chip, 12);
        }

        DrawRightAligned(g, ShortStatus(snapshot.Status), metaFont, mutedBrush, Width - 20, y + 28);
        y += 48;
        g.DrawLine(divider, 20, y, Width - 20, y);
        return y + 18;
    }

    private int DrawMeters(Graphics g, ProviderSnapshot snapshot, int y)
    {
        using var titleFont = new Font("Yu Gothic UI", 11f, FontStyle.Bold);
        using var bodyFont = new Font("Yu Gothic UI", 8.1f);
        using var textBrush = new SolidBrush(CodexColors.Text);
        using var mutedBrush = new SolidBrush(CodexColors.MutedText);

        foreach (var window in MeterSections(snapshot))
        {
            g.DrawString(window.Name, titleFont, textBrush, 20, y);
            if (window.UsedPercent is double usedPercent)
            {
                DrawRightAligned(g, string.Format(CultureInfo.CurrentCulture, UiText.PercentUsedShortFormat, Math.Clamp(usedPercent, 0, 100)), bodyFont, textBrush, Width - 20, y + 2);
            }
            y += 30;

            var usedRatio = window.UsedPercent is double used ? Math.Clamp(used / 100.0, 0, 1) : 0;
            DrawBar(g, new Rectangle(20, y, Width - 40, 7), usedRatio, window.UsedPercent);
            y += 16;

            var detail = ResetText(window);
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = window.Summary;
            }

            DrawTrimmed(g, detail, bodyFont, mutedBrush, new RectangleF(20, y, Width - 40, 24));
            y += 35;
        }

        return y;
    }

    private int DrawUsageAndCost(Graphics g, ProviderSnapshot snapshot, int y)
    {
        using var titleFont = new Font("Yu Gothic UI", 11f, FontStyle.Bold);
        using var bodyFont = new Font("Yu Gothic UI", 8.1f);
        using var textBrush = new SolidBrush(CodexColors.Text);
        using var mutedBrush = new SolidBrush(CodexColors.MutedText);
        using var divider = new Pen(CodexColors.Divider, 1f);

        g.DrawLine(divider, 20, y, Width - 20, y);
        y += 16;

        var extra = snapshot.Windows.FirstOrDefault(window => window.Kind == WindowKind.Extra);
        g.DrawString(UiText.ExtraUsage, titleFont, textBrush, 20, y);
        DrawRightAligned(g, extra?.Summary ?? UiText.ThisMonthUnset, bodyFont, mutedBrush, Width - 20, y + 2);
        y += 34;

        g.DrawLine(divider, 20, y, Width - 20, y);
        y += 16;

        g.DrawString(UiText.Cost, titleFont, textBrush, 20, y);
        DrawRightAligned(g, snapshot.CostLine ?? UiText.LocalChecksOnly, bodyFont, mutedBrush, Width - 20, y + 2);
        y += 30;

        if (!string.IsNullOrWhiteSpace(_message))
        {
            DrawTrimmed(g, Trim(_message, 54), bodyFont, mutedBrush, new RectangleF(20, y, Width - 40, 22));
            y += 24;
        }

        return y;
    }

    private void DrawFooter(Graphics g, ProviderSnapshot snapshot)
    {
        using var rowFont = new Font("Yu Gothic UI", 9.2f);
        using var buttonFont = new Font("Yu Gothic UI", 9.4f, FontStyle.Bold);
        using var textBrush = new SolidBrush(CodexColors.Text);
        using var whiteBrush = new SolidBrush(Color.White);
        using var divider = new Pen(CodexColors.Divider, 1f);

        var y = Height - 190;
        g.DrawLine(divider, 20, y, Width - 20, y);
        y += 12;

        var refreshRect = new Rectangle(20, y, Width - 40, 34);
        using (var refreshBrush = new SolidBrush(CodexColors.SelectedBlue))
        {
            FillRounded(g, refreshBrush, refreshRect, 8);
        }

        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString(UiText.Refresh, buttonFont, whiteBrush, refreshRect, center);
        }
        _hitTargets.Add(new HitTarget(refreshRect, _refresh));
        y += 44;

        var (dashboardUrl, statusUrl) = ProviderLinks(snapshot.ProviderId);
        y = DrawActionRow(g, rowFont, textBrush, UiText.UsageDashboard, y, () => OpenUrl(dashboardUrl), true);
        y = DrawActionRow(g, rowFont, textBrush, UiText.StatusPage, y, () => OpenUrl(statusUrl), true);
        y += 8;
        g.DrawLine(divider, 20, y, Width - 20, y);
        y += 8;
        y = DrawActionRow(g, rowFont, textBrush, UiText.Settings, y, _openConfigFolder, false);
        _ = DrawActionRow(g, rowFont, textBrush, UiText.Quit, y, _exit, false);
    }

    private int DrawActionRow(Graphics g, Font font, Brush brush, string text, int y, Action action, bool showChevron)
    {
        var bounds = new Rectangle(14, y, Width - 28, 25);
        DrawTrimmed(g, text, font, brush, new RectangleF(bounds.Left + 8, bounds.Top + 3, bounds.Width - 44, bounds.Height));
        if (showChevron)
        {
            using var mutedBrush = new SolidBrush(CodexColors.MutedText);
            DrawRightAligned(g, "›", font, mutedBrush, bounds.Right - 8, bounds.Top + 3);
        }

        _hitTargets.Add(new HitTarget(bounds, action));
        return y + 25;
    }

    private ProviderSnapshot SelectedSnapshot()
    {
        return _snapshots.FirstOrDefault(snapshot =>
                string.Equals(snapshot.ProviderId, _selectedProviderId, StringComparison.OrdinalIgnoreCase))
            ?? _snapshots.FirstOrDefault()
            ?? ProviderSnapshot.Pending("none", UiText.Checking);
    }

    private static IReadOnlyList<UsageWindow> MeterSections(ProviderSnapshot snapshot)
    {
        var meters = snapshot.Windows
            .Where(window => window.Kind is WindowKind.Session or WindowKind.Weekly or WindowKind.ModelWeekly)
            .ToArray();

        var placeholder = snapshot.Status switch
        {
            ProviderStatus.Pending => UiText.Checking,
            ProviderStatus.Error => UiText.FetchFailed,
            _ => UiText.UsageNotFetched
        };

        return new[]
        {
            meters.FirstOrDefault(window => window.Kind == WindowKind.Session)
                ?? new UsageWindow(UiText.Session, placeholder, null, null, WindowKind.Session),
            meters.FirstOrDefault(window => window.Kind == WindowKind.Weekly)
                ?? new UsageWindow(UiText.Weekly, UiText.UsageNotFetched, null, null, WindowKind.Weekly),
            meters.FirstOrDefault(window => window.Kind == WindowKind.ModelWeekly)
                ?? new UsageWindow(UiText.Model, UiText.UsageNotFetched, null, null, WindowKind.ModelWeekly)
        };
    }

    private static string ResetText(UsageWindow window)
    {
        if (window.ResetsAt is null)
        {
            return string.Empty;
        }

        var resetsAt = window.ResetsAt.Value;
        return resetsAt <= DateTimeOffset.Now
            ? string.Format(CultureInfo.CurrentCulture, UiText.ResetDoneFormat, resetsAt.LocalDateTime)
            : string.Format(CultureInfo.CurrentCulture, UiText.ResetAfterFormat, RelativeTime(resetsAt));
    }

    private static string ShortStatus(ProviderStatus status)
    {
        return status switch
        {
            ProviderStatus.Available => UiText.Normal,
            ProviderStatus.Warning => UiText.Warning,
            ProviderStatus.Unavailable => UiText.NotConfigured,
            ProviderStatus.Error => UiText.Error,
            _ => UiText.Checking
        };
    }

    private static Color StatusColor(double? usedPercent)
    {
        if (usedPercent is null)
        {
            return CodexColors.Offline;
        }

        return usedPercent switch
        {
            >= 90 => CodexColors.Error,
            >= 75 => CodexColors.Warning,
            _ => CodexColors.Success
        };
    }

    private static void DrawBar(Graphics g, Rectangle bounds, double usedRatio, double? usedPercent)
    {
        using var track = new SolidBrush(CodexColors.Track);
        FillRounded(g, track, bounds, 4);

        if (usedRatio <= 0)
        {
            return;
        }

        using var fill = new SolidBrush(StatusColor(usedPercent));
        FillRounded(g, fill, new Rectangle(bounds.Left, bounds.Top, Math.Max(5, (int)(bounds.Width * usedRatio)), bounds.Height), 4);
    }

    private static string UpdatedLabel(DateTimeOffset refreshedAt)
    {
        var elapsed = DateTimeOffset.Now - refreshedAt;
        if (elapsed.TotalSeconds < 45)
        {
            return UiText.UpdatedJustNow;
        }

        if (elapsed.TotalMinutes < 60)
        {
            return string.Format(CultureInfo.CurrentCulture, UiText.UpdatedMinutesAgoFormat, (int)elapsed.TotalMinutes);
        }

        if (elapsed.TotalHours < 24)
        {
            return string.Format(CultureInfo.CurrentCulture, UiText.UpdatedHoursAgoFormat, (int)elapsed.TotalHours);
        }

        return $"{UiText.UpdatedAt}: {refreshedAt.LocalDateTime:g}";
    }

    private static string RelativeTime(DateTimeOffset value)
    {
        var span = value - DateTimeOffset.Now;
        if (span.TotalMinutes <= 0)
        {
            return UiText.Now;
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)Math.Ceiling(span.TotalMinutes)}m";
        }

        if (span.TotalHours < 48)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

    private static (string Dashboard, string Status) ProviderLinks(string providerId)
    {
        return providerId.ToLowerInvariant() switch
        {
            "codex" => ("https://chatgpt.com/codex/settings/usage", "https://status.openai.com/"),
            "claude" => ("https://claude.ai/settings/usage", "https://status.anthropic.com/"),
            "openai" => ("https://platform.openai.com/usage", "https://status.openai.com/"),
            _ => ("", "")
        };
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Ignore browser launch failures.
        }
    }

    private static void DrawRightAligned(Graphics g, string text, Font font, Brush brush, int right, int y)
    {
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, right - size.Width, y);
    }

    private static void DrawTrimmed(Graphics g, string text, Font font, Brush brush, RectangleF bounds)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(text, font, brush, bounds, format);
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle bounds, int radius)
    {
        using var path = RoundedRectanglePath(bounds, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectanglePath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string Trim(string value, int maxLength)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "...";
    }

    private sealed record HitTarget(Rectangle Bounds, Action Action);
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = RoundedRectanglePath(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectanglePath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class CodexColors
{
    public static readonly Color PanelTop = Color.FromArgb(245, 246, 248);
    public static readonly Color PanelBottom = Color.FromArgb(230, 232, 236);
    public static readonly Color Text = Color.FromArgb(31, 28, 46);
    public static readonly Color MutedText = Color.FromArgb(88, 93, 108);
    public static readonly Color Divider = Color.FromArgb(92, 138, 146, 162);
    public static readonly Color Track = Color.FromArgb(118, 196, 204, 216);
    public static readonly Color Border = Color.FromArgb(116, 134, 142, 156);
    public static readonly Color SelectedBlue = Color.FromArgb(45, 124, 245);
    public static readonly Color Success = Color.FromArgb(216, 140, 82);
    public static readonly Color Warning = Color.FromArgb(216, 140, 82);
    public static readonly Color Error = Color.FromArgb(214, 77, 96);
    public static readonly Color Offline = Color.FromArgb(128, 134, 148);
    public static readonly Color Pending = Color.FromArgb(103, 112, 132);
}

internal static class UiText
{
    public const string ShowStatus = "ステータスを表示";
    public const string Refresh = "更新";
    public const string Refreshing = "更新中...";
    public const string Refreshed = "更新しました。";
    public const string RefreshCanceled = "更新をキャンセルしました。";
    public const string ProviderStatus = "プロバイダー状態";
    public const string OpenConfigFolder = "設定フォルダーを開く";
    public const string Exit = "終了";
    public const string Starting = "起動中";
    public const string FetchingProviders = "プロバイダー状態を取得しています...";
    public const string NoUsageWindows = "利用状況ウィンドウはまだありません";
    public const string UpdatedAt = "更新日時";
    public const string UpdatedJustNow = "たった今更新";
    public const string UpdatedMinutesAgoFormat = "{0}分前に更新";
    public const string UpdatedHoursAgoFormat = "{0}時間前に更新";
    public const string Normal = "正常";
    public const string Warning = "注意";
    public const string NotConfigured = "未設定";
    public const string Error = "エラー";
    public const string Checking = "確認中";
    public const string Reset = "リセット";
    public const string ResetAfterFormat = "{0} 後にリセット";
    public const string ResetDoneFormat = "リセット済み（{0:M/d H:mm} 時点の記録）";
    public const string ResetCompleted = "がリセットされました";
    public const string PercentRemainingFormat = "{0:0}% 残り";
    public const string PercentUsedFormat = "{0:0}% 使用";
    public const string PercentUsedShortFormat = "{0:0}%";
    public const string Session = "セッション";
    public const string Weekly = "週間";
    public const string Model = "モデル";
    public const string WeeklySonnet = "週間 (Sonnet)";
    public const string WeeklyOpus = "週間 (Opus)";
    public const string ExtraUsage = "追加使用量";
    public const string Cost = "コスト";
    public const string UsageNotFetched = "使用量は未取得";
    public const string LocalChecksOnly = "ローカル確認のみ";
    public const string ThisMonthUnset = "今月: 未設定";
    public const string Last30dTokensFormat = "過去30日: {0} トークン";
    public const string Ready = "準備済み";
    public const string FetchFailed = "取得に失敗";
    public const string NoRateLimitData = "クォータ情報なし（Codex CLI を一度実行すると表示されます）";
    public const string ClaudeNoCredentials = "未接続（Claude コマンドでログインしてください）";
    public const string ClaudeReauthNeeded = "再ログインが必要です";
    public const string UsageDashboard = "使用状況ダッシュボード";
    public const string StatusPage = "ステータスページ";
    public const string Settings = "設定...";
    public const string About = "AIUsageTray について";
    public const string Quit = "終了";
    public const string Now = "今";
}

internal static class NativeMethods
{
    public const int WmNcLButtonDown = 0x00A1;
    public static readonly IntPtr HtCaption = new(2);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
