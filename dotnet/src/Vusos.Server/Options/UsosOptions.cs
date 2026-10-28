using Microsoft.Extensions.Options;

namespace Vusos.Server.Options;

/// <summary>Konfiguracja sekcji "Usos".</summary>
public sealed class UsosOptions
{
    public const string SectionName = "Usos";

    /// <summary>"live" (domyślnie) lub "mock" (bez wymaganych kluczy; tylko dev/testy).</summary>
    public string Mode { get; set; } = "live";

    /// <summary>Korzeń instalacji USOS API, np. https://usosapps.uczelnia.pl/</summary>
    public string ApiBaseUrl { get; set; } = "https://usosapps.example.edu.pl/";

    public string? ConsumerKey { get; set; }
    public string? ConsumerSecret { get; set; }

    /// <summary>Opcjonalny shared secret; gdy ustawiony, /usos/* wymaga nagłówka X-Vusos-App-Key.</summary>
    public string? AppKey { get; set; }

    /// <summary>Limit czasu odpowiedzi USOS (po nim 504 UPSTREAM_TIMEOUT).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    public string[] AllowedCallbackSchemes { get; set; } = ["vusos", "https", "http"];

    public bool IsMock => string.Equals(Mode, "mock", StringComparison.OrdinalIgnoreCase);

    /// <summary>ApiBaseUrl zakończony ukośnikiem.</summary>
    public string NormalizedBaseUrl => ApiBaseUrl.EndsWith('/') ? ApiBaseUrl : ApiBaseUrl + "/";
}

public sealed class ServerLimitsOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>Limit żądań na IP na minutę (okno stałe).</summary>
    public int PermitPerMinute { get; set; } = 120;
}

public sealed class CorsConfigOptions
{
    public const string SectionName = "Cors";
    public string[] Origins { get; set; } = [];
}

/// <summary>Fail fast: brak ConsumerKey/ConsumerSecret poza trybem mock przerywa start.</summary>
public sealed class UsosOptionsValidator : IValidateOptions<UsosOptions>
{
    public ValidateOptionsResult Validate(string? name, UsosOptions options)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            errors.Add("Usos:ApiBaseUrl musi być bezwzględnym adresem http(s).");
        if (!options.IsMock)
        {
            if (string.IsNullOrWhiteSpace(options.ConsumerKey))
                errors.Add("Brak Usos:ConsumerKey (zmienna środowiskowa Usos__ConsumerKey). Ustaw Usos:Mode=mock, aby pominąć.");
            if (string.IsNullOrWhiteSpace(options.ConsumerSecret))
                errors.Add("Brak Usos:ConsumerSecret (zmienna środowiskowa Usos__ConsumerSecret). Ustaw Usos:Mode=mock, aby pominąć.");
        }
        if (options.AllowedCallbackSchemes.Length == 0)
            errors.Add("Usos:AllowedCallbackSchemes nie może być puste.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
