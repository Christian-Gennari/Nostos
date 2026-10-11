using Microsoft.AspNetCore.DataProtection;

namespace Nostos.Backend.Configuration;

public static class DataProtectionRegistration
{
    public static IServiceCollection AddNostosSelfHostedDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        var dataProtection = services.AddDataProtection();
        var configuredPath = configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var keysPath = Path.GetFullPath(
                Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(contentRootPath, configuredPath));

            // The Docker image points this at its persistent /data volume.
            // Leave the default key ring intact for existing native installs.
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        return services;
    }
}
