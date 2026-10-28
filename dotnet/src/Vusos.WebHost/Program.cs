using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Vusos.UI.Services;
using Vusos.WebHost.Components;
using Vusos.WebHost.Dev;
using Vusos.WebHost.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Tryby Adapters:Mode: "fake" (dane w pamięci, bez Core), "mock" (prawdziwy Core + adaptery mock; domyślny), "live" (USOS/Platon).
var adapterMode = (builder.Configuration["Adapters:Mode"] ?? "mock").ToLowerInvariant();
if (adapterMode is not ("fake" or "mock" or "live"))
    throw new InvalidOperationException($"Nieznany Adapters:Mode '{adapterMode}' (dozwolone: fake, mock, live).");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Klucze Data Protection (cookie sesji, tokeny /dl, szyfrowanie ISecureStore) w {Storage:Root}/keys – przetrwają restart.
var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddDataProtection()
    .SetApplicationName("vusos-webhost")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storage.RootFullPath, "keys")));

builder.Services.AddVusosAuth(builder.Configuration, builder.Environment);

// UI (Vusos.UI) + usługi hosta
builder.Services.AddVusosUi();
builder.Services.AddVusosWebHostCommon(builder.Configuration);

if (adapterMode == "fake")
{
    // Dane demonstracyjne w pamięci – bez dostępu do USOS/Platona (współdzielone przez wszystkich użytkowników).
    builder.Services.AddFakeVusosServices();
}
else
{
    builder.Services.AddVusosPerUserCore(builder.Configuration);
}

var server = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
if (server.UseForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (server.UseForwardedHeaders) app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment() && server.RedirectToHttps) app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseLoginRedirect(app.Configuration);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapVusosWebHost();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Vusos.UI.AppRouter).Assembly);

app.Run();

public partial class Program;
