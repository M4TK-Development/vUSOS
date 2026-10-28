using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Vusos.Core.Platform;

namespace Vusos.WebHost.Hosting;

/// <summary>Katalogi jednego użytkownika: <c>{Storage:Root}/users/{hash}</c> oraz <c>materials</c> pod nim.</summary>
public sealed class UserAppPaths : IAppPaths
{
    public UserAppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        MaterialsDirectory = Path.Combine(dataDirectory, "materials");
        Directory.CreateDirectory(MaterialsDirectory);
    }

    public string DataDirectory { get; }
    public string MaterialsDirectory { get; }
}

/// <summary>Serwer jest zawsze „online” (łączność klienta nie ma tu znaczenia) i nie liczy ruchu jako płatnego.</summary>
public sealed class ServerConnectivity : IConnectivity
{
    public bool IsOnline => true;
    public bool IsMetered => false;
    public event EventHandler<bool>? OnlineChanged { add { } remove { } }
}

/// <summary>
/// <see cref="ISecureStore"/> użytkownika: jeden plik JSON (<c>secure.json</c>) szyfrowany ASP.NET Core Data Protection
/// (protector <c>vusos:secure:{userId}</c>). Utrata kluczy Data Protection = poświadczenia nieczytelne (użytkownik łączy systemy ponownie).
/// </summary>
public sealed class DataProtectionSecureStore : ISecureStore
{
    private readonly IDataProtector _protector;
    private readonly string _file;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _data;

    public DataProtectionSecureStore(IDataProtectionProvider provider, string userId, string file, ILogger? logger = null)
    {
        _protector = provider.CreateProtector($"vusos:secure:{userId}");
        _file = file;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return Load().GetValueOrDefault(key); }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            data[key] = value;
            Save(data);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            if (data.Remove(key)) Save(data);
        }
        finally { _gate.Release(); }
    }

    private Dictionary<string, string> Load()
    {
        if (_data is not null) return _data;
        if (!File.Exists(_file)) return _data = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var json = _protector.Unprotect(File.ReadAllText(_file, Encoding.UTF8));
            return _data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            // Klucze Data Protection się zmieniły albo plik jest uszkodzony: odkładamy go na bok i zaczynamy od zera.
            var aside = _file + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            _logger?.LogWarning(e, "Nie można odczytać {File}; przeniesiono do {Aside}", _file, aside);
            try { File.Move(_file, aside, overwrite: true); } catch (IOException) { }
            return _data = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> data)
    {
        var protectedText = _protector.Protect(JsonSerializer.Serialize(data));
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, protectedText, Encoding.UTF8);
        File.Move(tmp, _file, overwrite: true);
    }
}
