using System.Text.Json.Serialization;

namespace Vusos.Core.Adapters;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(UsosApiCredentials), "api")]
[JsonDerivedType(typeof(UsosWebCredentials), "web")]
[JsonDerivedType(typeof(UsosMockCredentials), "mock")]
public abstract record UsosCredentials;

/// <summary>USOS API, OAuth 1.0a. Podpisywanie: lokalnie (gdy aplikacja ma consumer secret) albo przez Vusos.Server.</summary>
public sealed record UsosApiCredentials(string AccessToken, string AccessTokenSecret, string BaseUrl) : UsosCredentials;

/// <summary>Sesja USOSweb (nagłówek Cookie) — tryb awaryjny, parsowanie HTML.</summary>
public sealed record UsosWebCredentials(string Cookies, string BaseUrl) : UsosCredentials;

public sealed record UsosMockCredentials : UsosCredentials;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(PlatonWsCredentials), "ws")]
[JsonDerivedType(typeof(PlatonWebCredentials), "web")]
[JsonDerivedType(typeof(PlatonMockCredentials), "mock")]
public abstract record PlatonCredentials;

/// <summary>Moodle web services (wstoken z launch.php / tool_mobile).</summary>
public sealed record PlatonWsCredentials(string WsToken, string? PrivateToken, string BaseUrl, int? UserId = null) : PlatonCredentials;

/// <summary>Sesja przeglądarkowa Moodle: cookie MoodleSession + sesskey → lib/ajax/service.php.</summary>
public sealed record PlatonWebCredentials(string Cookies, string SessKey, string BaseUrl, int? UserId = null) : PlatonCredentials;

public sealed record PlatonMockCredentials : PlatonCredentials;
