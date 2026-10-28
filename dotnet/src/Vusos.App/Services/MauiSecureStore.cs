using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using Vusos.Core.Platform;

namespace Vusos.App.Services;

/// <summary>
/// <see cref="ISecureStore"/> nad <see cref="SecureStorage"/> (Keychain / Android Keystore / DPAPI).
/// Gdy SecureStorage rzuca wyjątek (np. emulator bez blokady ekranu, brak uprawnień Keychain w symulatorze iOS),
/// a <c>allowInsecureFallback</c> jest włączone, wartości trafiają do <see cref="Preferences"/> – NIESZYFROWANE,
/// tylko do developmentu. W Release domyślnie wyłączone (App:AllowInsecureSecureStoreFallback w appsettings.json).
/// </summary>
public sealed class MauiSecureStore : ISecureStore
{
    private const string FallbackPrefix = "vusos.insecure.";

    private readonly bool _allowFallback;
    private readonly ILogger? _logger;
    private volatile bool _secureStorageBroken;

    public MauiSecureStore(bool allowInsecureFallback, ILogger<MauiSecureStore>? logger = null)
    {
        _allowFallback = allowInsecureFallback;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_secureStorageBroken)
        {
            try
            {
                var value = await SecureStorage.Default.GetAsync(key).ConfigureAwait(false);
                if (value is not null || !_allowFallback)
                    return value;
            }
            catch (Exception ex) when (CanFallBack(ex))
            {
                MarkBroken(ex);
            }
        }
        return ReadFallback(key);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_secureStorageBroken)
        {
            try
            {
                await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);
                if (_allowFallback)
                    Preferences.Default.Remove(FallbackPrefix + key);
                return;
            }
            catch (Exception ex) when (CanFallBack(ex))
            {
                MarkBroken(ex);
            }
        }
        Preferences.Default.Set(FallbackPrefix + key, value);
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_secureStorageBroken)
        {
            try
            {
                SecureStorage.Default.Remove(key);
            }
            catch (Exception ex) when (CanFallBack(ex))
            {
                MarkBroken(ex);
            }
        }
        if (_allowFallback)
            Preferences.Default.Remove(FallbackPrefix + key);
        return Task.CompletedTask;
    }

    private static string? ReadFallback(string key)
    {
        var prefKey = FallbackPrefix + key;
        return Preferences.Default.ContainsKey(prefKey) ? Preferences.Default.Get(prefKey, string.Empty) : null;
    }

    private bool CanFallBack(Exception ex) => _allowFallback && ex is not OperationCanceledException;

    private void MarkBroken(Exception ex)
    {
        _secureStorageBroken = true;
        _logger?.LogWarning(ex, "SecureStorage niedostępny – używam NIEZABEZPIECZONEGO zapasowego magazynu (Preferences). Tylko do developmentu.");
    }
}
