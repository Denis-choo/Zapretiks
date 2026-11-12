using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed record ProfileRow(SolutionProfile Profile, bool IsInstalled, bool IsEnabled = false)
{
    public string Id => Profile.Id;
    public string Title => Profile.Name;
    public string Description => Profile.Description;
    public string Author => Profile.Author;
    public string Version => Profile.Version;
    public string CategoryLabel => Profile.Category == ProfileCategory.Games ? "Игры" : "Сервисы";
    public string SourceLabel => Profile.Source;
    public bool IsDemo => Profile.IsDemo;
    public string VerificationLabel => IsDemo ? "Демо" : Profile.IsVerified ? "Проверено" : "Без проверки";
    public string UpdatedLabel => Profile.UpdatedAt.ToString("d MMMM yyyy");
    public string InstallLabel => IsInstalled ? "Убрать" : "Добавить";
    public string ToggleLabel => IsEnabled ? "Выключить" : "Включить";
    public string StateLabel => IsEnabled ? "Включён для подготовки" : "В библиотеке";
    public string CompatibilityLabel => string.IsNullOrWhiteSpace(Profile.CompatibilityNotes)
        ? "Дополнительные условия не указаны."
        : Profile.CompatibilityNotes;

    public string DetailsText
    {
        get
        {
            var sections = new[]
            {
                FormatList("Домены", Profile.IncludeDomains),
                FormatList("Исключения доменов", Profile.ExcludeDomains),
                FormatList("IP и подсети", Profile.IncludeNetworks),
                FormatList("Исключения IP", Profile.ExcludeNetworks),
                FormatList("Совместимые стратегии", Profile.CompatibleStrategies)
            };
            var details = string.Join(Environment.NewLine + Environment.NewLine, sections.OfType<string>());
            return details.Length > 0 ? details : "В профиле нет записей.";
        }
    }

    private static string? FormatList(string title, string[] values) =>
        values.Length == 0 ? null : $"{title}{Environment.NewLine}{string.Join(", ", values)}";
}
