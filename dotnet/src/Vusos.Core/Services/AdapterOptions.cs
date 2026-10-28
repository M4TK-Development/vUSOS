namespace Vusos.Core.Services;

/// <summary>Konfiguracja adapterów (sekcja <c>Adapters</c>).</summary>
public sealed class AdapterOptions
{
    /// <summary><c>live</c> (domyślnie) albo <c>mock</c> — dane demonstracyjne bez sieci.</summary>
    public string Mode { get; set; } = "live";

    /// <summary>Prefiks kluczy cache (użytkownik). Jedna baza = jeden użytkownik, więc domyślnie stały.</summary>
    public string UserScope { get; set; } = "local";

    public bool IsMock => string.Equals(Mode, "mock", StringComparison.OrdinalIgnoreCase);
}
