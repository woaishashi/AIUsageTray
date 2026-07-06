namespace CodexBarWindows.Providers;

internal interface IUsageProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}
