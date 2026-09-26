using System.Diagnostics;
using System.Drawing.Drawing2D;
using CodexBarWindows.Providers;

namespace CodexBarWindows;

internal sealed class PopoverView : Control
{
    private readonly Action _refresh, _settings, _exit;
    private readonly List<(Rectangle Bounds, Action Action)> _targets = new();
    private IReadOnlyList<ProviderSnapshot> _snapshots = Array.Empty<ProviderSnapshot>();
    private string _message = "";
    private int _scroll, _contentHeight;
    private readonly ToolTip _tooltip = new();
    private readonly List<(Rectangle Bounds, string Text)> _tips = new();

    public PopoverView(Action refresh, Action settings, Action exit)
    {
        _refresh = refresh; _settings = settings; _exit = exit;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetSnapshots(IReadOnlyList<ProviderSnapshot> snapshots, string message)
    {
        _snapshots = snapshots; _message = message; Invalidate();
    }

    public int PreferredLogicalHeight => Math.Clamp(157 + _snapshots
        .Where(s => s.ProviderId is "codex" or "claude")
        .Select(CardHeight).DefaultIfEmpty(420).Max(), 580, 760);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tooltip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        // All geometry is in logical pixels, including hit testing on high-DPI displays.
        var scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        var w = (int)(Width / scale); var h = (int)(Height / scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        _targets.Clear(); _tips.Clear();
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(30, 48, 65), Color.FromArgb(20, 23, 42), 45f))
            g.FillRectangle(bg, 0, 0, w, h);
        Glow(g, new Rectangle(-100, -180, 700, 660), Color.FromArgb(50, 48, 206, 181));
        Glow(g, new Rectangle(w - 440, 80, 620, 620), Color.FromArgb(40, 146, 108, 225));
        using (var edge = new Pen(Color.FromArgb(95, 214, 234, 247)))
            g.DrawRoundedRectangle(edge, new Rectangle(1, 1, w - 3, h - 3), 18);

        TextAt(g, "AIUsageTray", 22, 16, 240, 30, 15, true);
        TextAt(g, "USAGE OVERVIEW", 23, 50, 240, 22, 8, false, CodexColors.MutedText);
        Button(g, new Rectangle(w - 226, 24, 104, 34), "↻  更新", _refresh, Color.FromArgb(52, 120, 155));
        Button(g, new Rectangle(w - 110, 24, 36, 34), "−", () => { if (FindForm() is { } f) f.WindowState = FormWindowState.Minimized; });
        Button(g, new Rectangle(w - 64, 24, 36, 34), "×", () => FindForm()?.Hide());

