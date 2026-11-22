using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Zapretiks.Core;

public static class ProfileValidation
{
    private const string DisabledIpsetMarker = "203.0.113.113";

    public static SolutionProfile Normalize(SolutionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SchemaVersion != 1)
            throw new InvalidDataException($"Формат решения версии {profile.SchemaVersion} пока не поддерживается.");

        var id = ReadText(profile.Id, "Идентификатор", 80);
        if (!char.IsAsciiLetterOrDigit(id[0]) ||
            id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))
        {
            throw new InvalidDataException("Идентификатор должен начинаться с латинской буквы или цифры и может содержать точки, дефисы и подчёркивания.");
        }

        if (!Enum.IsDefined(profile.Category) || profile.UpdatedAt == default)
            throw new InvalidDataException("Укажите допустимую категорию и дату обновления решения.");

        var includes = NormalizeList(profile.IncludeNetworks, NormalizeNetwork, "IP-адреса");
        if (includes.Contains(DisabledIpsetMarker, StringComparer.Ordinal))
            throw new InvalidDataException("Адрес 203.0.113.113 зарезервирован Flowseal для режима IPSet «none».");

        return profile with
        {
            Id = id,
            Name = ReadText(profile.Name, "Название", 100),
            Description = ReadText(profile.Description, "Описание", 2000, multiline: true),
            Author = ReadText(profile.Author, "Автор", 100),
            Version = ReadText(profile.Version, "Версия", 40),
            Source = ReadText(profile.Source, "Источник", 100),
            CompatibilityNotes = ReadText(profile.CompatibilityNotes, "Совместимость", 1500, allowEmpty: true, multiline: true),
            IncludeDomains = NormalizeList(profile.IncludeDomains, NormalizeDomain, "Домены"),
            ExcludeDomains = NormalizeList(profile.ExcludeDomains, NormalizeDomain, "Исключения доменов"),
            IncludeNetworks = includes,
            ExcludeNetworks = NormalizeList(profile.ExcludeNetworks, NormalizeNetwork, "Исключения IP"),
            CompatibleStrategies = NormalizeList(profile.CompatibleStrategies, NormalizeStrategy, "Стратегии", 40),
            CoverImage = CoverImageData.Validate(profile.CoverImage),
            ReportUrl = NormalizeReportUrl(profile)
        };
    }

    private static string? NormalizeReportUrl(SolutionProfile profile)
    {
        if (profile.ReportUrl is null)
            return null;

        if (profile.IsDemo && !profile.IsVerified && profile.ReportUrl.Length <= 240 &&
            Uri.TryCreate(profile.ReportUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
        {
            var segments = uri.AbsolutePath.Split('/');
            if (segments.Length == 5 && segments[1].Length > 0 && segments[2].Length > 0 &&
                segments[3] is "issues" or "discussions" && segments[4].Length > 0 &&
                segments[4].All(char.IsAsciiDigit))
                return uri.AbsoluteUri;
        }

        throw new InvalidDataException("Для карточки на проверке укажите ссылку на issue или обсуждение GitHub.");
    }

    public static string? NormalizeDomain(string? input)
    {
        if (input is null || input.Length > 254 || input.Any(char.IsWhiteSpace))
            return null;

        try
        {
            var domain = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(input).ToLowerInvariant();
            if (domain.EndsWith('.'))
                domain = domain[..^1];

            var labels = domain.Split('.');
            if (domain.Length > 253 || labels.Length < 2 || labels[^1].All(char.IsAsciiDigit))
                return null;

            return labels.All(IsDomainLabel) ? domain : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string? NormalizeNetwork(string? input)
    {
        if (input is null || input.Length > 50 || input.Contains('%') || input.Any(char.IsWhiteSpace))
            return null;

        var parts = input.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
            return null;

        if (address.AddressFamily == AddressFamily.InterNetwork && !IsDottedDecimal(parts[0]))
            return null;

        var bytes = address.GetAddressBytes();
        var prefixLength = bytes.Length * 8;
        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength) ||
             prefixLength > bytes.Length * 8))
        {
            return null;
        }

        // Canonicalize hosts and equivalent CIDRs before comparing or merging them.
        if (prefixLength == bytes.Length * 8)
            return address.ToString();

        for (var index = 0; index < bytes.Length; index++)
        {
            var bits = Math.Clamp(prefixLength - index * 8, 0, 8);
            bytes[index] &= (byte)(255 << (8 - bits));
        }

        return $"{new IPAddress(bytes)}/{prefixLength.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool IsStrategyFileName(string? name) =>
        name is { Length: >= 11 and <= 100 } &&
        name.StartsWith("general", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) &&
        name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.' or '(' or ')');

    private static string? NormalizeStrategy(string? input) => IsStrategyFileName(input) ? input : null;

    private static bool IsDottedDecimal(string input)
    {
        var octets = input.Split('.');
        return octets.Length == 4 && octets.All(octet =>
            (octet.Length == 1 || !octet.StartsWith('0')) &&
            byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static bool IsDomainLabel(string label) =>
        label.Length is >= 1 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) &&
        char.IsAsciiLetterOrDigit(label[^1]) && label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static string ReadText(string? value, string field, int limit, bool allowEmpty = false, bool multiline = false)
    {
        if (value is null || value.Length > limit || value.Any(ch =>
            char.IsControl(ch) && !(multiline && ch is '\r' or '\n' or '\t')))
        {
            throw new InvalidDataException($"Проверьте поле «{field}». Допустимо до {limit} символов.");
        }

        value = value.Trim();
        if (!allowEmpty && value.Length == 0)
            throw new InvalidDataException($"Заполните поле «{field}».");

        return value;
    }

    private static string[] NormalizeList(string[]? values, Func<string?, string?> normalize, string field, int limit = 512)
    {
        if (values is null || values.Length > limit)
            throw new InvalidDataException($"В списке «{field}» допустимо до {limit} записей.");

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < values.Length; index++)
        {
            var normalized = normalize(values[index]);
            if (normalized is null)
                throw new InvalidDataException($"Некорректная запись №{index + 1} в списке «{field}».");

            result.Add(normalized);
        }

        return result.ToArray();
    }
}
