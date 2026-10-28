using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Vusos.Core.Platform;
using Vusos.UI.Services;
using Vusos.WebHost.Components;
using Vusos.WebHost.Dev;
using Vusos.WebHost.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// UI (Vusos.UI) + usługi hosta
builder.Services.AddVusosUi();
builder.Services.AddScoped<DevAuthSession>();
builder.Services.AddScoped<IAuthSession>(sp => sp.GetRequiredService<DevAuthSession>());
builder.Services.AddScoped<IExternalOpener, JsExternalOpener>();

if (builder.Configuration["Adapters:Mode"] == "fake")
{
    // Dane demonstracyjne w pamięci – bez dostępu do USOS/Platona.
    builder.Services.AddFakeVusosServices();
}
// else: orchestrator rejestruje tu prawdziwe Vusos.Core (AddVusosCore + ISecureStore/IConnectivity/IAppPaths + IConnectFlows).

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Pobrane materiały (otwieranie plików z dysku serwera w przeglądarce). Token wystawia JsExternalOpener.
app.MapGet("/dl/{token}", (string token, HttpContext http) =>
{
    var dp = http.RequestServices.GetRequiredService<IDataProtectionProvider>();
    var paths = http.RequestServices.GetRequiredService<IAppPaths>();
    string path;
    try
    {
        path = dp.CreateProtector(JsExternalOpener.Purpose).ToTimeLimitedDataProtector().Unprotect(token);
    }
    catch (Exception)
    {
        return Results.NotFound();
    }

    var root = Path.GetFullPath(paths.MaterialsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(path);
    if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) return Results.NotFound();

    new FileExtensionContentTypeProvider().TryGetContentType(full, out var contentType);
    return Results.File(full, contentType ?? "application/octet-stream", enableRangeProcessing: true);
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Vusos.UI.AppRouter).Assembly);

app.Run();

public partial class Program;
