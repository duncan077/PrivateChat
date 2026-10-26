using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatServer.Data;

/// <summary>
/// Design-time context used by <c>dotnet ef</c>.
///
/// The running app resolves its server version with <c>ServerVersion.AutoDetect</c>,
/// which opens a connection. Migrations are authored offline - on a dev machine or
/// in CI with no database - so this factory pins a version instead. Only the SQL
/// dialect used to emit the migration depends on it; the deployed app still detects
/// the real server at startup.
///
/// Point it at a real database with CHATFLOW_DESIGN_CONNECTION when scaffolding
/// from an existing schema.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    // MariaDB 11.4 LTS. Raise this only when the generated DDL needs newer syntax.
    private static readonly MariaDbServerVersion DesignTimeServerVersion = new(new Version(11, 4));

    private const string FallbackConnectionString =
        "Server=localhost;Port=3306;Database=chatflow;User Id=root;Password=;";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CHATFLOW_DESIGN_CONNECTION")
            ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, DesignTimeServerVersion)
            .Options;

        return new AppDbContext(options);
    }
}
