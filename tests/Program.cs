using System.Text.Json;
using CodexBarWindows;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        _checks++; Console.WriteLine("PASS " + label);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        var root = Path.Combine(Path.GetTempPath(), "AIUsageTray-test-" + Guid.NewGuid());
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            var file = Path.Combine(sessions, "a.jsonl");
            string Event(string rate, string time = "2026-09-26T10:00:00Z") =>
                "{\"timestamp\":\"" + time + "\",\"payload\":{\"rate_limits\":" + rate + "}}";
            var reader = new CodexRateLimitReader();
            CodexRateLimits? Read(string content) { File.WriteAllText(file, content); return reader.ReadLatest(root, default); }
            Check(Read(Event("null")) is null, "null rate limits do not crash");
            Check(Read(Event("{}")) is null, "empty payload is not success");
            Check(Read(Event("{\"primary\":{\"resets_at\":123}}")) is null, "missing percentage is not zero");
            Check(Read(Event("{\"primary\":{\"used_percent\":\"bad\"}}")) is null, "invalid percentage is ignored");
            Check(Read(Event("{\"primary\":{\"used_percent\":0,\"window_minutes\":300}}"))?.Primary?.UsedPercent == 0, "explicit zero preserved");
            const string weekly = "{\"primary\":{\"used_percent\":32,\"window_minutes\":10080},\"secondary\":null}";
            var result = Read(Event(weekly));
            var windows = CodexUsageProvider.BuildWindows(result);
            Check(windows.Count == 1 && windows[0].Kind == WindowKind.Weekly, "primary weekly is never labeled session");
            var both = CodexUsageProvider.BuildWindows(new(new(12, 300, null), new(42, 10080, null), "pro", DateTimeOffset.Now));
            Check(both[0].Kind == WindowKind.Session && both[1].Kind == WindowKind.Weekly, "legacy two-window layout");
            Check(Read(Event(weekly) + "\n" + Event("null") + "\n{partial")?.Primary?.UsedPercent == 32, "partial tail and null retain last valid record");
            File.WriteAllText(Path.Combine(sessions, "b.jsonl"), Event("{\"primary\":{\"used_percent\":99,\"window_minutes\":10080}}", "2026-09-25T10:00:00Z"));
            File.SetLastWriteTimeUtc(Path.Combine(sessions, "b.jsonl"), DateTime.UtcNow.AddHours(1));
            Check(reader.ReadLatest(root, default)?.Primary?.UsedPercent == 32, "event timestamp beats file modification time");
            File.Delete(Path.Combine(sessions, "b.jsonl"));
            Check(Read(Event("{\"limit_id\":\"other\",\"primary\":{\"used_percent\":80}}")) is null, "other limit bucket is not the core quota");
            File.WriteAllText(file, "{\"type\":\"turn_context\",\"timestamp\":\"2026-09-26T10:00:00Z\",\"payload\":{\"model\":\"gpt-test\"}}\n{partial");
            Check(CodexModelReader.ReadLatest(root, default) == "gpt-test", "model read independently of quota events");
        }
        finally { Directory.Delete(root, true); }

        var now = DateTimeOffset.Now;
        ProviderSnapshot codex = new("codex", "Codex", ProviderStatus.Available, "ローカルログの使用量", "",
            [new("週間", "32% 使用", now.AddDays(2), 32, WindowKind.Weekly)], now, "Pro", "過去30日: 2.3M トークン", "gpt-test");
        ProviderSnapshot claude = new("claude", "Claude", ProviderStatus.Available, "セッション 24% 使用", "",
            [new("セッション", "", now.AddHours(3), 24, WindowKind.Session), new("週間", "", now.AddDays(4), 45, WindowKind.Weekly),
             new("週間 (Sonnet)", "", now.AddDays(4), 12, WindowKind.ModelWeekly), new("週間 (Opus)", "", now.AddDays(4), 67, WindowKind.ModelWeekly)],
            now, "Max", "過去30日: 4.5M トークン");
        if (args.Contains("--live"))
        {
            codex = new CodexUsageProvider().FetchAsync(default).GetAwaiter().GetResult();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            claude = new ClaudeUsageProvider(http).FetchAsync(default).GetAwaiter().GetResult();
            foreach (var s in new[] { codex, claude })
                Console.WriteLine(JsonSerializer.Serialize(new { s.ProviderId, s.Status, s.Model, s.Plan, s.Windows }));
            Check(codex.Model is not null, "live Codex model metadata");
            Check(codex.Windows.Any(w => w.Kind == WindowKind.Weekly), "live Codex weekly quota");
        }
        Directory.CreateDirectory(".tmp");
        using var form = new PopoverView(() => { }, () => { }, () => { }) { Size = new Size(820, 760) };
        form.SetSnapshots([codex, claude], "5分ごとに自動更新 · マウスホイールでスクロール");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(args.Contains("--live") ? ".tmp/overview-live.png" : ".tmp/overview-test.png");
        Check(bitmap.Width == 820, "overview render");
        Console.WriteLine($"{_checks} checks passed.");
    }
}
