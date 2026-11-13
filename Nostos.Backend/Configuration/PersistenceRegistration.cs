using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Configuration;

/// <summary>
/// SelfHosted persistence composition. The public host uses local SQLite; the
/// private hosted executable supplies its own PostgreSQL context factory.
/// </summary>
public static class PersistenceRegistration
{
    public const string DatabasePathConfigurationKey = "Persistence:DatabasePath";

    public static IServiceCollection AddNostosPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        var dbPath = ResolveDatabasePath(
            configuration[DatabasePathConfigurationKey],
            contentRootPath);
        return services.AddNostosPersistence(dbPath);
    }

    public static IServiceCollection AddNostosPersistence(
        this IServiceCollection services,
        string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);

        services.AddDbContextFactory<NostosDbContext>(options =>
        {
            // SQLite creates the database file, but not a missing parent
            // directory. This matters for an explicitly configured data root
            // while preserving the historical content-root default.
            var parentDirectory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            options.UseSqlite(
                $"Data Source={fullPath}",
                sqlite => sqlite.MigrationsAssembly(
                    typeof(PersistenceRegistration).Assembly.FullName));
        });
        return services;
    }

    /// <summary>
    /// Resolves the SelfHosted SQLite path without changing ASP.NET's content
    /// root. An unset value preserves the historical <c>&lt;contentRoot&gt;/nostos.db</c>
    /// location; relative configured paths are also resolved from the content
    /// root, while absolute paths allow container deployments to use a dedicated
    /// persistent volume such as <c>/data/nostos.db</c>.
    /// </summary>
    public static string ResolveDatabasePath(string? configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Path.Combine(contentRootPath, "nostos.db"));
        }

        return Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(contentRootPath, configuredPath));
    }
}
