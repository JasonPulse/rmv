using Microsoft.AspNetCore.DataProtection;
using Rmv.Web.Configuration;
using System.Security.Claims;
using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Rmv.Web.Analytics;
using Rmv.Web.Tools;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Secrets mounted as files
//
// Docker and Swarm deliver secrets as files under /run/secrets, one per secret,
// named after the config key with "__" for the separator. So a secret called
// ConnectionStrings__Postgres becomes ConnectionStrings:Postgres here, with the
// credential never appearing in `docker inspect`, in compose, or in the
// environment of the process.
//
// Registered last, so a non-empty mounted secret wins over the same key set as
// an environment variable. Does nothing when the directory is absent, which is
// the case in development.
//
// Not AddKeyPerFile: that treats a 0-byte secret as an empty value, and since
// this provider is last it wins, so an empty key in a Kubernetes Secret silently
// masks a good environment variable. Empty files are skipped here instead.
// ---------------------------------------------------------------------------
builder.Configuration.AddSecretsDirectory("/run/secrets");

builder.Services.AddRazorPages(o =>
{
    // /status shows build sha, hostname and boot count. Fine for an operator,
    // not for visitors. Open in Development so it is usable before Discord is
    // wired up; sign-in required everywhere else.
    if (!builder.Environment.IsDevelopment())
    {
        o.Conventions.AuthorizePage("/Status", AdminPolicy.Name);
        o.Conventions.AuthorizeFolder("/Admin", AdminPolicy.Name);
    }
});

// ---------------------------------------------------------------------------
// Postgres
//
// Optional on purpose. With no connection string the site still runs: pages
// render, and anything that needs the database says so. That keeps a first
// `dotnet run` working before any infrastructure exists, and keeps a Postgres
// restart from taking the site down with it.
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Postgres");
var databaseConfigured = !string.IsNullOrWhiteSpace(connectionString);

builder.Services.AddSingleton(new DatabaseState(
    databaseConfigured ? DatabaseStatus.Starting : DatabaseStatus.NotConfigured));

if (databaseConfigured)
{
    builder.Services.AddDbContext<RmvDbContext>(o => o
        .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

    builder.Services.AddScoped<IDeploymentStore, PostgresDeploymentStore>();

    // Keep the Data Protection key ring in Postgres. Without it every process
    // mints its own keys, so a sign-in cookie stops validating on redeploy and
    // never validates across replicas. SetApplicationName has to match on every
    // instance or they derive different keys from the same ring.
    builder.Services.AddDataProtection()
        .PersistKeysToDbContext<RmvDbContext>()
        .SetApplicationName("results-may-vary");

    // Migrations and the boot record run here, in the background, rather than
    // blocking startup. See DatabaseInitializer for why.
    builder.Services.AddHostedService<DatabaseInitializer>();

    // Buffers request records and flushes them in batches. Registered as a
    // singleton as well so the middleware can resolve the same instance.
    builder.Services.AddSingleton<RequestLogWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RequestLogWriter>());
}
else
{
    builder.Services.AddScoped<IDeploymentStore, NullDeploymentStore>();
    // No database, so no shared key ring. Keys stay in memory, which is fine
    // for a single local instance and is why sign-in needs a database in
    // production.
}

// ---------------------------------------------------------------------------
// Discord sign-in
//
// Wired only when credentials are present, so the site runs before the Discord
// application exists. Set Discord:ClientId and Discord:ClientSecret to switch
// it on. IsDiscordEnabled drives whether the login link renders.
// ---------------------------------------------------------------------------
var discordClientId = builder.Configuration["Discord:ClientId"];
var discordClientSecret = builder.Configuration["Discord:ClientSecret"];
var discordEnabled = !string.IsNullOrWhiteSpace(discordClientId)
                     && !string.IsNullOrWhiteSpace(discordClientSecret);

// Cookie auth is the app's own scheme and is always registered: [Authorize]
// needs one to exist, or protected pages throw instead of redirecting. Discord
// is layered on top only when it is configured.
var auth = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "rmv.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax; // Lax, not Strict: the OAuth return is a cross-site GET.
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/Denied";
    });