        var content = new Rectangle(20, 91, w - 40, h - 157);
        var state = g.Save();
        g.SetClip(content);
        var colWidth = (content.Width - 16) / 2;
        var primary = new[] { "codex", "claude" }.Select(id => _snapshots.FirstOrDefault(s => s.ProviderId == id)
            ?? new ProviderSnapshot(id, id == "codex" ? "Codex" : "Claude", ProviderStatus.Unavailable,
                "設定で無効になっています", "", Array.Empty<UsageWindow>(), DateTimeOffset.Now)).ToArray();
        var top = content.Top - _scroll;
        var cardHeight = Math.Max(CardHeight(primary[0]), CardHeight(primary[1]));
        DrawCard(g, primary[0], new Rectangle(content.Left, top, colWidth, cardHeight), Color.FromArgb(119, 226, 206));
        DrawCard(g, primary[1], new Rectangle(content.Left + colWidth + 16, top, colWidth, cardHeight), Color.FromArgb(236, 177, 143));
        var bottom = top + cardHeight;
        foreach (var snapshot in _snapshots.Where(s => s.ProviderId is not "codex" and not "claude"))
        {
            bottom += 16;
            var height = CardHeight(snapshot);
            DrawCard(g, snapshot, new Rectangle(content.Left, bottom, content.Width, height), Color.FromArgb(159, 184, 245));
            bottom += height;
        }
        _contentHeight = bottom - top;
        g.Restore(state);
        // Do not allow clipped card links to intercept footer or header clicks.
        for (var i = _targets.Count - 1; i >= 3; i--)
        {
            var target = _targets[i];
            _targets[i] = (Rectangle.Intersect(target.Bounds, content), target.Action);
        }
        if (_contentHeight > content.Height)
        {
            var trackHeight = content.Height;
            var thumbHeight = Math.Max(28, trackHeight * trackHeight / _contentHeight);
            var thumbY = content.Top + (trackHeight - thumbHeight) * _scroll / Math.Max(1, _contentHeight - content.Height);
            Glass(g, new Rectangle(w - 9, thumbY, 4, thumbHeight), 2, Color.FromArgb(140, 186, 208, 228));
        }
        TextAt(g, _message, 23, h - 48, w - 210, 27, 8, false, CodexColors.MutedText);
        Button(g, new Rectangle(w - 177, h - 52, 70, 32), "設定", _settings);
        Button(g, new Rectangle(w - 97, h - 52, 70, 32), "終了", _exit);
    }

    private static int CardHeight(ProviderSnapshot s) => 266 + Meters(s).Count * 77;

    private static List<UsageWindow> Meters(ProviderSnapshot s)
    {
        var meters = s.Windows.Where(w => w.Kind != WindowKind.Extra).ToList();
        if (!meters.Any(w => w.Kind == WindowKind.Session))
            meters.Insert(0, new UsageWindow("セッション · 5時間", s.Status == ProviderStatus.Pending ? "確認中" :
                s.ProviderId == "codex" && s.Windows.Count > 0 ? "取得元に5時間枠の提供なし" : s.Summary, Kind: WindowKind.Session));
        if (!meters.Any(w => w.Kind == WindowKind.Weekly))
            meters.Insert(Math.Min(1, meters.Count), new UsageWindow("週間", "週間枠は未取得", Kind: WindowKind.Weekly));
        return meters;
    }

    private void DrawCard(Graphics g, ProviderSnapshot s, Rectangle card, Color accent)
    {
        Glass(g, card, 18, Color.FromArgb(25, 228, 242, 255));
        var x = card.Left + 20; var y = card.Top + 19; var width = card.Width - 40;
        using (var dot = new SolidBrush(accent)) g.FillEllipse(dot, x, y + 11, 9, 9);
        TextAt(g, s.DisplayName, x + 20, y, width - 102, 34, 17, true);
        TextAt(g, s.Plan ?? "—", card.Right - 110, y + 7, 88, 25, 9, true, accent, true);
        y += 43;
        var status = s.Status switch { ProviderStatus.Available => "取得済み", ProviderStatus.Pending => "確認中", ProviderStatus.Error => "取得失敗", ProviderStatus.Warning => "要確認", _ => "未接続" };
        TextAt(g, status + "  ·  " + s.Summary, x, y, width, 25, 8, false, CodexColors.MutedText);
        y += 33;
        foreach (var meter in Meters(s))
        {
            var expired = meter.ResetsAt <= DateTimeOffset.Now;
            TextAt(g, meter.Name, x, y, width - 105, 24, 10, true);
            TextAt(g, meter.UsedPercent is double used ? $"{used:0.#}% 使用" : "—", x + width - 105, y, 105, 25, 10, true,
                expired ? CodexColors.MutedText : accent, true);
            Glass(g, new Rectangle(x, y + 31, width, 6), 3, Color.FromArgb(28, 225, 237, 249));
            if (meter.UsedPercent is double value && value > 0)
            {
                var color = expired ? CodexColors.Offline : value >= 90 ? CodexColors.Error : accent;
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, x + 1, y + 32, Math.Max(2, (width - 2) * (float)Math.Clamp(value / 100, 0, 1)), 4);
            }
            var detail = meter.ResetsAt is { } reset
                ? (expired ? "リセット済み · 次の記録を待機" : $"リセット {reset.LocalDateTime:M/d HH:mm} · {Remaining(reset)}")
                : meter.Summary;
            TextAt(g, detail, x, y + 44, width, 23, 8, false, CodexColors.MutedText);
            y += 77;
        }
        using (var line = new Pen(Color.FromArgb(30, Color.White))) g.DrawLine(line, x, y, x + width, y);
        y += 12;
        var modelLabel = s.ProviderId == "codex" ? "直近のモデル" : "モデル別の週間枠";
        var model = s.ProviderId == "codex" ? s.Model ?? "モデル記録なし"
            : s.Windows.Any(w => w.Kind == WindowKind.ModelWeekly) ? "上のメーターに表示"
            : s.Status == ProviderStatus.Available ? "取得元からの提供なし" : "接続後に取得";
        TextAt(g, modelLabel, x, y, width, 20, 8, false, CodexColors.MutedText);
        TextAt(g, model, x, y + 22, width, 26, 10, true);
        TextAt(g, s.CostLine ?? "追加の使用量情報なし", x, y + 53, width, 23, 8, false, CodexColors.MutedText);
        var extra = s.Windows.FirstOrDefault(w => w.Kind == WindowKind.Extra);
        if (extra is not null) TextAt(g, extra.Summary, x, y + 76, width, 22, 8, false, CodexColors.MutedText);
        var source = s.ProviderId == "codex" ? "ログ記録" : "取得";
        TextAt(g, $"{source} {s.RefreshedAt.LocalDateTime:M/d HH:mm}", x, card.Bottom - 39, width - 78, 23, 8, false, CodexColors.MutedText);
        var url = s.ProviderId switch { "codex" => "https://chatgpt.com/codex/settings/usage", "claude" => "https://claude.ai/settings/usage", _ => "https://platform.openai.com/usage" };
        Button(g, new Rectangle(card.Right - 98, card.Bottom - 44, 78, 28), "使用状況 ↗", () => OpenUrl(url));
    }

    private static string Remaining(DateTimeOffset reset)
    {
        var span = reset - DateTimeOffset.Now;
        return span.TotalDays >= 1 ? $"あと{(int)span.TotalDays}日 {span.Hours}時間" : span.TotalHours >= 1 ? $"あと{(int)span.TotalHours}時間 {span.Minutes}分" : $"あと{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}分";
    }

    private void TextAt(Graphics g, string text, int x, int y, int w, int h, float size, bool bold = false, Color? color = null, bool right = false)
    {
        using var font = new Font("Yu Gothic UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        using var brush = new SolidBrush(color ?? CodexColors.Text);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
            Alignment = right ? StringAlignment.Far : StringAlignment.Near, LineAlignment = StringAlignment.Center };
        var rect = new Rectangle(x, y, w, Math.Max(h, (int)Math.Ceiling(font.GetHeight(g)) + 2));
        g.DrawString(text, font, brush, rect, format);
        _tips.Add((rect, text));
    }

    private void Button(Graphics g, Rectangle bounds, string label, Action action, Color? color = null)
    {
        Glass(g, bounds, 8, color ?? Color.FromArgb(24, 225, 239, 255));
        TextAt(g, label, bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height, 8, true);
        _targets.Add((bounds, action));
    }

    private static void Glass(Graphics g, Rectangle bounds, int radius, Color color)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); path.CloseFigure();
        using var fill = new LinearGradientBrush(bounds, color, Color.FromArgb(Math.Max(5, color.A / 3), color), 100f);
        using var edge = new Pen(Color.FromArgb(48, 233, 246, 255));
        g.FillPath(fill, path); g.DrawPath(edge, path);
    }

    private static void Glow(Graphics g, Rectangle bounds, Color color)
    {
        using var path = new GraphicsPath(); path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = new[] { Color.Transparent } };
        g.FillPath(brush, path);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Clamp(_scroll - e.Delta / 3, 0, Math.Max(0, _contentHeight - ((int)(Height / (DeviceDpi / 96f)) - 157)));
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = new Point((int)(e.X / (DeviceDpi / 96f)), (int)(e.Y / (DeviceDpi / 96f)));
        Cursor = _targets.Any(t => t.Bounds.Contains(point)) ? Cursors.Hand : Cursors.Default;
        var tip = _tips.FirstOrDefault(t => t.Bounds.Contains(point)).Text ?? "";
        if (_tooltip.GetToolTip(this) != tip) _tooltip.SetToolTip(this, tip);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var point = new Point((int)(e.X / (DeviceDpi / 96f)), (int)(e.Y / (DeviceDpi / 96f)));
        foreach (var target in _targets) if (target.Bounds.Contains(point)) { target.Action(); return; }
        if (point.Y < 85 && FindForm() is { } form)
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(form.Handle, NativeMethods.WmNcLButtonDown, NativeMethods.HtCaption, IntPtr.Zero);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
