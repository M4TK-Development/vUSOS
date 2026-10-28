using System.Net;
using System.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Vusos.Server.Options;
using Vusos.Server.Security;

namespace Vusos.Server.Usos;

public sealed record RequestTokenRequest(string? CallbackUrl, string[]? Scopes);
public sealed record AccessTokenRequest(string? Token, string? Secret, string? Verifier);

public static class UsosEndpoints
{
    public const string HttpClientName = "usos";
    public const string TokenHeader = "X-Usos-Token";
    public const string TokenSecretHeader = "X-Usos-Token-Secret";

    /// <summary>Prefiksy ścieżek (po implicit "services/") dopuszczone w proxy.</summary>
    public static readonly string[] AllowedPrefixes =
        ["users/", "tt/", "grades/", "payments/", "courses/", "terms/", "mailclient/", "news/", "fac/", "apisrv/", "oauth/revoke_token"];

    public static IEndpointRouteBuilder MapUsosEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (IOptionsMonitor<UsosOptions> o) =>
            Results.Ok(new { status = "ok", mode = o.CurrentValue.IsMock ? "mock" : "live" }));

        var g = app.MapGroup("/usos");
        g.MapPost("/oauth/request_token", RequestToken);
        g.MapPost("/oauth/access_token", AccessToken);
        g.MapGet("/oauth/callback", Callback);
        g.MapMethods("/proxy/{**path}", ["GET", "POST"], Proxy);
        return app;
    }

    private static IResult Error(int status, string code, string? message = null) =>
        Results.Json(new { code, message }, statusCode: status);

    private static OAuth1Signer Signer(UsosOptions o) =>
        new(o.ConsumerKey ?? "mock-consumer-key", o.ConsumerSecret ?? "mock-consumer-secret");

    private sealed record Upstream(HttpStatusCode Status, string Body, string? ContentType, byte[] Bytes);

    private static async Task<Upstream> SendAsync(HttpClient http, HttpRequestMessage req, UsosOptions o, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
            var ct2 = res.Content.Headers.ContentType?.ToString();
            return new Upstream(res.StatusCode, System.Text.Encoding.UTF8.GetString(bytes), ct2, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static HttpRequestMessage OAuthPost(OAuth1Signer signer, string url, List<KeyValuePair<string, string>> form, string? token, string? tokenSecret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(OAuth1Signer.EncodeForm(form), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", signer.CreateAuthorizationHeader("POST", url, form, token, tokenSecret));
        req.Headers.TryAddWithoutValidation("Accept", "application/x-www-form-urlencoded, text/plain, */*");
        return req;
    }

    private static IResult? UpstreamFailure(Exception ex, ILogger log, string op) => ex switch
    {
        TimeoutException => Error(504, "UPSTREAM_TIMEOUT"),
        HttpRequestException hre =>
            LogAndError(log, op, hre),
        _ => null,
    };

    private static IResult LogAndError(ILogger log, string op, HttpRequestException ex)
    {
        log.LogWarning("USOS upstream unavailable during {Op}: {Reason}", op, ex.HttpRequestError);
        return Error(502, "UPSTREAM_UNAVAILABLE");
    }

    private static async Task<IResult> RequestToken(
        RequestTokenRequest body, IHttpClientFactory factory, IOptionsMonitor<UsosOptions> opts, ILoggerFactory lf, CancellationToken ct)
    {
        var o = opts.CurrentValue;
        var log = lf.CreateLogger("Vusos.Server.Usos.OAuth");
        if (string.IsNullOrWhiteSpace(body.CallbackUrl) ||
            !Uri.TryCreate(body.CallbackUrl, UriKind.Absolute, out var cb) ||
            !o.AllowedCallbackSchemes.Contains(cb.Scheme, StringComparer.OrdinalIgnoreCase))
            return Error(400, "INVALID_CALLBACK", "callbackUrl musi być bezwzględnym adresem z dozwolonym schematem.");

        var scopes = (body.Scopes ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (scopes.Any(s => s.Contains('|') || s.Any(char.IsWhiteSpace)))
            return Error(400, "INVALID_SCOPES");

        var form = new List<KeyValuePair<string, string>> { new("oauth_callback", body.CallbackUrl) };
        if (scopes.Length > 0) form.Add(new("scopes", string.Join('|', scopes)));

        var url = o.NormalizedBaseUrl + "services/oauth/request_token";
        Upstream res;
        try { res = await SendAsync(factory.CreateClient(HttpClientName), OAuthPost(Signer(o), url, form, null, null), o, ct); }
        catch (Exception ex) when (UpstreamFailure(ex, log, "request_token") is { } r) { return r; }

        if ((int)res.Status is < 200 or >= 300)
        {
            log.LogWarning("USOS request_token failed with HTTP {Status}", (int)res.Status);
            return Error(502, "UPSTREAM_ERROR", $"USOS request_token: HTTP {(int)res.Status}");
        }
        var parsed = QueryHelpers.ParseQuery(res.Body.Trim());
        var token = parsed.GetValueOrDefault("oauth_token").ToString();
        var secret = parsed.GetValueOrDefault("oauth_token_secret").ToString();
        if (token.Length == 0 || secret.Length == 0)
            return Error(502, "UPSTREAM_BAD_RESPONSE");

        return Results.Ok(new
        {
            token,
            secret,
            authorizeUrl = $"{o.NormalizedBaseUrl}services/oauth/authorize?oauth_token={OAuth1Signer.PercentEncode(token)}",
        });
    }

    private static async Task<IResult> AccessToken(
        AccessTokenRequest body, IHttpClientFactory factory, IOptionsMonitor<UsosOptions> opts, ILoggerFactory lf, CancellationToken ct)
    {
        var o = opts.CurrentValue;
        var log = lf.CreateLogger("Vusos.Server.Usos.OAuth");
        if (string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.Secret) || string.IsNullOrWhiteSpace(body.Verifier))
            return Error(400, "INVALID_REQUEST", "Wymagane: token, secret, verifier.");

        var form = new List<KeyValuePair<string, string>> { new("oauth_verifier", body.Verifier) };
        var url = o.NormalizedBaseUrl + "services/oauth/access_token";
        Upstream res;
        try { res = await SendAsync(factory.CreateClient(HttpClientName), OAuthPost(Signer(o), url, form, body.Token, body.Secret), o, ct); }
        catch (Exception ex) when (UpstreamFailure(ex, log, "access_token") is { } r) { return r; }

        if (res.Status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            return Error(401, "OAUTH_REJECTED");
        if ((int)res.Status is < 200 or >= 300)
        {
            log.LogWarning("USOS access_token failed with HTTP {Status}", (int)res.Status);
            return Error(502, "UPSTREAM_ERROR", $"USOS access_token: HTTP {(int)res.Status}");
        }
        var parsed = QueryHelpers.ParseQuery(res.Body.Trim());
        var token = parsed.GetValueOrDefault("oauth_token").ToString();
        var secret = parsed.GetValueOrDefault("oauth_token_secret").ToString();
        if (token.Length == 0 || secret.Length == 0)
            return Error(502, "UPSTREAM_BAD_RESPONSE");
        return Results.Ok(new { token, secret });
    }

    private static IResult Callback(HttpContext ctx, IOptionsMonitor<UsosOptions> opts)
    {
        var q = ctx.Request.Query;
        var scheme = q.TryGetValue("scheme", out var s) && s.Count > 0 && s[0]!.Length > 0 ? s[0]! : "vusos";
        if (!opts.CurrentValue.AllowedCallbackSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
            return Error(400, "INVALID_SCHEME");
        var token = q["oauth_token"].ToString();
        var verifier = q["oauth_verifier"].ToString();
        if (token.Length == 0) return Error(400, "INVALID_REQUEST", "Brak oauth_token.");

        // Host jest stały ("usos"), więc nie ma otwartego przekierowania na obcy adres.
        var target = $"{scheme.ToLowerInvariant()}://usos?oauth_token={OAuth1Signer.PercentEncode(token)}";
        if (verifier.Length > 0) target += $"&oauth_verifier={OAuth1Signer.PercentEncode(verifier)}";
        return Results.Redirect(target);
    }

    public static bool IsAllowedPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains("..") || path.Contains('\\') || path.Contains('%') || path.StartsWith('/'))
            return false;
        foreach (var seg in path.Split('/'))
            if (seg is "." or "..") return false;
        return AllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal));
    }

    private static async Task<IResult> Proxy(
        string path, HttpContext ctx, IHttpClientFactory factory, IOptionsMonitor<UsosOptions> opts, ILoggerFactory lf, CancellationToken ct)
    {
        var o = opts.CurrentValue;
        var log = lf.CreateLogger("Vusos.Server.Usos.Proxy");

        if (!IsAllowedPath(path)) return Error(403, "PATH_NOT_ALLOWED");

        var token = ctx.Request.Headers[TokenHeader].ToString();
        var tokenSecret = ctx.Request.Headers[TokenSecretHeader].ToString();
        if (token.Length == 0 || tokenSecret.Length == 0)
            return Error(400, "MISSING_TOKEN", $"Wymagane nagłówki {TokenHeader} i {TokenSecretHeader}.");

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var (k, vs) in QueryHelpers.ParseQuery(ctx.Request.QueryString.Value))
            foreach (var v in vs) pairs.Add(new(k, v ?? ""));
        var queryCount = pairs.Count;

        var isPost = HttpMethods.IsPost(ctx.Request.Method);
        var hasBody = ctx.Request.ContentLength > 0 || (ctx.Request.ContentLength is null && ctx.Request.Headers.TransferEncoding.Count > 0);
        if (isPost && hasBody && !ctx.Request.HasFormContentType)
            return Error(415, "UNSUPPORTED_MEDIA_TYPE", "POST musi mieć application/x-www-form-urlencoded.");
        if (isPost && ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            foreach (var (k, vs) in form)
                foreach (var v in vs) pairs.Add(new(k, v ?? ""));
        }
        if (pairs.Any(p => p.Key.StartsWith("oauth_", StringComparison.Ordinal)))
            return Error(400, "INVALID_PARAMS", "Parametry oauth_* są zastrzeżone.");

        var baseUrl = $"{o.NormalizedBaseUrl}services/{path}";
        var url = baseUrl;
        var query = OAuth1Signer.EncodeForm(pairs.Take(queryCount));
        if (query.Length > 0) url += "?" + query;

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
        if (isPost)
        {
            var body = OAuth1Signer.EncodeForm(pairs.Skip(queryCount));
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        }
        req.Headers.TryAddWithoutValidation("Authorization", Signer(o).CreateAuthorizationHeader(ctx.Request.Method, baseUrl, pairs, token, tokenSecret));
        var accept = ctx.Request.Headers.Accept.ToString();
        req.Headers.TryAddWithoutValidation("Accept", accept.Length > 0 ? accept : "application/json");
        var lang = ctx.Request.Headers.AcceptLanguage.ToString();
        if (lang.Length > 0) req.Headers.TryAddWithoutValidation("Accept-Language", lang);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Upstream res;
        try { res = await SendAsync(factory.CreateClient(HttpClientName), req, o, ct); }
        catch (Exception ex) when (UpstreamFailure(ex, log, "proxy") is { } r)
        {
            if (ex is TimeoutException) log.LogWarning("USOS proxy timeout for {Method} {Path}", ctx.Request.Method, path);
            return r;
        }
        log.LogInformation("USOS proxy {Method} {Path} -> {Status} in {Elapsed:0}ms",
            ctx.Request.Method, path, (int)res.Status, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        ctx.Response.StatusCode = (int)res.Status;
        ctx.Response.ContentType = res.ContentType ?? "application/octet-stream";
        ctx.Response.ContentLength = res.Bytes.Length;
        await ctx.Response.Body.WriteAsync(res.Bytes, ct);
        return Results.Empty;
    }
}
