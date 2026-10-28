using System.Net.Http.Headers;
using System.Text;

namespace Vusos.Core.Adapters.Usos;

/// <summary>
/// Sends one USOS API request on behalf of the user. <paramref name="relativePath"/> is relative to the
/// installation's <c>services/</c> directory (e.g. <c>users/user</c>); a leading "services/" is tolerated.
/// GET: <paramref name="parameters"/> go into the query, otherwise into a form-urlencoded body.
/// The caller owns the returned response (dispose it).
/// </summary>
public interface IUsosRequestSigner
{
    Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyDictionary<string, string> parameters,
        UsosApiCredentials creds,
        CancellationToken ct);
}

/// <summary>Signs locally with the consumer key/secret (own use) and calls {creds.BaseUrl}services/{path}.</summary>
public sealed class LocalUsosSigner(HttpClient http, string consumerKey, string consumerSecret) : IUsosRequestSigner
{
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string relativePath, IReadOnlyDictionary<string, string> parameters,
        UsosApiCredentials creds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(creds.BaseUrl))
            throw new AdapterUnavailableException("Brak adresu USOS API w poświadczeniach") { IsTransient = false };

        var url = $"{UsosHttp.NormalizeBaseUrl(creds.BaseUrl)}services/{UsosHttp.RelativeServicePath(relativePath)}";
        var uri = new Uri(url);
        var pairs = parameters.ToList();
        var header = OAuth1.CreateAuthorizationHeader(
            method.Method, uri, pairs, consumerKey, consumerSecret, creds.AccessToken, creds.AccessTokenSecret);

        var isGet = method == HttpMethod.Get;
        var query = OAuth1.FormEncode(pairs);
        var request = new HttpRequestMessage(method, isGet && query.Length > 0 ? $"{url}?{query}" : url);
        request.Headers.TryAddWithoutValidation("Authorization", header);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!isGet) request.Content = new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded");
        return http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }
}

/// <summary>Forwards to Vusos.Server (<c>{server}/usos/proxy/{path}</c>), which adds the OAuth signature.</summary>
public sealed class RemoteUsosSigner(HttpClient http, Uri signingServerUrl, string? appKey = null) : IUsosRequestSigner
{
    public const string TokenHeader = "X-Usos-Token";
    public const string TokenSecretHeader = "X-Usos-Token-Secret";
    public const string AppKeyHeader = "X-Vusos-App-Key";

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string relativePath, IReadOnlyDictionary<string, string> parameters,
        UsosApiCredentials creds, CancellationToken ct)
    {
        var server = signingServerUrl.ToString().TrimEnd('/');
        var url = $"{server}/usos/proxy/{UsosHttp.RelativeServicePath(relativePath)}";
        var isGet = method == HttpMethod.Get;
        var query = OAuth1.FormEncode(parameters);
        var request = new HttpRequestMessage(method, isGet && query.Length > 0 ? $"{url}?{query}" : url);
        request.Headers.TryAddWithoutValidation(TokenHeader, creds.AccessToken);
        request.Headers.TryAddWithoutValidation(TokenSecretHeader, creds.AccessTokenSecret);
        if (!string.IsNullOrEmpty(appKey)) request.Headers.TryAddWithoutValidation(AppKeyHeader, appKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!isGet) request.Content = new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded");
        return http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }
}
