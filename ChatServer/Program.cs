using System.Text;
using ChatServer.Auth;
using ChatServer.Data;
using System.Data;
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

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureGlobalRoleColumnAsync(db);
}

app.UseCors();
app.UseForwardedHeaders();

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
    RequestPath = "/uploads"
});

app.MapControllers();
app.MapMagicOnionService();

app.Run();

static async Task EnsureGlobalRoleColumnAsync(AppDbContext db)
{
    await using var connection = db.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    var hasGlobalRole = false;
    await using (var pragma = connection.CreateCommand())
    {
        pragma.CommandText = "PRAGMA table_info('AspNetUsers');";
        await using var reader = await pragma.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader["name"]?.ToString();
            if (string.Equals(name, "GlobalRole", StringComparison.OrdinalIgnoreCase))
            {
                hasGlobalRole = true;
                break;
            }
        }
    }

    if (hasGlobalRole)
    {
        return;
    }

    await using var tx = await connection.BeginTransactionAsync();

    await using (var alter = connection.CreateCommand())
    {
        alter.Transaction = tx;
        alter.CommandText = "ALTER TABLE AspNetUsers ADD COLUMN GlobalRole INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync();
    }

    await using (var backfill = connection.CreateCommand())
    {
        backfill.Transaction = tx;
        backfill.CommandText = """
            UPDATE AspNetUsers
            SET GlobalRole = COALESCE(
                (SELECT MAX(Role) FROM ChatRoomMembers m WHERE m.UserId = AspNetUsers.Id),
                0
            );
            """;
        await backfill.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
}
