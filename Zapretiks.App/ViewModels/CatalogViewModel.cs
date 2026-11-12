using System.Windows.Input;
using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed class CatalogViewModel : ViewModel
{
    private const string AllCategories = "Все категории";
    private const string AllSources = "Все публикации";
    private const string Verified = "Проверенные";
    private const string Community = "Сообщество";
    private const string Demo = "Демо";
    private readonly Session session;
    private readonly ICatalogService catalog;
    private IReadOnlyList<SolutionProfile> profiles = [];
    private IReadOnlyList<ProfileRow> items = [];
    private ProfileRow? selectedItem;
    private string searchText = string.Empty;
    private string selectedCategory = AllCategories;
    private string selectedSource = AllSources;
    private string? statusMessage;

    public CatalogViewModel(Session session, ICatalogService catalog)
    {
        this.session = session;
        this.catalog = catalog;
        ResetFiltersCommand = session.Action(ResetFilters);
        InstallCommand = session.Command(ToggleInstallationAsync,
            parameter => session.CanSave && (parameter is ProfileRow || SelectedItem is not null));
        session.Changed += Refresh;
    }

    public IReadOnlyList<string> Categories { get; } = [AllCategories, "Игры", "Сервисы"];
    public IReadOnlyList<string> Sources { get; } = [AllSources, Verified, Community, Demo];
    public IReadOnlyList<ProfileRow> Items => items;
    public bool HasItems => Items.Count > 0;
    public ProfileRow? SelectedItem { get => selectedItem; set => Set(ref selectedItem, value); }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public ICommand ResetFiltersCommand { get; }
    public ICommand InstallCommand { get; }

    public string SearchText
    {
        get => searchText;
        set { if (Set(ref searchText, value)) Refresh(); }
    }

    public string SelectedCategory
    {
        get => selectedCategory;
        set { if (Set(ref selectedCategory, value)) Refresh(); }
    }

    public string SelectedSource
    {
        get => selectedSource;
        set { if (Set(ref selectedSource, value)) Refresh(); }
    }

    public async Task LoadAsync()
    {
        try
        {
            profiles = await catalog.LoadAsync();
            Refresh();
        }
        catch (Exception error)
        {
            StatusMessage = "Не удалось загрузить локальный каталог.";
            session.Report(error);
        }
    }

    private async Task ToggleInstallationAsync(object? parameter)
    {
        var row = parameter as ProfileRow ?? SelectedItem;
        if (row is null)
            return;

        await session.UpdateAsync(state =>
        {
            var current = state.Profiles.FirstOrDefault(item => SameId(row, item.Profile));
            return state with
            {
                Profiles = current is not null
                    ? state.Profiles.Where(item => !SameId(row, item.Profile)).ToArray()
                    : [.. state.Profiles, new InstalledProfile(row.Profile, IsEnabled: false)]
            };
        });
        StatusMessage = row.IsInstalled ? $"«{row.Title}» удалён из библиотеки." : $"«{row.Title}» добавлен в библиотеку.";
    }

    private void Refresh()
    {
        var previousId = SelectedItem?.Id;
        var installed = session.State.Profiles.ToDictionary(item => item.Profile.Id, StringComparer.OrdinalIgnoreCase);
        var query = SearchText.Trim();
        var rows = profiles.Select(profile => new ProfileRow(profile,
            installed.ContainsKey(profile.Id), installed.GetValueOrDefault(profile.Id)?.IsEnabled == true)).ToArray();
        items = rows.Where(row => (SelectedCategory == AllCategories || row.CategoryLabel == SelectedCategory)
            && MatchesSource(row)
            && (row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        Raise(nameof(Items));
        Raise(nameof(HasItems));
        selectedItem = items.FirstOrDefault(row => row.Id == previousId) ?? items.FirstOrDefault();
        Raise(nameof(SelectedItem));
    }

    private bool MatchesSource(ProfileRow row) => SelectedSource switch
    {
        Verified => row.Profile.IsVerified && !row.IsDemo,
        Community => !row.IsDemo && !row.Profile.IsVerified,
        Demo => row.IsDemo,
        _ => true
    };

    private void ResetFilters()
    {
        Set(ref searchText, string.Empty, nameof(SearchText));
        Set(ref selectedCategory, AllCategories, nameof(SelectedCategory));
        Set(ref selectedSource, AllSources, nameof(SelectedSource));
        Refresh();
    }

    private static bool SameId(ProfileRow row, SolutionProfile profile) =>
        string.Equals(row.Id, profile.Id, StringComparison.OrdinalIgnoreCase);
}
