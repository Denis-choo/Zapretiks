using System.Windows.Input;
using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal sealed class ShellViewModel : ViewModel
{
    private readonly Session session;
    private readonly CatalogViewModel catalog;
    private readonly GamesViewModel games;
    private readonly (string Key, string Title, ViewModel Page)[] pages;
    private int currentPageIndex;
    private string? notice;
    private string noticeKind = "Information";
    private bool initialized;
    private bool isOnboarding;

    public ShellViewModel(Session session, ICatalogService catalogService, IProfileTransfer transfer,
        IConfigurationPreview preview, ISteamScanner scanner)
    {
        this.session = session;
        NavigateCommand = session.Command(parameter =>
        {
            if (parameter is string key)
                Navigate(key);
            return Task.CompletedTask;
        });
        ChooseFolderCommand = session.Command(async () =>
        {
            if (await session.ChooseFolderAsync())
                IsOnboarding = false;
        }, () => session.CanSave);
        SkipOnboardingCommand = session.Action(() => IsOnboarding = false);
        var showOnboarding = session.Action(() => IsOnboarding = true);
        var openCatalog = session.Action(() => Navigate("Catalog"));
        DismissNoticeCommand = session.Action(() => Notice = null);

        var library = new LibraryViewModel(session, transfer, preview, openCatalog);
        bool OpenLibraryForAction()
        {
            Navigate("Library");
            if (library.IsBrowsing)
                return true;
            session.Notify("Сохраните или отмените открытый черновик профиля.");
            return false;
        }
        var createProfile = session.Command(() =>
        {
            if (OpenLibraryForAction())
                library.CreateCommand.Execute(null);
            return Task.CompletedTask;
        }, () => session.CanSave);
        var openPreview = session.Command(() =>
        {
            if (OpenLibraryForAction())
                library.ReviewChangesCommand.Execute(null);
            return Task.CompletedTask;
        }, () => library.HasItems);
        catalog = new CatalogViewModel(session, catalogService, id =>
        {
            if (OpenLibraryForAction())
                library.SelectedItem = library.Items.FirstOrDefault(row =>
                    string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase));
        });
        var browseFromHome = session.Action(() =>
        {
            if (session.State.Settings.ZapretDirectory is null)
            {
                catalog.ResetFiltersCommand.Execute(null);
                catalog.SelectedItem = catalog.Items.FirstOrDefault(row => row.IsDemo) ?? catalog.Items.FirstOrDefault();
            }
            Navigate("Catalog");
        });
        var home = new HomeViewModel(session, browseFromHome, session.Action(() => Navigate("Library")),
            session.Action(() => Navigate("Settings")), createProfile, openPreview);
        games = new GamesViewModel(session, scanner);
        var settings = new SettingsViewModel(session, showOnboarding);

        pages =
        [
            ("Home", "Обзор", home),
            ("Catalog", "Каталог", catalog),
            ("Library", "Мои профили", library),
            ("Games", "Игры Steam", games),
            ("Settings", "Настройки", settings)
        ];

        session.Notification += (message, kind) =>
        {
            Set(ref noticeKind, kind.ToString(), nameof(NoticeKind));
            Notice = message;
        };
    }

    public ViewModel CurrentPage => pages[CurrentPageIndex].Page;
    public string PageTitle => pages[CurrentPageIndex].Title;
    public string SelectedPageKey => pages[CurrentPageIndex].Key;
    public int CurrentPageIndex => currentPageIndex;
    public bool IsOnboarding { get => isOnboarding; private set => Set(ref isOnboarding, value); }
    public bool CanNavigateWithWheel => initialized && !IsOnboarding;
    public string? Notice { get => notice; private set => Set(ref notice, value); }
    public string NoticeKind => noticeKind;
    public ICommand NavigateCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand SkipOnboardingCommand { get; }
    public ICommand DismissNoticeCommand { get; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(session.InitializeAsync(), catalog.LoadAsync());
        initialized = true;
        TryAutoScan();
    }

    public bool TryNavigateAdjacent(int direction) =>
        CanNavigateWithWheel && NavigateTo(CurrentPageIndex + direction);

    private void Navigate(string key) => NavigateTo(Array.FindIndex(pages, page => page.Key == key));

    private bool NavigateTo(int index)
    {
        if (index < 0 || index >= pages.Length || !Set(ref currentPageIndex, index, nameof(CurrentPageIndex)))
            return false;

        Raise(nameof(SelectedPageKey));
        Raise(nameof(CurrentPage));
        Raise(nameof(PageTitle));
        TryAutoScan();
        return true;
    }

    private void TryAutoScan()
    {
        if (initialized && SelectedPageKey == "Games" && session.State.Settings.AutoScanSteam)
            games.ScanWhenOpened();
    }
}
