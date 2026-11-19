using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed record ProfileRow(SolutionProfile Profile, bool IsInstalled, bool IsEnabled = false)
{
    public string Id => Profile.Id;
    public bool SharesLocalId { get; init; }
    public string Title => Profile.Name;
    public string Description => Profile.Description;
    public string Author => Profile.Author;
    public string Version => Profile.Version;
    public string CategoryLabel => Profile.Category == ProfileCategory.Games ? "Игры" : "Сервисы";
    public string SourceLabel => Profile.Source;
    public bool IsDemo => Profile.IsDemo;
    public string VerificationLabel => IsDemo ? "Демо" : Profile.IsVerified ? "Проверено" : "Без проверки";
    public string ExampleExplanation => "Учебный профиль. Его адреса исключены из расчёта.";
    public bool HasCompatibilityNotes => !IsDemo && !string.IsNullOrWhiteSpace(Profile.CompatibilityNotes);
    public string UpdatedLabel => Profile.UpdatedAt.ToString("d MMMM yyyy");
    public string FirstAddress => Profile.IncludeDomains.FirstOrDefault() ?? Profile.IncludeNetworks.FirstOrDefault()
        ?? Profile.ExcludeDomains.FirstOrDefault() ?? Profile.ExcludeNetworks.FirstOrDefault() ?? Description;
    public string ToggleLabel => IsEnabled ? "Исключить из расчёта" : "Включить в расчёт";
    public string StateLabel => IsDemo
        ? "Демонстрационный профиль"
        : IsEnabled ? "Учитывается в расчёте" : "Не участвует в расчёте";
    public IReadOnlyList<ProfileEntryGroup> EntryGroups
    {
        get
        {
            var sections = new[]
            {
                new ProfileEntryGroup("Домены", Profile.IncludeDomains),
                new ProfileEntryGroup("Исключения доменов", Profile.ExcludeDomains),
                new ProfileEntryGroup("IP и подсети", Profile.IncludeNetworks),
                new ProfileEntryGroup("Исключения IP", Profile.ExcludeNetworks),
                new ProfileEntryGroup("Совместимые стратегии", Profile.CompatibleStrategies)
            };
            return sections.Where(section => section.Values.Length > 0).ToArray();
        }
    }
}

internal sealed record ProfileEntryGroup(string Label, string[] Values)
{
    public string Lines => string.Join(Environment.NewLine, Values);
}
