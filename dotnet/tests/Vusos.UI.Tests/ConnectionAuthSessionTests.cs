using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Hosting;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.UI.Services;
using Vusos.WebHost.Dev;

namespace Vusos.UI.Tests;

public sealed class ConnectionAuthSessionTests : IDisposable
{
    private readonly FakeBackend _backend;
    private readonly FakeConnectionManager _connections;
    private readonly FakeSecureStore _store = new();
    private readonly ConnectionIdentity _identity;
    private readonly RecordingCache _cache = new();
    private readonly RecordingMaterials _materials = new();
    private readonly string _materialsDir = Path.Combine(Path.GetTempPath(), "vusos-ui-tests-" + Guid.NewGuid().ToString("N"));

    public ConnectionAuthSessionTests()
    {
        _backend = new FakeBackend(new FakeSyncStatus(), new FakeConnectivity()) { Latency = TimeSpan.Zero };
        _connections = new FakeConnectionManager(_backend);
        _identity = new ConnectionIdentity(_connections, _store);
        Directory.CreateDirectory(_materialsDir);
        File.WriteAllText(Path.Combine(_materialsDir, "a.txt"), "x");
    }

    public void Dispose()
    {
        if (Directory.Exists(_materialsDir)) Directory.Delete(_materialsDir, recursive: true);
    }

    private ConnectionAuthSession Session() => new(_identity, new LocalDataSignOutHandler(
        _connections, _identity, _materials, _cache, new Paths(_materialsDir)));

    [Fact]
    public async Task Identity_comes_from_the_connected_system_not_from_a_hardcoded_user()
    {
        var session = Session();
        Assert.False(session.SupportsExternalIdentityProvider);

        await session.InitializeAsync(); // FakeConnectionManager startuje jak tryb mock: oba systemy połączone

        Assert.True(session.IsSignedIn);
        Assert.Equal("Anna Kowalska", session.DisplayName);
        Assert.Equal("anna.kowalska@stu.vistula.edu.pl", session.Email);
    }

    [Fact]
    public async Task Changed_flows_from_identity_to_session_subscribers()
    {
        await _connections.DisconnectAsync(SourceSystem.Usos);
        await _connections.DisconnectAsync(SourceSystem.Platon);
        var session = Session();
        await session.InitializeAsync();
        Assert.False(session.IsSignedIn);
        var signedIn = new TaskCompletionSource();
        session.Changed += () => { if (session.IsSignedIn) signedIn.TrySetResult(); };

        await _connections.ConnectPlatonAsync(new PlatonMockCredentials());

        await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Anna Kowalska", session.DisplayName);
        Assert.Null(session.Email); // Platon nie zna e-maila
    }

    [Fact]
    public async Task SignOut_disconnects_both_systems_clears_identity_cache_and_materials()
    {
        var session = Session();
        await session.InitializeAsync();
        Assert.True(session.IsSignedIn);

        await session.SignOutAsync();

        var status = await _connections.GetStatusAsync();
        Assert.All(status.Values, s => Assert.Equal(ConnectionStatus.Disconnected, s.Status));
        Assert.False(session.IsSignedIn);
        Assert.Null(await _store.GetAsync(ConnectionIdentity.StoreKey));
        Assert.True(_cache.Cleared);
        Assert.True(_materials.Removed.Count > 0);
        Assert.False(Directory.Exists(_materialsDir));
    }

    [Fact]
    public void AddVusosConnectionAuth_registers_the_connection_session_as_IAuthSession()
    {
        var services = new ServiceCollection();
        services.AddVusosConnectionAuth();
        Assert.Contains(services, d => d.ServiceType == typeof(ConnectionAuthSession) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthSession));
        Assert.Contains(services, d => d.ServiceType == typeof(ISignOutHandler));
    }

    private sealed class Paths(string materials) : IAppPaths
    {
        public string DataDirectory => Path.GetDirectoryName(materials)!;
        public string MaterialsDirectory => materials;
    }

    private sealed class RecordingCache : ICacheStore
    {
        public bool Cleared { get; private set; }
        public Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<CacheEntry?>(null);
        public Task SetAsync(string key, string json, DateTimeOffset fetchedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) { Cleared = true; return Task.CompletedTask; }
    }

    private sealed class RecordingMaterials : IMaterialService
    {
        public List<string> Removed { get; } = [];
        public IObservable<DownloadProgress> Progress => throw new NotSupportedException();
        public Task<IReadOnlyList<MaterialRecord>> ListAsync(string? courseId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MaterialRecord>>([new MaterialRecord("m1", "c1", "x", "a.txt", "https://x", null, 1, null, null, DownloadState.Done, null, DateTimeOffset.UnixEpoch)]);
        public Task RemoveAsync(string materialId, CancellationToken ct = default) { Removed.Add(materialId); return Task.CompletedTask; }
        public Task<MaterialRecord?> FindAsync(string remoteUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MaterialRecord> EnqueueAsync(string courseId, LmsModule module, LmsFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> EnqueueCourseAsync(string courseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> GetTotalSizeAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task ProcessQueueAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
