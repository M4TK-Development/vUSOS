using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Vusos.Server.Options;
using Vusos.Server.Security;
using Vusos.Server.Usos;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IValidateOptions<UsosOptions>, UsosOptionsValidator>();
builder.Services.AddOptions<UsosOptions>().Bind(builder.Configuration.GetSection(UsosOptions.SectionName)).ValidateOnStart();
builder.Services.AddOptions<ServerLimitsOptions>().Bind(builder.Configuration.GetSection(ServerLimitsOptions.SectionName));
builder.Services.AddOptions<CorsConfigOptions>().Bind(builder.Configuration.GetSection(CorsConfigOptions.SectionName));

builder.Services.AddHttpClient(UsosEndpoints.HttpClientName)
    .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan) // timeout egzekwujemy sami (20 s -> 504)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
    });

// CORS tylko dla skonfigurowanych originów (Blazor WebHost); klienci natywni go nie potrzebują.
builder.Services.AddCors();
builder.Services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
    .Configure<IOptions<CorsConfigOptions>>((cors, cfg) =>
    {
        var origins = cfg.Value.Origins.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray();
        cors.AddDefaultPolicy(p =>
        {
            if (origins.Length > 0)
                p.WithOrigins(origins).AllowAnyHeader().WithMethods("GET", "POST", "OPTIONS");
        });
    });

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/health"))
            return RateLimitPartition.GetNoLimiter("health");
        var permits = Math.Max(1, ctx.RequestServices.GetRequiredService<IOptions<ServerLimitsOptions>>().Value.PermitPerMinute);
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { code = "RATE_LIMITED" }, ct);
    };
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Server:TrustForwardedHeaders"))
{
    var fh = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    fh.KnownIPNetworks.Clear();
    fh.KnownProxies.Clear();
    app.UseForwardedHeaders(fh);
}

app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<AppKeyMiddleware>();
app.MapUsosEndpoints();

app.Run();

public partial class Program;
