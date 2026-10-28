namespace Vusos.Core.Domain;

public enum SourceSystem { Usos, Platon }

public enum ConnectionStatus { Disconnected, Connected, Expired, Error }

/// <summary>Tekst w dwóch językach; <see cref="En"/> może być pusty.</summary>
public sealed record LangText(string Pl, string? En = null)
{
    public static LangText Of(string pl) => new(pl);
    public override string ToString() => Pl;
}

/// <summary>Skąd pochodzi wynik: na żywo czy z lokalnego cache (tryb offline / przeciążenie).</summary>
public enum DataOrigin { Live, Cache }

public sealed record Sourced<T>(T Value, DataOrigin Origin, DateTimeOffset FetchedAt, string? Warning = null)
{
    public bool IsStale => Origin == DataOrigin.Cache;
}
