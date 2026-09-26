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
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(520, 300);

        _view = new PopoverView(refresh, openConfigFolder, exit)
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_view);
        _view.LayoutChanged += ApplyViewSize;

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
        ApplyViewSize();
    }

    private void ApplyViewSize()
    {
        var scale = DeviceDpi / 96f;
        Size = new Size((int)(_view.PreferredLogicalWidth * scale), (int)(_view.PreferredLogicalHeight * scale));
        if (Visible)
        {
            var area = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
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
    public static readonly Color PanelTop = Color.FromArgb(24, 35, 53);
    public static readonly Color PanelBottom = Color.FromArgb(16, 23, 38);
    public static readonly Color Text = Color.FromArgb(239, 245, 252);
    public static readonly Color MutedText = Color.FromArgb(161, 180, 201);
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
    public const string WeeklyModelFormat = "週間 ({0})";
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
