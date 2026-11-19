using System.IO;
using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed class ProfileEditorViewModel
{
    public const string LocalSource = "Создано в Zapretiks";
    private const int MaxListTextLength = 131072;
    private readonly string profileId;

    public ProfileEditorViewModel(SolutionProfile? original = null)
    {
        OriginalProfile = original;
        profileId = original?.Id ?? $"local-{Guid.NewGuid():N}";
        Name = original?.Name ?? "";
        Description = original?.Description ?? "";
        Author = original?.Author ?? "Пользователь";
        Version = original?.Version ?? "1.0.0";
        CategoryLabel = original?.Category == ProfileCategory.Games ? "Игры" : "Сервисы";
        IncludeDomainsText = original is null ? "" : JoinLines([.. original.IncludeDomains, .. original.IncludeNetworks]);
        ExcludeDomainsText = JoinLines(original?.ExcludeDomains);
        IncludeNetworksText = "";
        ExcludeNetworksText = JoinLines(original?.ExcludeNetworks);
        CompatibleStrategiesText = JoinLines(original?.CompatibleStrategies);
        CompatibilityNotes = original?.CompatibilityNotes ?? "";
    }

    public SolutionProfile? OriginalProfile { get; }
    public bool IsNew => OriginalProfile is null;
    public int ConvertedUrlCount { get; private set; }
    public string Title => IsNew ? "Новый профиль" : "Изменить профиль";
    public string Name { get; set; }
    public string Description { get; set; }
    public string Author { get; set; }
    public string Version { get; set; }
    public string CategoryLabel { get; set; }
    public string IncludeDomainsText { get; set; }
    public string ExcludeDomainsText { get; set; }
    public string IncludeNetworksText { get; set; }
    public string ExcludeNetworksText { get; set; }
    public string CompatibleStrategiesText { get; set; }
    public string CompatibilityNotes { get; set; }
    public IReadOnlyList<string> Categories { get; } = ["Сервисы", "Игры"];

    public static bool CanEdit(SolutionProfile profile) => !profile.IsVerified &&
        profile.Source is LocalSource or "Локальный импорт";

    public SolutionProfile Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("Введите название профиля.");

        var category = CategoryLabel switch
        {
            "Игры" => ProfileCategory.Games,
            "Сервисы" => ProfileCategory.Services,
            _ => throw new InvalidDataException("Выберите категорию «Игры» или «Сервисы».")
        };

        var includedAddresses = ReadAddresses(IncludeDomainsText, "Адреса");
        var excludedAddresses = ReadAddresses(ExcludeDomainsText, "Исключения доменов");
        var includeNetworks = includedAddresses.Networks.Concat(ReadLines(IncludeNetworksText, "IP-адреса и подсети")).ToArray();
        var excludeNetworks = excludedAddresses.Networks.Concat(ReadLines(ExcludeNetworksText, "Исключения IP")).ToArray();
        if (IsNew && includedAddresses.Domains.Length == 0 && includeNetworks.Length == 0
            && excludedAddresses.Domains.Length == 0 && excludeNetworks.Length == 0)
            throw new InvalidDataException("Добавьте хотя бы один адрес сайта или IP.");

        var profile = new SolutionProfile
        {
            SchemaVersion = OriginalProfile?.SchemaVersion ?? 1,
            Id = profileId,
            Name = Name,
            Description = string.IsNullOrWhiteSpace(Description) ? $"Локальный профиль «{Name.Trim()}»." : Description,
            Author = string.IsNullOrWhiteSpace(Author) ? "Пользователь" : Author,
            Version = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version,
            Source = OriginalProfile?.Source ?? LocalSource,
            Category = category,
            UpdatedAt = DateOnly.FromDateTime(DateTime.Now),
            IsVerified = false,
            IsDemo = OriginalProfile?.IsDemo ?? false,
            IncludeDomains = includedAddresses.Domains,
            ExcludeDomains = excludedAddresses.Domains,
            IncludeNetworks = includeNetworks,
            ExcludeNetworks = excludeNetworks,
            CompatibleStrategies = ReadLines(CompatibleStrategiesText, "Совместимые стратегии", 40),
            CompatibilityNotes = CompatibilityNotes
        };
        var normalized = ProfileValidation.Normalize(profile);
        ConvertedUrlCount = includedAddresses.UrlCount + excludedAddresses.UrlCount;
        return normalized;
    }

    private static string JoinLines(string[]? values) => values is null ? "" : string.Join(Environment.NewLine, values);

    private static string[] ReadLines(string text, string field, int limit = 512)
    {
        if (text is null || text.Length > MaxListTextLength)
            throw new InvalidDataException($"Список «{field}» слишком длинный. Допустимо до {limit} записей.");

        var entries = text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length > limit)
            throw new InvalidDataException($"В списке «{field}» допустимо до {limit} записей.");

        return entries;
    }

    private static AddressEntries ReadAddresses(string text, string field)
    {
        var values = ReadLines(text, field);
        var domains = new List<string>();
        var networks = new List<string>();
        var urlCount = 0;
        for (var index = 0; index < values.Length; index++)
        {
            var address = values[index];
            if (address.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                    || string.IsNullOrWhiteSpace(uri.Host))
                    throw AddressError(field, index);
                try
                {
                    address = uri.IdnHost.Trim('[', ']');
                }
                catch (UriFormatException)
                {
                    throw AddressError(field, index);
                }
                urlCount++;
            }

            if (ProfileValidation.NormalizeDomain(address) is { } domain)
                domains.Add(domain);
            else if (ProfileValidation.NormalizeNetwork(address) is { } network)
                networks.Add(network);
            else
                throw AddressError(field, index);
        }
        return new(domains.ToArray(), networks.ToArray(), urlCount);
    }

    private static InvalidDataException AddressError(string field, int index) =>
        new($"Проверьте строку {index + 1} в поле «{field}». Нужен домен, ссылка http(s) или IP-адрес.");

    private sealed record AddressEntries(string[] Domains, string[] Networks, int UrlCount);
}
