using System.Globalization;
using System.Text.RegularExpressions;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

public sealed partial class FlowsealInspector : IZapretInspector
{
    private static readonly string[] RequiredBinaries = ["winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll"];
    private static readonly string[] BaseLists = ["list-general.txt", "list-exclude.txt", "ipset-all.txt", "ipset-exclude.txt"];
    private static readonly StringComparer StrategyOrder = StringComparer.Create(
        CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public Task<ZapretInstallation> InspectAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.Run(() => InspectDirectoryAsync(directory, cancellationToken), cancellationToken);

    private static async Task<ZapretInstallation> InspectDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        try
        {
            var root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
                return new(root, false, null, [], ["Папка не найдена или недоступна."]);

            var strategies = Directory.EnumerateFiles(root, "general*.bat", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(ProfileValidation.IsStrategyFileName)
                .OrderBy(file => file.Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(file => file, StrategyOrder)
                .Select(file => new ZapretStrategy(file, DisplayName(file)))
                .ToArray();
            if (strategies.Length == 0)
                diagnostics.Add("В корне папки не найдены стратегии general*.bat.");

            var missingBinaries = RequiredBinaries.Where(file => !File.Exists(Path.Combine(root, "bin", file))).ToArray();
            if (missingBinaries.Length > 0)
                diagnostics.Add($"Не найдены компоненты в папке bin: {string.Join(", ", missingBinaries)}.");

            var listsPath = Path.Combine(root, "lists");
            var hasLists = Directory.Exists(listsPath);
            if (!hasLists)
                diagnostics.Add("Не найдена папка lists.");
            else
            {
                var missingLists = BaseLists.Where(file => !File.Exists(Path.Combine(listsPath, file))).ToArray();
                if (missingLists.Length > 0)
                    diagnostics.Add($"Не найдены основные списки: {string.Join(", ", missingLists)}.");
            }

            string? version = null;
            var servicePath = Path.Combine(root, "service.bat");
            var hasService = File.Exists(servicePath);
            if (hasService)
            {
                try
                {
                    var contents = await LocalFile.ReadTextAsync(servicePath, 256 * 1024, cancellationToken).ConfigureAwait(false);
                    var match = VersionDeclaration().Match(contents);
                    if (match.Success)
                        version = match.Groups[1].Value;
                }
                catch (Exception exception) when (LocalFile.IsReadError(exception))
                {
                    diagnostics.Add("Не удалось прочитать service.bat.");
                }
            }
            else
                diagnostics.Add("Не найден service.bat.");

            if (version is null)
                diagnostics.Add("Версию сборки определить не удалось.");

            return new(root, strategies.Length > 0 && missingBinaries.Length == 0 && hasLists,
                version, strategies, diagnostics)
            {
                HasServiceScript = hasService,
                HasLists = hasLists
            };
        }
        catch (Exception exception) when (LocalFile.IsReadError(exception) || exception is ArgumentException or NotSupportedException)
        {
            return new(directory, false, null, [], ["Не удалось прочитать выбранную папку."]);
        }
    }

    private static string DisplayName(string file)
    {
        var suffix = Path.GetFileNameWithoutExtension(file)["general".Length..].Trim();
        return suffix.Length == 0 ? "General" : suffix.Trim('(', ')');
    }

    [GeneratedRegex("^[ \\t]*@?set[ \\t]+\"?LOCAL_VERSION=([0-9][0-9a-z.-]{0,39})\"?[ \\t]*\\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionDeclaration();
}

internal static class LocalFile
{
    public static bool IsReadError(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidDataException;

    public static async Task<string> ReadTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
            throw new InvalidDataException("Файл превышает допустимый размер.");

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
