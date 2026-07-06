using System.Drawing.Drawing2D;
using CodexBarWindows.Providers;
using System.Runtime.InteropServices;

namespace CodexBarWindows;

internal static partial class TrayIconRenderer
{
    public static Icon Create(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using (var back = new SolidBrush(Color.FromArgb(92, 83, 210)))
        {
            FillRounded(g, back, new Rectangle(3, 3, 26, 26), 7);
        }

        var bars = snapshots.Take(3).ToArray();
        if (bars.Length == 0)
        {
            bars = new[] { ProviderSnapshot.Pending("pending", "Pending") };
        }

        var x = 8;
        foreach (var snapshot in bars)
        {
            var color = StatusColor(snapshot.Status);
            using var brush = new SolidBrush(color);
            var height = snapshot.Status switch
            {
                ProviderStatus.Available => 16,
                ProviderStatus.Warning => 12,
                ProviderStatus.Error => 20,
                ProviderStatus.Unavailable => 7,
                _ => 10
            };
            FillRounded(g, brush, new Rectangle(x, 23 - height, 4, height), 2);
            x += 7;
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Color StatusColor(ProviderStatus status)
    {
        return status switch
        {
            ProviderStatus.Available => CodexColors.Success,
            ProviderStatus.Warning => CodexColors.Warning,
            ProviderStatus.Error => CodexColors.Error,
            ProviderStatus.Unavailable => CodexColors.Offline,
            _ => CodexColors.Pending
        };
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle bounds, int radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
