using Microsoft.Extensions.DependencyInjection;

namespace MugoByte.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddMugoBytePlatform(this IServiceCollection services, PlatformOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IPlatformLog>(_ => new FilePlatformLog(options.ProductDisplayName));
        services.AddSingleton<ISecureStore>(_ => new DpapiSecureStore(options.ProductDisplayName));
        services.AddSingleton<IdentityStore>();
        services.AddSingleton<ILicenseGuard, LicenseGuard>();

        if (options.UseMock)
        {
            services.AddSingleton<IPortalAuthClient, MockPortalAuthClient>();
            services.AddSingleton<IPortalLicenseClient, MockPortalLicenseClient>();
            services.AddSingleton<IPortalUpdateClient, MockPortalUpdateClient>();
        }
        else
        {
            services.AddSingleton(_ => PortalHttp.Create(options));
            services.AddSingleton<IPortalAuthClient, PortalAuthClient>();
            services.AddSingleton<IPortalLicenseClient, PortalLicenseClient>();
            services.AddSingleton<IPortalUpdateClient, PortalUpdateClient>();
        }

        services.AddSingleton<IActivationService, ActivationService>();
        services.AddSingleton<IUpdateFallback, NoOpUpdateFallback>();
        services.AddSingleton<PlatformSyncHost>();
        services.AddSingleton<IPlatformSync>(sp => sp.GetRequiredService<PlatformSyncHost>());
        return services;
    }
}

/// <summary>Default: no secondary update source.</summary>
public sealed class NoOpUpdateFallback : IUpdateFallback
{
    public Task<UpdateCheckResult?> TryCheckAsync(string currentVersion, CancellationToken ct = default) =>
        Task.FromResult<UpdateCheckResult?>(null);
}
