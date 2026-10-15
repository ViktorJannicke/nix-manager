using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NixManager.Domain;
using NixManager.Execution;
using NixManager.Observation;
using NixManager.Operations;
using NixManager.Reviews;
using NixManager.Setup;
using NixManager.Software;
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
        services.TryAddSingleton<IReviewService>(provider => new ReviewService(provider.GetRequiredService<IOperationService>()));
        // The Software page's store: one per checkout, shared like the operation service.
        services.TryAddSingleton<ISoftwareService>(provider => new SoftwareService(config, provider.GetRequiredService<ICommandRunner>()));
        // A Window scope has one model context; a CommunityToolkit.Mvvm application without navigation registers it.
        services.TryAddScoped<IRunicModelContext, RunicModelContext>();
        services.TryAddScoped<OperationsViewModel>();
        // One observation cache for the process; Windows and operation events share its refreshes.
        services.TryAddSingleton(_ => new Observer(config));
        services.TryAddSingleton(provider => new BackgroundTasks(provider.GetRequiredService<IOperationService>(),
            clock: provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<SystemViewModel>();
        services.TryAddScoped<ReviewsViewModel>();
        services.TryAddScoped<SoftwareViewModel>();
        return services;
    }

    /// <summary>
    /// Registers the setup assistant, used before there is a checkout to manage: it runs its commands unprivileged,
    /// and with <paramref name="problem"/> only says why the dashboard could not start.
    /// </summary>
    public static IServiceCollection AddNixManagerSetup(this IServiceCollection services, SetupProblem? problem = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new SetupAssistantService(
            new ProcessCommandRunner(clock: provider.GetRequiredService<TimeProvider>()), problem: problem));
        services.TryAddScoped<IRunicModelContext, RunicModelContext>();
        services.TryAddScoped<SetupViewModel>();
        return services;
    }
}
