namespace CodexBarWindows.Providers;

internal sealed class ProviderCoordinator
{
    private readonly IReadOnlyList<IUsageProvider> _providers;

    public ProviderCoordinator(IReadOnlyList<IUsageProvider> providers)
    {
        _providers = providers;
    }

    public async Task<IReadOnlyList<ProviderSnapshot>> RefreshAllAsync(CancellationToken cancellationToken)
    {
        var tasks = _providers.Select(provider => FetchSafelyAsync(provider, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private static async Task<ProviderSnapshot> FetchSafelyAsync(
        IUsageProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.FetchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Error(provider.Id, provider.DisplayName, ex);
        }
    }
}
