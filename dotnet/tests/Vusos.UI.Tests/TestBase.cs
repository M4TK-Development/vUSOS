using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.UI.Services;
using Vusos.WebHost.Dev;

namespace Vusos.UI.Tests;

public sealed class StubAuth(bool signedIn = true) : IAuthSession
{
    public bool IsSignedIn { get; private set; } = signedIn;
    public string? DisplayName => IsSignedIn ? "Anna Kowalska" : null;
    public string? Email => IsSignedIn ? "anna.kowalska@stu.vistula.edu.pl" : null;
    public event Action? Changed;
    public Task SignInAsync() { IsSignedIn = true; Changed?.Invoke(); return Task.CompletedTask; }
    public Task SignOutAsync() { IsSignedIn = false; Changed?.Invoke(); return Task.CompletedTask; }
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
