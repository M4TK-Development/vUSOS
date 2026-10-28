using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.UI.Services;
using Vusos.WebHost.Dev;

namespace Vusos.UI.Tests;

public sealed class StubAuth(bool signedIn = true, bool external = false) : IAuthSession
{
    public bool IsSignedIn { get; private set; } = signedIn;
    public bool SupportsExternalIdentityProvider { get; } = external;
    public string? DisplayName => IsSignedIn ? "Anna Kowalska" : null;
    public string? Email => IsSignedIn ? "anna.kowalska@stu.vistula.edu.pl" : null;
    public event Action? Changed;
    public Task SignInAsync() { IsSignedIn = true; Changed?.Invoke(); return Task.CompletedTask; }
    public Task SignOutAsync() { IsSignedIn = false; Changed?.Invoke(); return Task.CompletedTask; }
}

/// <summary>IConnectFlows zapisujące wywołania; opcjonalnie rzuca wyjątek (np. jak WebHost w trybie live dla Platona).</summary>
public sealed class RecordingFlows : IConnectFlows
{
    public List<string> Calls { get; } = [];
    public Exception? Failure { get; set; }
    public Action? OnStart { get; set; }

    private Task Record(string call)
    {
        Calls.Add(call);
        if (Failure is not null) throw Failure;
        OnStart?.Invoke();
        return Task.CompletedTask;
    }

    public Task StartUsosAsync() => Record("usos");
    public Task StartPlatonAsync() => Record("platon");
    public Task SubmitPlatonTokenAsync(string pasted) => Record("platon-token:" + pasted);
    public Task SubmitUsosCookiesAsync(string cookies) => Record("usos-cookies:" + cookies);
    public Task SubmitPlatonCookiesAsync(string cookies, string sesskey) => Record($"platon-cookies:{cookies}:{sesskey}");
}

public sealed class RecordingOpener : IExternalOpener
{
    public List<string> Urls { get; } = [];
    public List<string> Files { get; } = [];
    public Task OpenUrlAsync(string url) { Urls.Add(url); return Task.CompletedTask; }
    public Task OpenFileAsync(string localPath) { Files.Add(localPath); return Task.CompletedTask; }
}

/// <summary>Kontekst bUnit z fałszywymi usługami Core (te same, co w WebHost przy Adapters:Mode=fake).</summary>
public abstract class UiTestBase : BunitContext
{
    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    protected UiTestBase(bool signedIn = true)
    {
        Services.AddVusosUi();
        Services.AddFakeVusosServices();
        Services.AddSingleton<IAuthSession>(new StubAuth(signedIn));
        Services.AddSingleton<RecordingOpener>();
        Services.AddSingleton<IExternalOpener>(sp => sp.GetRequiredService<RecordingOpener>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        Backend.Latency = TimeSpan.Zero;
    }

    protected FakeBackend Backend => Services.GetRequiredService<FakeBackend>();
    protected FakeConnectivity Connectivity => Services.GetRequiredService<FakeConnectivity>();
    protected FakeConnectionManager Connections => Services.GetRequiredService<FakeConnectionManager>();
    protected RecordingOpener Opener => Services.GetRequiredService<RecordingOpener>();
}
