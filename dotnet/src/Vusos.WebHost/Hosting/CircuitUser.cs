using Microsoft.AspNetCore.Components;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Użytkownik bieżącego zakresu (obwodu Blazora albo żądania HTTP). W obwodzie HttpContext nie jest wiarygodny po pierwszym
/// renderze, więc tożsamość jest brana z <see cref="AuthenticationStateProvider"/> (ustawianego z HttpContext.User przy
/// tworzeniu obwodu) i zapamiętywana; poza obwodem (endpointy) – z <see cref="IHttpContextAccessor"/>.
/// </summary>
public sealed class CircuitUser(AuthenticationStateProvider authentication, IHttpContextAccessor httpContext)
{
    private ClaimsPrincipal? _principal;

    public ClaimsPrincipal Principal
    {
        get
        {
            if (_principal is { Identity.IsAuthenticated: true }) return _principal;
            return Capture();
        }
    }

    public string? UserId => UserIdentity.GetUserId(Principal);
    public string? UserHash => UserId is { } id ? UserIdentity.Hash(id) : null;
    public bool IsSignedIn => UserId is not null;
    public string? Email => UserIdentity.GetEmail(Principal);
    public string? DisplayName => UserIdentity.GetDisplayName(Principal);

    public string RequireUserId() => UserId ?? throw new InvalidOperationException("Brak zalogowanego użytkownika.");

    private ClaimsPrincipal Capture()
    {
        try
        {
            var task = authentication.GetAuthenticationStateAsync();
            if (task.IsCompletedSuccessfully && task.Result.User.Identity?.IsAuthenticated == true)
                return _principal = task.Result.User;
        }
        catch (InvalidOperationException)
        {
            // poza obwodem stan nie jest ustawiony – próbujemy HttpContext
        }

        if (httpContext.HttpContext?.User is { Identity.IsAuthenticated: true } user)
            return _principal = user;
        return new ClaimsPrincipal(new ClaimsIdentity());
    }
}

/// <summary>Użytkownik bieżącego zakresu wraz z jego własnym Vusos.Core.</summary>
public interface ICurrentUserCore
{
    string UserId { get; }
    string UserHash { get; }
    UserCore Core { get; }
    IServiceProvider Services { get; }
}

public sealed class CurrentUserCore(CircuitUser user, UserCoreFactory factory) : ICurrentUserCore
{
    public string UserId => user.RequireUserId();
    public string UserHash => UserIdentity.Hash(UserId);
    public UserCore Core => factory.GetOrCreate(UserId);
    public IServiceProvider Services => Core.Services;
}

/// <summary>Trzyma dane użytkownika w pamięci, dopóki jego obwód jest otwarty (blokuje zwolnienie przez sweeper).</summary>
public sealed class UserCoreCircuitHandler(CircuitUser user, UserCoreFactory factory) : CircuitHandler
{
    private string? _pinned;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (user.UserId is { } id)
        {
            factory.Pin(id);
            _pinned = id;
        }
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_pinned is { } id)
        {
            factory.Unpin(id);
            _pinned = null;
        }
        return Task.CompletedTask;
    }
}

/// <summary>IAuthSession nad zalogowanym użytkownikiem (cookie); logowanie i wylogowanie to przekierowania na /auth/*.</summary>
public sealed class HttpAuthSession(CircuitUser user, NavigationManager navigation) : Vusos.UI.Services.IAuthSession
{
    public bool IsSignedIn => user.IsSignedIn;
    public string? DisplayName => user.DisplayName;
    public string? Email => user.Email;

    // Stan sesji zmienia się tylko przez przeładowanie strony (cookie), więc zdarzenie nigdy nie jest wywoływane.
    public event Action? Changed { add { } remove { } }

    public Task SignInAsync()
    {
        navigation.NavigateTo("/auth/login?returnUrl=" + Uri.EscapeDataString(CurrentReturnUrl()), forceLoad: true);
        return Task.CompletedTask;
    }

    public Task SignOutAsync()
    {
        navigation.NavigateTo("/auth/logout", forceLoad: true);
        return Task.CompletedTask;
    }

    private string CurrentReturnUrl()
    {
        var uri = new Uri(navigation.Uri);
        var path = uri.AbsolutePath;
        if (path is "/logowanie" or "/dev-logowanie")
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return UserIdentity.SafeReturnUrl(query["returnUrl"]);
        }
        return UserIdentity.SafeReturnUrl(path + uri.Query);
    }
}
