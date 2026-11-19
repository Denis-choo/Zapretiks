using System.Windows.Input;
using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed class CatalogViewModel : ViewModel
{
    private const string AllCategories = "Все";
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
    private IReadOnlyList<string> sources = [AllSources];

    public CatalogViewModel(Session session, ICatalogService catalog, Action<string> openInLibrary)
    {
        this.session = session;
        this.catalog = catalog;
        ResetFiltersCommand = session.Action(ResetFilters);
        InstallCommand = session.Command(ToggleInstallationAsync,
            parameter => session.CanSave && (parameter as ProfileRow ?? SelectedItem) is { SharesLocalId: false });
        OpenInLibraryCommand = session.Action(() =>
        {
            if (SelectedItem is { IsInstalled: true } row)
                openInLibrary(row.Id);
        });
        session.Changed += Refresh;
    }

    public IReadOnlyList<string> Categories { get; } = [AllCategories, "Игры", "Сервисы"];
    public IReadOnlyList<string> Sources => sources;
    public bool HasSourceFilter => Sources.Count > 2;
    public bool HasOnlyExamples => profiles.Count > 0 && profiles.All(profile => profile.IsDemo);
    public IReadOnlyList<ProfileRow> Items => items;
    public bool HasItems => Items.Count > 0;
    public bool HasFilters => SearchText.Length > 0 || SelectedCategory != AllCategories || SelectedSource != AllSources;
    public ProfileRow? SelectedItem
    {
        get => selectedItem;
        set { if (Set(ref selectedItem, value)) StatusMessage = null; }
    }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public ICommand ResetFiltersCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand OpenInLibraryCommand { get; }

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
            UpdateSources();
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
            if (current is not null && (!row.IsInstalled || current.Profile.Source != row.Profile.Source))
                throw new InvalidOperationException("Профиль с этим идентификатором уже есть в библиотеке.");
            return state with
            {
                Profiles = row.IsInstalled
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
        var rows = profiles.Select(profile =>
        {
            installed.TryGetValue(profile.Id, out var saved);
            return new ProfileRow(profile, saved is not null, saved?.IsEnabled == true)
            {
                SharesLocalId = saved is not null && saved.Profile.Source != profile.Source
            };
        });
        items = rows.Where(row => (SelectedCategory == AllCategories || row.CategoryLabel == SelectedCategory)
            && MatchesSource(row)
            && MatchesQuery(row.Profile, query)).ToArray();
        Raise(nameof(Items));
        Raise(nameof(HasItems));
        Raise(nameof(HasFilters));
        selectedItem = items.FirstOrDefault(row => row.Id == previousId) ?? items.FirstOrDefault();
        Raise(nameof(SelectedItem));
    }

    private static bool MatchesQuery(SolutionProfile profile, string query) =>
        query.Length == 0 ||
        profile.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        profile.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        profile.IncludeDomains.Concat(profile.ExcludeDomains).Concat(profile.IncludeNetworks).Concat(profile.ExcludeNetworks)
            .Any(address => address.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void UpdateSources()
    {
        var available = new List<string> { AllSources };
        if (profiles.Any(profile => profile.IsVerified && !profile.IsDemo))
            available.Add(Verified);
        if (profiles.Any(profile => !profile.IsVerified && !profile.IsDemo))
            available.Add(Community);
        if (profiles.Any(profile => profile.IsDemo))
            available.Add(Demo);
        sources = available;
        if (!sources.Contains(selectedSource))
            selectedSource = AllSources;

        Raise(nameof(Sources));
        Raise(nameof(SelectedSource));
        Raise(nameof(HasSourceFilter));
        Raise(nameof(HasOnlyExamples));
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
