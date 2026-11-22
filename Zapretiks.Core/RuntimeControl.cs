namespace Zapretiks.Core;

public sealed record ManagedZapretRun(string Directory, string StrategyFile);

public sealed record RuntimeControlResult(bool Succeeded, string Message);

/// <summary>Controls only the strategy started by this client instance.</summary>
public interface IZapretRuntimeController : IDisposable
{
    ManagedZapretRun? Current { get; }
    Task<RuntimeControlResult> StartAsync(ZapretInstallation installation, string strategyFile,
        CancellationToken cancellationToken = default);
    Task<RuntimeControlResult> SwitchAsync(ZapretInstallation installation, string strategyFile,
        CancellationToken cancellationToken = default);
    /// <summary>Restarts the active strategy identified by the instance returned from <see cref="Current"/>.</summary>
    Task<RuntimeControlResult> RestartAsync(ZapretInstallation installation, ManagedZapretRun run,
        CancellationToken cancellationToken = default);
    Task<RuntimeControlResult> StopAsync(CancellationToken cancellationToken = default);
}
