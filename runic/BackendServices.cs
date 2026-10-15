using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Operations;
using NixManager.Updates;
using NixManager.ViewModels;
using Runic.Navigation;
using Runic.Platform;
using Runic.Platform.Linux.Polkit;

namespace NixManager;

public static class BackendServices
{
    /// <summary>
    /// Registers the backend for a loaded configuration: one operation service for the process (the daemon of
    /// D025 shares it between Windows, the tray and forwarded command lines), the step runners with the Polkit
    /// action, and the ViewModels per Window scope.
    /// </summary>
    public static IServiceCollection AddNixManagerBackend(this IServiceCollection services, Config config)
    {
        services.TryAddSingleton(config);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDesktopPrivilegedActions>(_ => PolkitPrivilegedActions.Create(PrivilegedActions.All));
        services.TryAddSingleton<INetwork, HttpNetwork>();
        services.TryAddSingleton<IUpdateChecker>(provider => new UpdateChecker(provider.GetRequiredService<INetwork>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ICommandRunner>(provider => new PrivilegedCommandRunner(config,
            provider.GetRequiredService<IDesktopPrivilegedActions>(), PrivilegedActions.Privileged,
            new ProcessCommandRunner(clock: provider.GetRequiredService<TimeProvider>())));
        services.TryAddSingleton<IOperationService>(provider => new OperationService(config,
            provider.GetRequiredService<ICommandRunner>(), provider.GetRequiredService<IUpdateChecker>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IOperationClient>(provider => new LocalOperationClient(provider.GetRequiredService<IOperationService>()));
        // A Window scope has one model context; a CommunityToolkit.Mvvm application without navigation registers it.
        services.TryAddScoped<IRunicModelContext, RunicModelContext>();
        services.TryAddScoped<OperationsViewModel>();
        return services;
    }
}
