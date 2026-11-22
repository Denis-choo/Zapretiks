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
    Task<RuntimeControlResult> StopAsync(CancellationToken cancellationToken = default);
}
