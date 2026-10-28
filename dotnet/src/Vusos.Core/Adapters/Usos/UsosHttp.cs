using System.Net;
using System.Text.Json;

namespace Vusos.Core.Adapters.Usos;

/// <summary>Non-2xx answer of the USOS API other than 401 (which becomes <see cref="AdapterAuthException"/>).</summary>
public sealed class UsosHttpException : AdapterUnavailableException
{
    private static readonly System.Text.RegularExpressions.Regex MethodNotFoundRegex =
        new(@"method|metod|not found|nie znaleziono|nie istnieje", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public UsosHttpException(string message, int status, string body, string path) : base(message)
    {
        Status = status;
        Body = body;
        Path = path;
        // 5xx, request timeout and rate limiting are worth retrying later; 4xx means the request itself is wrong.
        IsTransient = status >= 500 || status is 408 or 429;
    }

    public int Status { get; }
    public string Body { get; }
    public string Path { get; }

    /// <summary>USOS answers 400/404 "Method ... not found" for modules that are not installed/enabled.</summary>
    public bool IsMethodNotFound => Status is 400 or 404 && MethodNotFoundRegex.IsMatch(Body);
}

internal static class UsosHttp
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Sends a request, reads the whole body under a timeout, maps transport failures to <see cref="AdapterUnavailableException"/>.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> SendForTextAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send, string what, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var res = await send(cts.Token).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return (res.StatusCode, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdapterUnavailableException($"{what}: przekroczono limit czasu ({timeout.TotalSeconds:0} s)") { IsTransient = true };
        }
        catch (HttpRequestException ex)
        {
            throw new AdapterUnavailableException($"{what}: {ex.Message}", ex) { IsTransient = true };
        }
        catch (IOException ex)
        {
            throw new AdapterUnavailableException($"{what}: {ex.Message}", ex) { IsTransient = true };
        }
    }

    /// <summary>"message" of a USOS error JSON, else the first 300 characters of the body.</summary>
    public static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? "";
        }
        catch (JsonException) { /* not JSON */ }
        var t = body.Trim();
        return t.Length > 300 ? t[..300] : t;
    }

    public static string Truncate(string s, int max)
    {
        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }

    public static string NormalizeBaseUrl(string url) => url.EndsWith('/') ? url : url + "/";

    /// <summary>Path relative to "services/" (tolerates leading slashes and an explicit "services/" prefix).</summary>
    public static string RelativeServicePath(string path)
    {
        var p = path.TrimStart('/');
        return p.StartsWith("services/", StringComparison.Ordinal) ? p["services/".Length..] : p;
    }
}
