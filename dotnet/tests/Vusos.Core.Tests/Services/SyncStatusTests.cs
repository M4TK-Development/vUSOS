using Vusos.Core.Domain;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public class SyncStatusTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_IsLiveWithNothingPending()
    {
        var s = new SyncStatus().Get(SourceSystem.Platon);
        Assert.Equal(SourceSystem.Platon, s.System);
        Assert.Equal(0, s.PendingActions);
        Assert.Null(s.LastLiveAt);
    }

    [Fact]
    public void Report_UpdatesState_AndRaisesChanged()
    {
        var status = new SyncStatus();
        var events = new List<SyncState>();
        status.Changed += (_, s) => events.Add(s);

        status.Report(SourceSystem.Usos, DataOrigin.Live, T);
        status.Report(SourceSystem.Usos, DataOrigin.Cache, T.AddMinutes(-5), "offline");
        status.SetPending(SourceSystem.Usos, 2);
        status.SetPending(SourceSystem.Usos, 2); // bez zmiany — bez zdarzenia

        Assert.Equal(3, events.Count);
        var s = status.Get(SourceSystem.Usos);
        Assert.Equal(DataOrigin.Cache, s.LastOrigin);
        Assert.Equal(T, s.LastLiveAt);
        Assert.Equal(T.AddMinutes(-5), s.LastCacheAt);
        Assert.Equal("offline", s.LastError);
        Assert.Equal(2, s.PendingActions);

        status.Report(SourceSystem.Usos, DataOrigin.Live, T.AddMinutes(1));
        Assert.Null(status.Get(SourceSystem.Usos).LastError);
        Assert.Equal(SourceSystem.Platon, status.Get(SourceSystem.Platon).System);
        Assert.Equal(DataOrigin.Live, status.Get(SourceSystem.Platon).LastOrigin);
    }

    [Fact]
    public async Task ConcurrentReports_AreThreadSafe()
    {
        var status = new SyncStatus();
        var count = 0;
        status.Changed += (_, _) => Interlocked.Increment(ref count);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
            status.Report(SourceSystem.Usos, DataOrigin.Live, T.AddSeconds(i)))));
        Assert.Equal(200, count);
        Assert.NotNull(status.Get(SourceSystem.Usos).LastLiveAt);
    }
}
