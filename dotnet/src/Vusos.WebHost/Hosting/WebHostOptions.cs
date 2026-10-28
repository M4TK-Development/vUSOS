namespace Vusos.WebHost.Hosting;

/// <summary>Sekcja <c>Storage</c>: gdzie leżą dane użytkowników.</summary>
public sealed class StorageOptions
{
    /// <summary>Katalog główny danych: <c>users/{hash}</c> (baza, materiały, secure.json) oraz <c>keys</c> (Data Protection).</summary>
    public string Root { get; set; } = "./data";

    public string RootFullPath => Path.GetFullPath(Root);
}

/// <summary>Sekcja <c>Server</c>.</summary>
public sealed class ServerOptions
{
    /// <summary>Publiczny adres aplikacji (bez końcowego /); do callbacków OAuth USOS i OIDC. Pusty = adres z żądania.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Po tylu czasie bez dostępu (i bez otwartych obwodów) dane użytkownika są zwalniane z pamięci.</summary>
    public TimeSpan UserIdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Jak często sprawdzać bezczynnych użytkowników.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Przekierowanie http→https poza Development (wyłącz za reverse proxy, które terminuje TLS).</summary>
    public bool RedirectToHttps { get; set; } = true;

    /// <summary>Respektuj X-Forwarded-For/Proto/Host (reverse proxy).</summary>
    public bool UseForwardedHeaders { get; set; }
}

/// <summary>Sekcja <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    /// <summary>
    /// <c>anonymous</c> (domyślny: bez rejestracji w Entra, cookie sesji + tożsamość z połączeń z USOS/Platonem),
    /// <c>dev</c> (logowanie adresem e-mail) albo <c>entra</c> (OpenID Connect, Microsoft Entra ID).
    /// </summary>
    public string Mode { get; set; } = "anonymous";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Domyślnie <c>University:EntraAuthority</c> (https://login.microsoftonline.com/organizations).</summary>
    public string? Authority { get; set; }

    /// <summary>Opcjonalna lista dozwolonych tenantów (<c>tid</c>); pusta = dowolny tenant z dozwoloną domeną e-mail.</summary>
    public string[] AllowedTenantIds { get; set; } = [];

    /// <summary>Zezwala na tryb dev poza środowiskiem Development/Testing (domyślnie wyłączone: każdy mógłby podać cudzy e-mail).</summary>
    public bool AllowDevInProduction { get; set; }

    public bool IsEntra => string.Equals(Mode, "entra", StringComparison.OrdinalIgnoreCase);
    public bool IsAnonymous => string.Equals(Mode, "anonymous", StringComparison.OrdinalIgnoreCase);
    public bool IsDev => string.Equals(Mode, "dev", StringComparison.OrdinalIgnoreCase);
}
