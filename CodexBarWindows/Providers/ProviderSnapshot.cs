using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

internal enum ProviderStatus
{
    Pending,
    Available,
    Warning,
    Unavailable,
    Error
}

internal static class WindowKind
{
    public const string Session = "session";
    public const string Weekly = "weekly";
    public const string ModelWeekly = "model-weekly";
    public const string Extra = "extra";
    public const string Info = "info";
}

internal sealed record UsageWindow(
    string Name,
    string Summary,
    DateTimeOffset? ResetsAt = null,
    double? UsedPercent = null,
    string Kind = WindowKind.Info);

internal sealed record ProviderSnapshot(
    string ProviderId,
    string DisplayName,
    ProviderStatus Status,
    string Summary,
    string Details,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset RefreshedAt,
    string? Plan = null,
    string? CostLine = null)
{
    public static ProviderSnapshot Pending(string providerId, string displayName)
    {
        return new ProviderSnapshot(
            providerId,
            displayName,
            ProviderStatus.Pending,
            "Not refreshed yet",
            string.Empty,
            Array.Empty<UsageWindow>(),
            DateTimeOffset.Now);
    }

    public static ProviderSnapshot Error(string providerId, string displayName, Exception exception)
    {
        return new ProviderSnapshot(
            providerId,
            displayName,
            ProviderStatus.Error,
            "Refresh failed",
            SecretSafeText.ForDisplay(exception.Message),
            Array.Empty<UsageWindow>(),
            DateTimeOffset.Now);
    }
}
