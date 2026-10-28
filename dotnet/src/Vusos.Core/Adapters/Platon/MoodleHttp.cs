using System.Net;
using System.Text.Json;

namespace Vusos.Core.Adapters.Platon;

internal readonly record struct HttpResult(HttpStatusCode Status, string Body, Uri? FinalUri)
{
    public int Code => (int)Status;
}

internal static class MoodleHttp
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Wysyła żądanie; timeout i błędy sieci → <see cref="AdapterUnavailableException"/> (przejściowy).</summary>
    public static async Task<HttpResult> SendAsync(
        HttpClient http, HttpRequestMessage request, TimeSpan timeout, HttpCompletionOption completion, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, completion, cts.Token).ConfigureAwait(false);
            // 3xx nie ma sensownego body; nie czytamy go.
            var code = (int)response.StatusCode;
            var body = code is >= 300 and < 400
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return new HttpResult(response.StatusCode, body, response.RequestMessage?.RequestUri);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdapterUnavailableException($"Platon nie odpowiedział w ciągu {timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new AdapterUnavailableException($"Platon jest nieosiągalny: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new AdapterUnavailableException($"Platon jest nieosiągalny: {ex.Message}", ex);
        }
    }

    /// <summary>401/403 → auth; pozostałe błędy HTTP → niedostępność (5xx/408/429 przejściowa).</summary>
    public static void ThrowForStatus(int code)
    {
        if (code is 401 or 403) throw new AdapterAuthException($"Platon zwrócił HTTP {code}");
        if (code is < 200 or >= 300)
            throw new AdapterUnavailableException($"Platon zwrócił HTTP {code}") { IsTransient = code >= 500 || code is 408 or 429 };
    }

    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public static bool IsTruthy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => element.GetString()!.Length > 0,
        JsonValueKind.Number => element.GetDouble() != 0,
        JsonValueKind.Object or JsonValueKind.Array => true,
        _ => false
    };

    public static JsonElement NullElement()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }
}
