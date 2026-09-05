using QuotaDock.Models;

namespace QuotaDock.Providers;

public interface IQuotaProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Glyph { get; }
    string AccentHex { get; }
    Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken);
}

internal static class ProviderResult
{
    public static QuotaSnapshot Missing(IQuotaProvider provider, string message) =>
        Create(provider, ProviderState.MissingCredentials, message);

    public static QuotaSnapshot AuthenticationRequired(IQuotaProvider provider, string message) =>
        Create(provider, ProviderState.AuthenticationRequired, message);

    public static QuotaSnapshot Unavailable(IQuotaProvider provider, string message) =>
        Create(provider, ProviderState.Unavailable, message);

    private static QuotaSnapshot Create(IQuotaProvider provider, ProviderState state, string message) =>
        new(provider.Id, provider.DisplayName, provider.Glyph, provider.AccentHex, [], DateTimeOffset.Now, state, message);
}
