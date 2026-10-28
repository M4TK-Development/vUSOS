using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Vusos.WebHost.Hosting;

namespace Vusos.WebHost.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly WebHostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/", "%2F")]
    [InlineData("/kursy", "%2Fkursy")]
    [InlineData("/testy?x=1", "%2Ftesty%3Fx%3D1")]
    public async Task Unauthenticated_pages_redirect_to_login(string path, string expectedReturn)
    {
        var response = await _factory.NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/logowanie?returnUrl=" + expectedReturn, response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/logowanie")]
    [InlineData("/dev-logowanie")]
    public async Task Login_pages_are_public(string path)
    {
        var response = await _factory.NewClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_endpoint_in_dev_mode_leads_to_the_email_form()
    {
        var response = await _factory.NewClient().GetAsync("/auth/login?returnUrl=%2Fkursy");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dev-logowanie?returnUrl=%2Fkursy", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Dev_sign_in_sets_cookie_and_the_app_shell_renders()
    {
        var client = _factory.NewClient();
        var login = await client.PostAsync("/auth/dev-login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = Emails.Anna,
            ["returnUrl"] = "/kursy",
        }));

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/kursy", login.Headers.Location!.OriginalString);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AuthSetup.CookieName + "=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        // Cookie wraca w kolejnych żądaniach (odświeżenie strony nie gubi sesji).
        foreach (var path in new[] { "/", "/kursy", "/testy", "/ustawienia" })
        {
            var page = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("blazor.web.js", html);
            Assert.Contains("<base href=\"/\"", html);
        }
    }

    [Fact]
    public async Task Dev_sign_in_rejects_foreign_domains_with_polish_message()
    {
        var client = _factory.NewClient();
        var response = await client.PostAsync("/auth/dev-login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "ktos@gmail.com",
            ["returnUrl"] = "/",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/dev-logowanie?", location);
        Assert.Contains("Dozwolone%20s%C4%85%20tylko%20konta%20uczelni", location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    public async Task Return_url_must_be_local(string returnUrl)
    {
        var client = _factory.NewClient();
        var response = await client.PostAsync("/auth/dev-login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = Emails.Anna,
            ["returnUrl"] = returnUrl,
        }));

        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Logout_clears_the_session()
    {
        var client = await _factory.SignedInClientAsync(Emails.Anna);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        var logout = await client.GetAsync("/auth/logout");
        Assert.Equal("/logowanie", logout.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public void Dev_mode_is_refused_in_production_unless_allowed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var f = new WebHostFactory().WithWebHostBuilder(b => b.UseEnvironment("Production"));
            _ = f.Services;
        });
        Assert.Contains("Auth:Mode=dev", ex.Message);
    }

    [Fact]
    public void Dev_mode_in_production_can_be_allowed_explicitly()
    {
        using var f = new WebHostFactory(extra: new() { ["Auth:AllowDevInProduction"] = "true" })
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));
        _ = f.Services;
    }
}

public sealed class EntraAuthTests : IDisposable
{
    private readonly WebHostFactory _entra = new(auth: "entra", extra: new()
    {
        ["Auth:ClientId"] = "11111111-2222-3333-4444-555555555555",
        ["Auth:ClientSecret"] = "secret",
    });

    // Metadane OIDC są podstawiane lokalnie (bez sieci).
    private readonly WebApplicationFactory<Program> _factory;

    public EntraAuthTests()
    {
        _factory = _entra.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { AuthorizationEndpoint = "https://login.example.test/organizations/oauth2/v2.0/authorize" }))));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _entra.Dispose();
    }

    private OpenIdConnectOptions Oidc() =>
        _factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

    [Fact]
    public void Entra_mode_requires_client_credentials()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var f = new WebHostFactory(auth: "entra");
            _ = f.Services;
        });
        Assert.Contains("Auth:ClientId", ex.Message);
    }

    [Fact]
    public async Task Login_challenges_entra_with_code_flow_and_pkce()
    {
        var response = await _factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/auth/login?returnUrl=%2Fkursy");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://login.example.test/organizations/oauth2/v2.0/authorize?", location);
        Assert.Contains("client_id=11111111-2222-3333-4444-555555555555", location);
        Assert.Contains("response_type=code", location);
        Assert.Contains("code_challenge=", location);
        Assert.Contains("redirect_uri=https%3A%2F%2Fvusos.test%2Fsignin-oidc", location);
    }

    [Fact]
    public void Issuer_validation_accepts_any_entra_tenant_but_nothing_else()
    {
        var validate = Oidc().TokenValidationParameters.IssuerValidator!;
        var tenant = "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0";

        Assert.Equal(tenant, validate(tenant, null!, new TokenValidationParameters()));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => validate("https://evil.example/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0", null!, new TokenValidationParameters()));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => validate("https://login.microsoftonline.com/common/v2.0", null!, new TokenValidationParameters()));
    }

    private async Task<(TokenValidatedContext Context, ClaimsPrincipal Principal)> ValidateAsync(params Claim[] claims)
    {
        var http = new DefaultHttpContext { RequestServices = _factory.Services };
        var options = Oidc();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var ctx = new TokenValidatedContext(http, new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            options, principal, new AuthenticationProperties());
        await options.Events.OnTokenValidated(ctx);
        return (ctx, principal);
    }

    [Fact]
    public async Task University_account_gets_a_stable_per_tenant_user_id()
    {
        var (ctx, principal) = await ValidateAsync(
            new Claim("tid", "TENANT-1"), new Claim("oid", "OID-1"),
            new Claim("preferred_username", "Anna.Kowalska@stu.vistula.edu.pl"), new Claim("name", "Anna Kowalska"));

        Assert.Null(ctx.Result?.Failure);
        Assert.Equal("entra:tenant-1:oid-1", principal.FindFirst(UserIdentity.UidClaim)!.Value);
        Assert.Equal("anna.kowalska@stu.vistula.edu.pl", principal.FindFirst(ClaimTypes.Email)!.Value);
        Assert.Equal("Anna Kowalska", principal.FindFirst(ClaimTypes.Name)!.Value);
    }

    [Fact]
    public async Task Accounts_outside_allowed_domains_fail_with_polish_message()
    {
        var (ctx, _) = await ValidateAsync(
            new Claim("tid", "t"), new Claim("oid", "o"), new Claim("preferred_username", "ktos@firma.pl"));

        Assert.NotNull(ctx.Result?.Failure);
        Assert.Contains("To konto nie należy do uczelni", ctx.Result!.Failure!.Message);
        Assert.Contains("@vistula.edu.pl", ctx.Result.Failure.Message);
    }

    [Fact]
    public async Task Lookalike_domains_are_rejected()
    {
        var (ctx, _) = await ValidateAsync(
            new Claim("tid", "t"), new Claim("oid", "o"), new Claim("preferred_username", "ktos@evilvistula.edu.pl"));

        Assert.NotNull(ctx.Result?.Failure);
    }
}
