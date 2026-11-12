using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.RegularExpressions;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

public sealed class FlowsealConfigurationPreview : IConfigurationPreview
{
    private const int MaxListBytes = 16 * 1024 * 1024;
    private const string IpsetModeMarker = "203.0.113.113/32";
    private static readonly ListTarget[] Targets =
    [
        new("list-general-user.txt", "hostlist", profile => profile.IncludeDomains, ProfileValidation.NormalizeDomain),
        new("list-exclude-user.txt", "hostlist-exclude", profile => profile.ExcludeDomains, ProfileValidation.NormalizeDomain),
        new("ipset-all.txt", "ipset", profile => profile.IncludeNetworks, ProfileValidation.NormalizeNetwork),
        new("ipset-exclude-user.txt", "ipset-exclude", profile => profile.ExcludeNetworks, ProfileValidation.NormalizeNetwork)
    ];

    public Task<ConfigurationPlan> CreateAsync(ZapretInstallation? installation, string? strategyFile,
        IEnumerable<InstalledProfile> profiles, CancellationToken cancellationToken = default) =>
        Task.Run(() => BuildPlanAsync(installation, strategyFile, profiles, cancellationToken), cancellationToken);

    private static async Task<ConfigurationPlan> BuildPlanAsync(ZapretInstallation? installation, string? strategyFile,
        IEnumerable<InstalledProfile> profiles, CancellationToken cancellationToken)
    {
        if (installation is not { IsValid: true })
            return new([], ["Подключите папку Flowseal, чтобы сравнить профили со списками."]);

        var warnings = new List<string>();
        var enabled = profiles.Where(item => item.IsEnabled).Select(item => item.Profile).ToArray();
        var demoCount = enabled.Count(profile => profile.IsDemo);
        if (demoCount > 0)
            warnings.Add($"Демонстрационные профили исключены из плана ({demoCount}).");

        var strategy = installation.Strategies.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.FileName, strategyFile));
        var realProfiles = enabled.Where(profile => !profile.IsDemo).Select(ProfileValidation.Normalize).ToArray();
        var compatible = realProfiles.Where(profile => profile.CompatibleStrategies.Length == 0 ||
            profile.CompatibleStrategies.Contains(strategy?.FileName, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (compatible.Length < realProfiles.Length)
            warnings.Add($"Исключены профили, требующие другой стратегии ({realProfiles.Length - compatible.Length}).");

        string? strategyText = null;
        if (strategy is not null && ProfileValidation.IsStrategyFileName(strategy.FileName))
        {
            try
            {
                strategyText = await LocalFile.ReadTextAsync(Path.Combine(installation.Directory, strategy.FileName),
                    256 * 1024, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (LocalFile.IsReadError(exception))
            {
                warnings.Add("Файл выбранной стратегии недоступен. Используемые ею списки не проверены.");
            }
        }
        else
            warnings.Add("Выберите стратегию, чтобы проверить, какие списки она использует.");

        var listsPath = Path.Combine(installation.Directory, "lists");
        var results = new List<ListResult>(Targets.Length);
        foreach (var target in Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await PreviewListAsync(listsPath, target, compatible, strategyText, warnings, cancellationToken)
                .ConfigureAwait(false));
        }

        if (HasDomainConflict(results[0].Plan.Additions, results[1].Combined) ||
            HasDomainConflict(results[1].Plan.Additions, results[0].Combined))
        {
            warnings.Add("Есть пересечения доменов подключения и исключений. Добавления, полностью покрытые исключениями, пропущены.");
            results[0] = RemoveAdditions(results[0], results[0].Plan.Additions.Where(
                domain => ContainsDomainOrParent(results[1].Combined, domain)));
        }

        if (HasNetworkConflict(results[2].Plan.Additions, results[3].Combined) ||
            HasNetworkConflict(results[3].Plan.Additions, results[2].Combined))
        {
            warnings.Add("Есть пересечения IP-адресов и подсетей. Добавления, полностью покрытые исключениями, пропущены.");
            results[2] = RemoveAdditions(results[2], CoveredNetworks(results[2].Plan.Additions, results[3].Combined));
        }

        return new(results.Select(result => result.Plan).ToArray(), warnings);
    }

    private static async Task<ListResult> PreviewListAsync(string directory, ListTarget target, SolutionProfile[] profiles,
        string? strategyText, List<string> warnings, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, target.FileName);
        var exists = false;
        string? contents = null;
        try
        {
            contents = await LocalFile.ReadTextAsync(path, MaxListBytes, cancellationToken).ConfigureAwait(false);
            exists = true;
        }
        catch (FileNotFoundException) { }
        catch (Exception exception) when (LocalFile.IsReadError(exception))
        {
            warnings.Add($"Не удалось прочитать {target.FileName}. Для этого файла план не рассчитан.");
            return new(new(target.FileName, File.Exists(path), 0, []), []);
        }

        var lines = (contents ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
        var existing = lines.Select(target.Normalize).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = profiles.SelectMany(target.Entries).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(entry => !existing.Contains(entry)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (additions.Length > 0 && target.Option == "ipset" &&
            (lines.Length == 0 || contents!.Contains(IpsetModeMarker, StringComparison.Ordinal) ||
             existing.Contains("203.0.113.113")))
        {
            warnings.Add("Добавления IP пропущены: ipset-all.txt отсутствует, пуст или содержит маркер режима «none». Проверьте режим IPSet в Flowseal.");
            additions = [];
        }

        if (additions.Length > 0 && strategyText is not null && !ReferencesList(strategyText, target))
        {
            warnings.Add($"В выбранной стратегии не найдена ссылка на {target.FileName}. Добавления для него пропущены.");
            additions = [];
        }

        existing.UnionWith(additions);
        return new(new(target.FileName, exists, lines.Length, additions), existing);
    }

    private static bool ReferencesList(string strategyText, ListTarget target)
    {
        var option = Regex.Escape(target.Option);
        var file = Regex.Escape(target.FileName);
        var pattern = $"(?:^|[ \\t])--{option}=(?:\"(?:[^\"\\r\\n]*[\\\\/%])?{file}\"|(?:[^\"\\s]*[\\\\/%])?{file})(?=[ \\t\\r\\n]|$)";
        foreach (var line in strategyText.Split('\n'))
        {
            var text = line.TrimStart().TrimStart('@').TrimStart();
            if (text.StartsWith("::", StringComparison.Ordinal) || text.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("rem\t", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    private static ListResult RemoveAdditions(ListResult result, IEnumerable<string> removals)
    {
        var removed = removals.ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.Combined.ExceptWith(removed);
        return result with { Plan = result.Plan with { Additions = result.Plan.Additions.Where(item => !removed.Contains(item)).ToArray() } };
    }

    private static bool HasDomainConflict(IReadOnlyList<string> additions, HashSet<string> opposite)
    {
        if (additions.Count == 0)
            return false;

        var addedSet = additions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return additions.Any(domain => ContainsDomainOrParent(opposite, domain)) ||
            opposite.Any(domain => ContainsDomainOrParent(addedSet, domain));
    }

    private static bool ContainsDomainOrParent(HashSet<string> domains, string domain)
    {
        while (true)
        {
            if (domains.Contains(domain))
                return true;
            var separator = domain.IndexOf('.');
            if (separator < 0)
                return false;
            domain = domain[(separator + 1)..];
        }
    }

    private static IEnumerable<string> CoveredNetworks(IReadOnlyList<string> additions, IEnumerable<string> opposite)
    {
        var excluded = opposite.Select(NetworkRange.Parse).OrderBy(range => range.BitCount).ThenBy(range => range.Start).ToArray();
        var candidates = additions.Select(text => (Text: text, Range: NetworkRange.Parse(text)))
            .OrderBy(candidate => candidate.Range.BitCount).ThenBy(candidate => candidate.Range.Start);
        var index = 0;
        var previousBitCount = 0;
        var furthestEnd = BigInteger.MinusOne;
        foreach (var (text, range) in candidates)
        {
            if (range.BitCount != previousBitCount)
            {
                previousBitCount = range.BitCount;
                furthestEnd = BigInteger.MinusOne;
            }

            while (index < excluded.Length && excluded[index].BitCount < range.BitCount)
                index++;

            while (index < excluded.Length && excluded[index].BitCount == range.BitCount && excluded[index].Start <= range.Start)
                furthestEnd = BigInteger.Max(furthestEnd, excluded[index++].End);

            if (range.End <= furthestEnd)
                yield return text;
        }
    }

    private static bool HasNetworkConflict(IReadOnlyList<string> additions, IEnumerable<string> opposite)
    {
        if (additions.Count == 0)
            return false;

        var left = additions.Select(NetworkRange.Parse).OrderBy(range => range.BitCount).ThenBy(range => range.Start).ToArray();
        var right = opposite.Select(NetworkRange.Parse).OrderBy(range => range.BitCount).ThenBy(range => range.Start).ToArray();
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var added = left[leftIndex];
            var other = right[rightIndex];
            if (added.BitCount < other.BitCount || (added.BitCount == other.BitCount && added.End < other.Start))
                leftIndex++;
            else if (added.BitCount > other.BitCount || other.End < added.Start)
                rightIndex++;
            else
                return true;
        }

        return false;
    }

    private sealed record ListTarget(string FileName, string Option, Func<SolutionProfile, string[]> Entries,
        Func<string, string?> Normalize);

    private sealed record ListResult(ConfigurationFilePlan Plan, HashSet<string> Combined);

    private readonly record struct NetworkRange(int BitCount, BigInteger Start, BigInteger End)
    {
        public static NetworkRange Parse(string normalized)
        {
            var parts = normalized.Split('/');
            var bytes = IPAddress.Parse(parts[0]).GetAddressBytes();
            var bits = bytes.Length * 8;
            var prefix = parts.Length == 1 ? bits : int.Parse(parts[1], CultureInfo.InvariantCulture);
            var start = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            return new(bits, start, start + (BigInteger.One << (bits - prefix)) - BigInteger.One);
        }
    }
}
