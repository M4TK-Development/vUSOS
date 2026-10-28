using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vusos.Core.Services.Json;

/// <summary>
/// Wspólne ustawienia System.Text.Json dla cache, magazynów offline i poświadczeń: rekordy domenowe,
/// enumy jako tekst, daty ISO 8601, polimorficzne <c>Credentials</c> (dyskryminator <c>mode</c>).
/// </summary>
public static class VusosJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            AllowOutOfOrderMetadataProperties = true,
            NumberHandling = JsonNumberHandling.Strict,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary>Serializacja po typie statycznym <typeparamref name="T"/> (ważne dla typów polimorficznych: dyskryminator).</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize<T>(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
