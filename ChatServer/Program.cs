using System.Text;
using ChatServer.Auth;
using ChatServer.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Allow file uploads up to 50 MB (matches UploadController's RequestSizeLimit)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024;
});

var dbConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

// Pinned, not AutoDetect. AddDbContext registers the options as Scoped, so this
// lambda runs once per DI scope - i.e. once per unary RPC - and AutoDetect opens a
// real MySqlConnection with Pooling=false and calls Open() synchronously. That put a
// blocking TCP connect + auth + teardown in front of every request, on a thread-pool
// thread, and it ran outside the execution strategy so EnableRetryOnFailure below
// never covered it.
//
// The version only selects which SQL dialect EF emits, and the migrations in
// Data/Migrations were authored against this same version (see AppDbContextFactory).
// Pinning keeps runtime and design time in agreement and makes startup independent
// of database reachability. Raise it when the server is upgraded and newer syntax is
// actually wanted.
var serverVersion = new MariaDbServerVersion(new Version(11, 4));

void ConfigureDb(DbContextOptionsBuilder options) =>
    options.UseMySql(dbConnectionString, serverVersion, mysql =>
        // A transient database blip on restart should recover, not crash-loop.
        mysql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null));

// The factory is the primary registration: it is a singleton, so the options graph
// is built exactly once for the process rather than once per scope.
//
// ChatHubImpl needs it because a StreamingHub is one long-lived DI scope - a scoped
// DbContext there would live for the whole connection, accumulating every entity it
// ever tracked. It creates a short-lived context per hub method instead.
builder.Services.AddDbContextFactory<AppDbContext>(ConfigureDb);

// The unary services are genuinely request-scoped, so they keep taking AppDbContext
// directly. Resolving it through the factory (rather than a second AddDbContext call)
// keeps one options instance in play and avoids two registrations racing to own
// DbContextOptions<AppDbContext>. The scope disposes what this lambda returns.
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

var redisConn = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConn))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConn;
        options.InstanceName = "PrivateChat:";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ChatServer.Services.JanusService>();
// Runs the videoroom codec sweep after the socket is bound rather than in front of it.
builder.Services.AddHostedService<ChatServer.Services.JanusRoomSyncService>();
builder.Services.AddMagicOnion();

// CORS for Blazor WebView (MAUI BlazorWebView runs from https://0.0.0.0)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// Trust reverse proxy forwarding headers (proto/host) for redirect correctness.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Migrate, not EnsureCreated: this applies schema changes to an existing database
// rather than only creating a missing one, which is what the old hand-written
// GlobalRole column patch was compensating for.
//
// Set Database:MigrateOnStartup to false to apply migrations out of band instead
// (dotnet ef migrations script > init.sql, then run it). That is the escape hatch
// when the provider's own migration machinery misbehaves against a given server -
// MySQL vs MariaDB differ here - and it avoids several instances racing to migrate.
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var migrationLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("DatabaseMigration");

    try
    {
        await db.Database.MigrateAsync();
        migrationLogger.LogInformation("Database schema is up to date.");
    }
    catch (Exception ex)
    {
        // Crash-looping under systemd buries the real error in restart spam.
        // Fail once, loudly, with the actionable next step.
        migrationLogger.LogCritical(
            ex,
            "Database migration failed. Apply the schema manually and set "
            + "Database:MigrateOnStartup=false in appsettings.json, or check that the "
            + "connection string points at a supported server.");
        throw;
    }
}

// First in the pipeline, before anything reads the scheme, host or client IP.
// Kestrel is reached over plain HTTP from the reverse proxy, so until these are
// applied every downstream component sees http:// and the proxy's own address.
app.UseForwardedHeaders();

app.UseCors();

// Enable HTTPS redirection outside Development.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

var uploadsDir = Path.Combine(app.Environment.ContentRootPath, "Uploads");
Directory.CreateDirectory(uploadsDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsDir),
    RequestPath = "/uploads",
    // Stored names are GUIDs (see UploadController), so a given URL's bytes never
    // change. Without an explicit max-age clients fall back to heuristic freshness
    // and revalidate recently uploaded files - exactly the ones being scrolled past.
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable"
});

app.MapControllers();
app.MapMagicOnionService();

app.Run();
