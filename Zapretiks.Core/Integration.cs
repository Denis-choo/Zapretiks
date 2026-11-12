namespace Zapretiks.Core;

public sealed record ZapretStrategy(string FileName, string Name);

public sealed record ZapretInstallation(
    string Directory,
    bool IsValid,
    string? Version,
    IReadOnlyList<ZapretStrategy> Strategies,
    IReadOnlyList<string> Diagnostics)
{
    public bool HasServiceScript { get; init; }
    public bool HasLists { get; init; }
}

public sealed record ConfigurationFilePlan(
    string FileName,
    bool Exists,
    int ExistingCount,
    IReadOnlyList<string> Additions);

public sealed record ConfigurationPlan(
    IReadOnlyList<ConfigurationFilePlan> Files,
    IReadOnlyList<string> Warnings);

public sealed record SteamGame(uint AppId, string Name, string InstallPath);

public sealed record SteamScanResult(
    bool SteamFound,
    IReadOnlyList<SteamGame> Games,
    IReadOnlyList<string> Diagnostics);

public interface IZapretInspector
{
    Task<ZapretInstallation> InspectAsync(string directory, CancellationToken cancellationToken = default);
}

public interface IConfigurationPreview
{
    Task<ConfigurationPlan> CreateAsync(
        ZapretInstallation? installation,
        string? strategyFile,
        IEnumerable<InstalledProfile> profiles,
        CancellationToken cancellationToken = default);
}

public interface ISteamScanner
{
    Task<SteamScanResult> ScanAsync(CancellationToken cancellationToken = default);
}
