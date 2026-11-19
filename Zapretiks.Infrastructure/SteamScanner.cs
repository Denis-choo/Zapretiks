using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

public sealed class SteamScanner(string? steamDirectory = null) : ISteamScanner
{
    private const uint FullyInstalled = 4;

    public Task<SteamScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => FindGamesAsync(cancellationToken), cancellationToken);

    private async Task<SteamScanResult> FindGamesAsync(CancellationToken cancellationToken)
    {
        var roots = steamDirectory is not null ? [steamDirectory] : GetWindowsSteamDirectories();
        var steamRoot = roots.Select(NormalizeLibraryPath).OfType<string>()
            .FirstOrDefault(path => Directory.Exists(Path.Combine(path, "steamapps")));
        if (steamRoot is null)
            return new(false, [], ["Установка Steam не найдена."]);

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var diagnostics = new List<string>();
        var librariesFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            var data = await LocalFile.ReadTextAsync(librariesFile, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var folders = SteamKeyValues.Parse(data).GetObject("libraryfolders")
                ?? throw new InvalidDataException("Раздел библиотек отсутствует.");
            foreach (var item in folders.Values)
            {
                if (!uint.TryParse(item.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    continue;

                var path = NormalizeLibraryPath(item.Value.Text ?? item.Value.GetObjectValue("path"));
                if (path is not null)
                    libraries.Add(path);
                else
                    diagnostics.Add("Пропущена библиотека с неподдерживаемым путём.");
            }
        }
        catch (FileNotFoundException)
        {
            diagnostics.Add("Файл дополнительных библиотек Steam отсутствует.");
        }
        catch (Exception exception) when (LocalFile.IsReadError(exception))
        {
            diagnostics.Add("Файл дополнительных библиотек Steam недоступен или повреждён.");
        }

        var games = new Dictionary<uint, SteamGame>();
        var unreadableManifests = 0;
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(library, "steamapps");
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var game = await ReadGameAsync(steamApps, manifest, cancellationToken).ConfigureAwait(false);
                        if (game is not null)
                            games.TryAdd(game.AppId, game);
                    }
                    catch (Exception exception) when (LocalFile.IsReadError(exception))
                    {
                        unreadableManifests++;
                    }
                }
            }
            catch (Exception exception) when (LocalFile.IsReadError(exception))
            {
                diagnostics.Add($"Библиотека Steam недоступна: {library}.");
            }
        }

        if (unreadableManifests > 0)
            diagnostics.Add($"Не удалось прочитать манифесты Steam ({unreadableManifests}).");

        return new(true, games.Values.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), diagnostics);
    }

    private static async Task<SteamGame?> ReadGameAsync(string steamApps, string manifest, CancellationToken cancellationToken)
    {
        var data = await LocalFile.ReadTextAsync(manifest, 256 * 1024, cancellationToken).ConfigureAwait(false);
        var state = SteamKeyValues.Parse(data).GetObject("AppState")
            ?? throw new InvalidDataException("В манифесте отсутствует AppState.");
        if (!uint.TryParse(state.GetText("appid"), NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ||
            appId == 0 ||
            !Path.GetFileNameWithoutExtension(manifest).Equals($"appmanifest_{appId}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Идентификатор Steam в манифесте не совпадает с именем файла.");
        }

        if (!uint.TryParse(state.GetText("StateFlags"), NumberStyles.None, CultureInfo.InvariantCulture, out var flags) ||
            (flags & FullyInstalled) == 0)
        {
            return null;
        }

        var name = state.GetText("name");
        var directory = state.GetText("installdir");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory) ||
            directory is "." or ".." || directory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            directory.Contains('/') || directory.Contains('\\') || directory.Contains(':'))
        {
            throw new InvalidDataException("Некорректное имя приложения или каталога Steam.");
        }

        var path = Path.Combine(steamApps, "common", directory);
        return Directory.Exists(path) ? new(appId, name, path) : null;
    }

    private static string? NormalizeLibraryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string?[] GetWindowsSteamDirectories() => OperatingSystem.IsWindows() ? ReadRegistry() : [];

    [SupportedOSPlatform("windows")]
    private static string?[] ReadRegistry() =>
    [
        GetRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
        GetRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        GetRegistryPath(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
    ];

    [SupportedOSPlatform("windows")]
    private static string? GetRegistryPath(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
