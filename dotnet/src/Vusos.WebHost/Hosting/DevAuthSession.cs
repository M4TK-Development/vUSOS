using Microsoft.AspNetCore.Components;
using Vusos.UI.Services;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Prosta sesja deweloperska (zakres: obwód Blazor). „Logowanie M365” przekierowuje na /dev-logowanie z formularzem e-mail;
/// docelowo w WebHost zastąpi ją OIDC (Entra ID). Stan znika po odświeżeniu strony (nowy obwód).
/// </summary>
public sealed class DevAuthSession : IAuthSession
{
    private readonly NavigationManager _navigation;

    public DevAuthSession(NavigationManager navigation, IConfiguration configuration)
    {
        _navigation = navigation;
        if (configuration["Dev:AutoSignInEmail"] is { Length: > 0 } email) SetUser(email);
    }

    public bool IsSignedIn { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Email { get; private set; }
    public event Action? Changed;

    public Task SignInAsync()
    {
        _navigation.NavigateTo("/dev-logowanie");
        return Task.CompletedTask;
    }

    public void Complete(string email)
    {
        SetUser(email);
        Changed?.Invoke();
    }

    private void SetUser(string email)
    {
        Email = email.Trim();
        DisplayName = FormatName(Email);
        IsSignedIn = true;
    }

    public Task SignOutAsync()
    {
        IsSignedIn = false;
        DisplayName = null;
        Email = null;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private static string FormatName(string email)
    {
        var local = email.Split('@')[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]);
        var name = string.Join(' ', parts);
        return name.Length == 0 ? email : name;
    }
}