if (discordEnabled)
{
    auth.AddDiscord(o =>
    {
        o.ClientId = discordClientId!;
        o.ClientSecret = discordClientSecret!;
        o.CallbackPath = "/signin-discord";
        // "identify" is enough for a name and avatar. Add "guilds" only when
        // there is a feature that actually gates on guild membership.
        o.Scope.Clear();
        o.Scope.Add("identify");
        o.SaveTokens = false; // Nothing calls the Discord API on the member's behalf yet.

        // The avatar hash, taken straight off the OAuth payload. Doing it here
        // rather than through a claim-mapping helper keeps the null handling
        // explicit and avoids depending on which namespace that helper lives in.
        o.Events.OnCreatingTicket = context =>
        {
            if (context.User.TryGetProperty("avatar", out var avatar)
                && avatar.ValueKind == System.Text.Json.JsonValueKind.String
                && avatar.GetString() is { Length: > 0 } hash)
            {
                context.Identity?.AddClaim(new Claim(DiscordUser.AvatarClaim, hash));
            }

            return Task.CompletedTask;
        };
    });
}

// Discord sign-in only proves someone has a Discord account. Editing the site
// requires being on this list.
var adminIds = AdminPolicy.Parse(builder.Configuration["Admin:DiscordIds"]);
builder.Services.AddAuthorization(o => AdminPolicy.Configure(o, adminIds));

// ---------------------------------------------------------------------------
// Rate limiting
//
// Only the upload endpoints need it. They are the one place an anonymous
// visitor can make the server do work proportional to what they send. Keyed on
// the forwarded client address, which is why UseForwardedHeaders has to run
// before UseRateLimiter or every visitor shares cloudflared's pod address.
// ---------------------------------------------------------------------------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    o.AddPolicy(RateLimitPolicies.Upload, http => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
builder.Services.AddSingleton(new SiteOptions(discordEnabled, databaseConfigured, adminIds.Length > 0));

// ---------------------------------------------------------------------------
// Health
//
// /healthz/live answers as soon as the process is up; /healthz/ready also
// requires Postgres. Docker's healthcheck uses ready so the container is not
// marked healthy while the database is still starting.
// ---------------------------------------------------------------------------
// Reads DatabaseState rather than probing the DbContext, so "migrations have
// not finished yet" reports unready instead of healthy-but-broken. With no
// database configured there is nothing to be unready about, so it passes.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

// ---------------------------------------------------------------------------
// Proxy headers
//
// Requests arrive from cloudflared over plain HTTP inside the compose network.
// Without this, ASP.NET Core sees scheme=http and the container hostname, and
// builds an OAuth redirect_uri Discord will reject.
//
// KnownIPNetworks and KnownProxies are cleared because cloudflared's address
// inside the Docker
// network is not stable. That means X-Forwarded-* is trusted from anything that
// can reach the app, which is safe only because the app port is not published
// to the internet. If you ever expose it directly, pin the proxy address here.
// ---------------------------------------------------------------------------
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
                       | ForwardedHeaders.XForwardedProto
                       | ForwardedHeaders.XForwardedHost,
    ForwardLimit = 2,
};
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Without this a missing page returns a blank browser 404, which reads as a
// broken site rather than a wrong URL.
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.UseStaticFiles();
app.UseRouting();

// After UseForwardedHeaders, so the limiter partitions on the real client
// address rather than on cloudflared's.
app.UseRateLimiter();

// Records every request that reaches routing, including 404s. Only registered
// when there is a database to write to.
if (databaseConfigured)
{
    app.UseMiddleware<RequestLogMiddleware>();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapHealthChecks("/healthz/live", new() { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new() { Predicate = c => c.Tags.Contains("ready") });

app.Run();

/// <summary>
/// Site-wide facts the views need. Injected rather than read from configuration
/// in the view, so the "is Discord wired up" test lives in exactly one place.
/// </summary>
public record SiteOptions(bool DiscordEnabled, bool DatabaseConfigured, bool AdminsConfigured);
