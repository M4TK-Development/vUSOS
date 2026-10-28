using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.WebHost.Hosting;

namespace Vusos.WebHost.Tests;

public sealed class UserIsolationTests : IDisposable
{
    private readonly WebHostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private UserCoreFactory Cores => _factory.Cores;

    [Fact]
    public void Users_get_separate_data_directories_named_by_hash()
    {
        var anna = Cores.GetOrCreate("dev:" + Emails.Anna);
        var jan = Cores.GetOrCreate("dev:" + Emails.Jan);

        Assert.NotEqual(anna.Paths.DataDirectory, jan.Paths.DataDirectory);
        Assert.Equal(Path.Combine(_factory.StorageRoot, "users", UserIdentity.Hash("dev:" + Emails.Anna)), anna.Paths.DataDirectory);
        Assert.Equal(16, anna.UserHash.Length);
        Assert.Equal(Path.Combine(anna.Paths.DataDirectory, "materials"), anna.Paths.MaterialsDirectory);
        Assert.DoesNotContain("anna", anna.Paths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(anna.Paths.MaterialsDirectory));
    }

    [Fact]
    public void Same_user_gets_the_same_core_and_concurrent_calls_create_it_once()
    {
        var cores = Cores; // host startowany przed równoległą częścią
        var all = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(8)
            .Select(_ => cores.GetOrCreate("dev:" + Emails.Anna)).ToList();

        Assert.All(all, c => Assert.Same(all[0], c));
        Assert.Equal(1, Cores.LoadedCount);
    }

    [Fact]
    public async Task Connection_managers_are_isolated_between_users()
    {
        var anna = Cores.GetOrCreate("dev:" + Emails.Anna);
        var jan = Cores.GetOrCreate("dev:" + Emails.Jan);
        var annaConnections = anna.Get<IConnectionManager>();
        var janConnections = jan.Get<IConnectionManager>();
        Assert.NotSame(annaConnections, janConnections);
        Assert.NotSame(anna.Get<ISyncStatus>(), jan.Get<ISyncStatus>());

        // Tryb mock podłącza dane demonstracyjne od razu; rozłączenie Anny nie może dotknąć Jana.
        await annaConnections.DisconnectAsync(SourceSystem.Usos);

        var annaStatus = await annaConnections.GetStatusAsync();
        var janStatus = await janConnections.GetStatusAsync();
        Assert.Equal(ConnectionStatus.Disconnected, annaStatus[SourceSystem.Usos].Status);
        Assert.Equal(ConnectionStatus.Connected, janStatus[SourceSystem.Usos].Status);
        await Assert.ThrowsAsync<NotConnectedException>(() => annaConnections.GetUsosAsync());
        Assert.NotNull(await janConnections.GetUsosAsync());

        // Poświadczenia i baza leżą w katalogu właściciela.
        Assert.True(File.Exists(Path.Combine(anna.Paths.DataDirectory, "secure.json")));
        Assert.True(File.Exists(Path.Combine(jan.Paths.DataDirectory, "secure.json")));
        Assert.True(File.Exists(Path.Combine(anna.Paths.DataDirectory, "vusos.db")));
        Assert.NotEqual(File.ReadAllText(Path.Combine(anna.Paths.DataDirectory, "secure.json")),
            File.ReadAllText(Path.Combine(jan.Paths.DataDirectory, "secure.json")));
    }

    [Fact]
    public void User_scope_in_cache_keys_is_the_user_hash()
    {
        var anna = Cores.GetOrCreate("dev:" + Emails.Anna);
        Assert.Equal(anna.UserHash, anna.Get<AdapterOptions>().UserScope);
        Assert.True(anna.Get<AdapterOptions>().IsMock);
        Assert.True(anna.Get<IConnectivity>().IsOnline);
        Assert.False(anna.Get<IConnectivity>().IsMetered);
        Assert.True(anna.Get<BackgroundSyncCoordinator>().IsStarted);
    }

    [Fact]
    public async Task Real_core_serves_mock_dashboard_end_to_end()
    {
        var core = Cores.GetOrCreate("dev:" + Emails.Anna);
        await core.Get<IConnectionManager>().ConnectUsosAsync(new UsosMockCredentials());
        await core.Get<IConnectionManager>().ConnectPlatonAsync(new PlatonMockCredentials());

        var dashboard = await core.Get<IDashboardService>().GetAsync();

        Assert.NotNull(dashboard);
        Assert.NotNull(await core.Get<IQuizService>().GetQuizzesAsync());
    }

