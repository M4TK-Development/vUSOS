using System.Text.Json;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;
using Vusos.Core.Services.Json;

namespace Vusos.Core.Tests.Services;

public class VusosJsonTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(1));

    private static T RoundTrip<T>(T value) => VusosJson.Deserialize<T>(VusosJson.Serialize(value))!;

    [Fact]
    public void DomainRecords_RoundTrip()
    {
        var profile = new StudentProfile("1", "Jan", "Kowalski", "j@x.pl", "123", null, [new Programme("p", new LangText("Informatyka", "CS"), "active", "I")]);
        Assert.Equal(profile.FullName, RoundTrip(profile).FullName);
        Assert.Equal("CS", RoundTrip(profile).Programmes[0].Name.En);

        var grade = new Grade("g", "t", "c", LangText.Of("Matematyka"), "wyk", 1, "4.5", null, true, true, T, "Dr X", null);
        Assert.Equal(grade, RoundTrip(grade));

        var payment = new Payment("pay", "czesne", LangText.Of("Opłata"), 100.5m, 20m, "PLN", new DateOnly(2026, 11, 1), PaymentState.PartiallyPaid, null, "PL00");
        var p2 = RoundTrip(payment);
        Assert.Equal(payment, p2);
        Assert.Equal(80.5m, p2.Outstanding);

        var entry = new TimetableEntry("e", SourceSystem.Usos, T, T.AddHours(1), "c", LangText.Of("Fizyka"), "lab", 2, "101", "A", ["Dr A"], null);
        var e2 = RoundTrip(entry);
        Assert.Equal(entry.Start, e2.Start);
        Assert.Equal(entry.Lecturers, e2.Lecturers);
        Assert.Equal(SourceSystem.Usos, e2.Source);

        var section = new LmsSection("s", "Sekcja", null, [new LmsModule("m", "c", "s", "Sekcja", "Plik", LmsModuleType.Resource, "resource", "https://u", null,
            [new LmsFile("a.pdf", "https://f", "application/pdf", 10, T)], null, true, "7")]);
        Assert.Equal(LmsModuleType.Resource, RoundTrip(section).Modules[0].Type);
        Assert.Equal("7", RoundTrip(section).Modules[0].InstanceId);

        var quiz = new QuizAttemptData(new QuizAttempt("a", "q", 1, QuizAttemptState.InProgress, T, null, null, 0), [], [new QuizAnswer("n", "v")]);
        Assert.Equal(QuizAttemptState.InProgress, RoundTrip(quiz).Attempt.State);
    }

    [Fact]
    public void Enums_AreWrittenAsStrings_AndDatesAsIso()
    {
        var json = VusosJson.Serialize(new Announcement("a", SourceSystem.Platon, "T", "<p/>", T, null, null, null));
        Assert.Contains("\"Platon\"", json);
        Assert.Contains("2026-10-28T09:30:00+01:00", json);
    }

    [Fact]
    public void DashboardData_WithEnumKeyedDictionaries_RoundTrips()
    {
        var d = new DashboardData(null, [], [], [], [], 3, [],
            new Dictionary<SourceSystem, ConnectionStatus> { [SourceSystem.Usos] = ConnectionStatus.Connected, [SourceSystem.Platon] = ConnectionStatus.Expired },
            new Dictionary<SourceSystem, DataOrigin> { [SourceSystem.Usos] = DataOrigin.Cache });
        var d2 = RoundTrip(d);
        Assert.Equal(ConnectionStatus.Expired, d2.Connections[SourceSystem.Platon]);
        Assert.Equal(DataOrigin.Cache, d2.Origins[SourceSystem.Usos]);
        Assert.Equal(3, d2.UnreadMessages);
    }

    [Fact]
    public void Sourced_And_ConnectionInfo_RoundTrip()
    {
        var info = new ConnectionInfo(SourceSystem.Usos, ConnectionStatus.Error, "api", T, "błąd");
        Assert.Equal(info, RoundTrip(info));
        var s = new Sourced<IReadOnlyList<int>>([1, 2], DataOrigin.Cache, T, "w");
        Assert.True(RoundTrip(s).IsStale);
    }

    [Fact]
    public void UsosCredentials_ArePolymorphic()
    {
        UsosCredentials[] all = [new UsosApiCredentials("t", "s", "https://u/"), new UsosWebCredentials("c=1", "https://w/"), new UsosMockCredentials()];
        foreach (var c in all)
        {
            var json = VusosJson.Serialize(c);
            Assert.Contains("\"mode\":", json);
            Assert.Equal(c, VusosJson.Deserialize<UsosCredentials>(json));
        }
        Assert.Contains("\"mode\":\"api\"", VusosJson.Serialize<UsosCredentials>(all[0]));
    }

    [Fact]
    public void PlatonCredentials_ArePolymorphic_AndTolerateDiscriminatorNotFirst()
    {
        PlatonCredentials[] all = [new PlatonWsCredentials("tok", null, "https://p/", 5), new PlatonWebCredentials("c", "sk", "https://p/"), new PlatonMockCredentials()];
        foreach (var c in all)
            Assert.Equal(c, VusosJson.Deserialize<PlatonCredentials>(VusosJson.Serialize(c)));

        var reordered = VusosJson.Deserialize<PlatonCredentials>("{\"wsToken\":\"a\",\"baseUrl\":\"u\",\"mode\":\"ws\"}");
        Assert.Equal(new PlatonWsCredentials("a", null, "u"), reordered);
    }

    [Fact]
    public void CacheKey_IsStable_AndDependsOnArgs()
    {
        var a = CacheKey.Create("local", SourceSystem.Usos, "GetGrades", new object?[] { new[] { "t1", "t2" } });
        var b = CacheKey.Create("local", SourceSystem.Usos, "GetGrades", new object?[] { new[] { "t1", "t2" } });
        var c = CacheKey.Create("local", SourceSystem.Usos, "GetGrades", new object?[] { new[] { "t1" } });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("local:Usos:GetGrades:", a);
        Assert.StartsWith(CacheKey.SystemPrefix("local", SourceSystem.Usos), a);
        Assert.StartsWith(CacheKey.MethodPrefix("local", SourceSystem.Usos, "GetGrades"), a);
        Assert.Equal(CacheKey.Create("x", SourceSystem.Platon, "M"), CacheKey.Create("x", SourceSystem.Platon, "M"));
    }
}
