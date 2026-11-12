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
        ChooseFolderCommand = session.ChooseFolderCommand;
        SkipOnboardingCommand = session.Command(() => session.UpdateSettingsAsync(settings =>
            settings with { OnboardingCompleted = true }));
        var showOnboarding = session.Command(() => session.UpdateSettingsAsync(settings =>
            settings with { OnboardingCompleted = false }));
        var openCatalog = session.Action(() => Navigate("Catalog"));
        DismissNoticeCommand = session.Action(() => Notice = null);

        var home = new HomeViewModel(session, openCatalog, session.Action(() => Navigate("Library")),
            session.Action(() => Navigate("Settings")));
        catalog = new CatalogViewModel(session, catalogService);
        var library = new LibraryViewModel(session, transfer, preview, openCatalog);
        games = new GamesViewModel(session, scanner);
        var settings = new SettingsViewModel(session, showOnboarding);

        pages =
        [
            ("Home", "Обзор", home),
            ("Catalog", "Каталог решений", catalog),
            ("Library", "Мои решения", library),
            ("Games", "Обнаруженные игры", games),
            ("Settings", "Настройки", settings)
        ];

        session.Changed += () => Raise(nameof(IsOnboarding));
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
    public bool IsOnboarding => initialized && session.CanSave && !session.State.Settings.OnboardingCompleted;
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
        Raise(nameof(IsOnboarding));
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
