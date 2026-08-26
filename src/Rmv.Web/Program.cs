using System.Security.Claims;
using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

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

    // Migrations and the boot record run here, in the background, rather than
    // blocking startup. See DatabaseInitializer for why.
    builder.Services.AddHostedService<DatabaseInitializer>();
}
else
{
    builder.Services.AddScoped<IDeploymentStore, NullDeploymentStore>();
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
    });
}

builder.Services.AddAuthorization();
builder.Services.AddSingleton(new SiteOptions(discordEnabled, databaseConfigured));

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
public record SiteOptions(bool DiscordEnabled, bool DatabaseConfigured);
