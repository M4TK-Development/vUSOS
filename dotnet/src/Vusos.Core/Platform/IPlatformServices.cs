namespace Vusos.Core.Platform;

/// <summary>Bezpieczny magazyn (Keychain / Keystore / DPAPI / Blazor Server: protected storage).</summary>
public interface ISecureStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

public interface IConnectivity
{
    bool IsOnline { get; }
    bool IsMetered { get; }
    event EventHandler<bool>? OnlineChanged;
}

/// <summary>Katalogi aplikacji: baza cache i pobrane materiały.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string MaterialsDirectory { get; }
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