    [Fact]
    public async Task Circuit_scopes_forward_to_the_signed_in_users_core()
    {
        using var annaScope1 = _factory.CircuitScope(Emails.Anna);
        using var annaScope2 = _factory.CircuitScope(Emails.Anna);
        using var janScope = _factory.CircuitScope(Emails.Jan);

        var anna1 = annaScope1.ServiceProvider.GetRequiredService<IConnectionManager>();
        var anna2 = annaScope2.ServiceProvider.GetRequiredService<IConnectionManager>();
        var jan = janScope.ServiceProvider.GetRequiredService<IConnectionManager>();

        Assert.Same(anna1, anna2); // dwie karty tego samego użytkownika dzielą stan
        Assert.NotSame(anna1, jan);
        Assert.Same(Cores.GetOrCreate("dev:" + Emails.Anna).Get<IConnectionManager>(), anna1);
        Assert.Equal(Cores.GetOrCreate("dev:" + Emails.Jan).Paths.MaterialsDirectory,
            janScope.ServiceProvider.GetRequiredService<IAppPaths>().MaterialsDirectory);
        Assert.NotNull(annaScope1.ServiceProvider.GetRequiredService<ISyncStatus>());
        Assert.NotNull(annaScope1.ServiceProvider.GetRequiredService<IMaterialService>());
        Assert.NotNull(annaScope1.ServiceProvider.GetRequiredService<IQuizService>());
        Assert.NotNull(annaScope1.ServiceProvider.GetRequiredService<IDashboardService>());
        Assert.NotNull(annaScope1.ServiceProvider.GetRequiredService<IConnectivity>());

        var current = annaScope1.ServiceProvider.GetRequiredService<ICurrentUserCore>();
        Assert.Equal("dev:" + Emails.Anna, current.UserId);
        Assert.Equal(UserIdentity.Hash("dev:" + Emails.Anna), current.UserHash);

        var auth = annaScope1.ServiceProvider.GetRequiredService<Vusos.UI.Services.IAuthSession>();
        Assert.True(auth.IsSignedIn);
        Assert.Equal(Emails.Anna, auth.Email);
        Assert.Equal("Anna Kowalska", auth.DisplayName);

        await anna1.DisconnectAsync(SourceSystem.Platon);
        Assert.Equal(ConnectionStatus.Disconnected, (await anna1.GetStatusAsync())[SourceSystem.Platon].Status);
        Assert.Equal(ConnectionStatus.Connected, (await jan.GetStatusAsync())[SourceSystem.Platon].Status);
    }

    [Fact]
    public void Scope_without_signed_in_user_cannot_resolve_core_services()
    {
        using var scope = _factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<Vusos.UI.Services.IAuthSession>();
        Assert.False(auth.IsSignedIn);
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IConnectionManager>());
    }

    [Fact]
    public async Task Circuit_handler_pins_the_user_until_the_circuit_closes()
    {
        var time = new ManualTime();
        await using var cores = NewFactory(time);
        cores.GetOrCreate("u1");
        cores.Pin("u1");
        cores.GetOrCreate("u2");

        time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(1, await cores.EvictIdleAsync(TimeSpan.FromHours(2))); // u2; u1 ma otwarty obwód
        Assert.Equal(1, cores.LoadedCount);

        cores.Unpin("u1");
        Assert.Equal(0, await cores.EvictIdleAsync(TimeSpan.FromHours(2))); // dopiero co zwolniony – licznik bezczynności od zera
        time.Advance(TimeSpan.FromHours(2.5));
        Assert.Equal(1, await cores.EvictIdleAsync(TimeSpan.FromHours(2)));
        Assert.Equal(0, cores.LoadedCount);
    }

    [Fact]
    public async Task Idle_user_is_disposed_and_recreated_with_the_same_data()
    {
        var time = new ManualTime();
        await using var cores = NewFactory(time);
        var first = cores.GetOrCreate("u1");
        await first.Get<IConnectionManager>().DisconnectAsync(SourceSystem.Usos);

        time.Advance(TimeSpan.FromHours(1));
        cores.GetOrCreate("u1"); // dostęp odświeża licznik
        time.Advance(TimeSpan.FromHours(1.5));
        Assert.Equal(0, await cores.EvictIdleAsync(TimeSpan.FromHours(2)));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await cores.EvictIdleAsync(TimeSpan.FromHours(2)));

        var second = cores.GetOrCreate("u1");
        Assert.NotSame(first, second);
        Assert.Equal(first.Paths.DataDirectory, second.Paths.DataDirectory);
        var status = await second.Get<IConnectionManager>().GetStatusAsync();
        Assert.Equal(ConnectionStatus.Disconnected, status[SourceSystem.Usos].Status); // stan przetrwał na dysku
    }

    private UserCoreFactory NewFactory(TimeProvider time)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Adapters:Mode"] = "mock" }).Build();
        var storage = Options.Create(new StorageOptions { Root = Path.Combine(_factory.StorageRoot, "unit") });
        return new UserCoreFactory(config, new EphemeralDataProtectionProvider(), NullLoggerFactory.Instance, storage, time);
    }
}

public sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * TimeSpan.TicksPerSecond));
}

public sealed class SecureStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vusos-secure-" + Guid.NewGuid().ToString("N"));

    public SecureStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DataProtectionSecureStore Store(IDataProtectionProvider dp, string user) =>
        new(dp, user, Path.Combine(_dir, "secure.json"));

    [Fact]
    public async Task Values_are_encrypted_on_disk_and_survive_restart()
    {
        var dp = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, "keys")));
        await Store(dp, "u1").SetAsync("conn:usos", "super-secret-token");

        var raw = await File.ReadAllTextAsync(Path.Combine(_dir, "secure.json"));
        Assert.DoesNotContain("super-secret-token", raw);
        Assert.DoesNotContain("conn:usos", raw);

        var again = Store(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, "keys"))), "u1");
        Assert.Equal("super-secret-token", await again.GetAsync("conn:usos"));
        await again.RemoveAsync("conn:usos");
        Assert.Null(await Store(dp, "u1").GetAsync("conn:usos"));
    }

    [Fact]
    public async Task Another_users_protector_cannot_read_the_file()
    {
        var dp = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, "keys")));
        await Store(dp, "u1").SetAsync("k", "v");

        var other = Store(dp, "u2");
        Assert.Null(await other.GetAsync("k"));
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains("unreadable", StringComparison.Ordinal));
    }
}
