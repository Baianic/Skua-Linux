using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Backend.Linux.Services;

public static class LinuxServiceRegistration
{
    public static IServiceCollection AddLinuxServices(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<
            ISettingsService,
            LinuxSettingsService
        >();

        services.AddSingleton<
            IDispatcherService,
            LinuxDispatcherService
        >();

        services.AddSingleton<
            IDialogService,
            LinuxDialogService
        >();

        return services;
    }
}
