using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Usos;

namespace Vusos.Core.Tests.Usos;

public class UsosAdapterFactoryTests
{
    private static readonly FixedClock Clock = new("2025-11-20T10:00:00Z");

    [Fact]
    public void Dispatches_on_the_credentials_type()
    {
        var http = new HttpClient(new FakeHandler(_ => Http.JsonText("{}")));
        var signer = new LocalUsosSigner(http, "k", "s");
        Assert.IsType<UsosApiAdapter>(UsosAdapterFactory.Create(new UsosApiCredentials("t", "s", "https://u/"), http, signer, Clock));
        Assert.IsType<UsosWebAdapter>(UsosAdapterFactory.Create(new UsosWebCredentials("a=b", "https://w/"), http, null, Clock));
        Assert.IsType<UsosMockAdapter>(UsosAdapterFactory.Create(new UsosMockCredentials(), http, null, Clock));
    }

    [Fact]
    public void Api_credentials_without_a_signer_are_a_configuration_error()
    {
        var http = new HttpClient(new FakeHandler(_ => Http.JsonText("{}")));
        var ex = Assert.Throws<AdapterUnavailableException>(() =>
            UsosAdapterFactory.Create(new UsosApiCredentials("t", "s", "https://u/"), http, null, Clock));
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Api_mode_calls_the_installation_from_the_credentials()
    {
        var handler = new FakeHandler(_ => Http.JsonText(Fx.Text("user.json")));
        var http = new HttpClient(handler);
        var adapter = UsosAdapterFactory.Create(
            new UsosApiCredentials("t", "s", "https://other.example/api/"), http, new LocalUsosSigner(http, "k", "c"), Clock);
        await adapter.GetProfileAsync();
        Assert.StartsWith("https://other.example/api/services/users/user?", handler.Requests[0].Url.ToString());
    }

    [Fact]
    public async Task Mock_adapter_from_the_factory_works_without_network()
    {
        var adapter = UsosAdapterFactory.Create(new UsosMockCredentials(), new HttpClient(), null, Clock);
        Assert.Equal("Anna", (await adapter.GetProfileAsync()).FirstName);
    }

    [Fact]
    public void Credentials_round_trip_through_json_polymorphism()
    {
        var json = System.Text.Json.JsonSerializer.Serialize<UsosCredentials>(new UsosApiCredentials("t", "s", "https://u/"));
        Assert.Contains("\"mode\":\"api\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<UsosCredentials>(json);
        Assert.Equal(new UsosApiCredentials("t", "s", "https://u/"), back);
    }

    [Fact]
    public void Signing_strategy_follows_the_configuration()
    {
        var http = new HttpClient();
        var remote = new Vusos.Core.Services.UniversityOptions { SigningServerUrl = "https://signing.example/", UsosConsumerKey = "k", UsosConsumerSecret = "s" };
        var local = new Vusos.Core.Services.UniversityOptions { UsosConsumerKey = "k", UsosConsumerSecret = "s" };
        var none = new Vusos.Core.Services.UniversityOptions();

        Assert.IsType<RemoteUsosSigner>(UsosAdapterFactory.CreateSigner(remote, http));
        Assert.IsType<RemoteUsosOAuthClient>(UsosAdapterFactory.CreateOAuthClient(remote, http));
        Assert.IsType<LocalUsosSigner>(UsosAdapterFactory.CreateSigner(local, http));
        Assert.IsType<LocalUsosOAuthClient>(UsosAdapterFactory.CreateOAuthClient(local, http));
        Assert.Null(UsosAdapterFactory.CreateSigner(none, http));
        Assert.Null(UsosAdapterFactory.CreateOAuthClient(none, http));
    }
}
