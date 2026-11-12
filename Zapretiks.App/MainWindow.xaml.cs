using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Zapretiks.App.ViewModels;

namespace Zapretiks.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PageArrival = TimeSpan.FromMilliseconds(230);
    private static readonly TimeSpan PageDeparture = TimeSpan.FromMilliseconds(145);
    private readonly WheelNavigationGesture wheel = new();
    private ContentControl activePage;
    private ShellViewModel? shell;
    private int presentedPage = -1;
    private int transitionVersion;

    public MainWindow()
    {
        InitializeComponent();
        PageHost.RenderTransform = new TranslateTransform();
        AlternatePageHost.RenderTransform = new TranslateTransform();
        activePage = PageHost;
        DataContextChanged += OnShellChanged;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, workArea.Width - 24));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, workArea.Height - 24));
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None
            || DataContext is not ShellViewModel { CanNavigateWithWheel: true } shell
            || !AllowsPageWheel(e.OriginalSource as DependencyObject))
        {
            wheel.ResetInput();
            return;
        }

        var now = Stopwatch.GetElapsedTime(0);
        var direction = wheel.ReadDirection(e.Delta, now);
        if (direction != 0 && shell.TryNavigateAdjacent(direction))
        {
            wheel.MarkNavigation(now);
            e.Handled = true;
        }
    }

    private bool AllowsPageWheel(DependencyObject? element)
    {
        for (var current = element; current is not null; current = GetParent(current))
        {
            if (current == NavigationBar || current == activePage)
                return true;

            // The wheel belongs to the scroll region even at its first or last row.
            if (current is ScrollViewer { ScrollableHeight: > 0, VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled }
                or ComboBox or TextBoxBase or Slider or ScrollBar)
                return false;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element) => element switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element),
        ContentElement content => ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent,
        _ => LogicalTreeHelper.GetParent(element)
    };

    private void OnShellChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (shell is not null)
            shell.PropertyChanged -= OnShellPropertyChanged;

        ++transitionVersion;
        ClearPage(PageHost);
        ClearPage(AlternatePageHost);
        presentedPage = -1;
        activePage = PageHost;
        shell = e.NewValue as ShellViewModel;
        if (shell is null)
            return;

        shell.PropertyChanged += OnShellPropertyChanged;
        PresentPage(shell);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage) && shell is not null)
            PresentPage(shell);
    }

    private void PresentPage(ShellViewModel model)
    {
        var previousIndex = presentedPage;
        presentedPage = model.CurrentPageIndex;
        var version = ++transitionVersion;
        var previousPage = activePage;
        var nextPage = previousPage == PageHost ? AlternatePageHost : PageHost;
        var moveFocus = previousPage.IsKeyboardFocusWithin;

        if (previousIndex < 0 || !SystemParameters.ClientAreaAnimation)
        {
            ClearPage(nextPage);
            ResetMotion(previousPage);
            ShowPage(previousPage, model);
            if (moveFocus)
                FocusSelectedTab();
            return;
        }

        ClearPage(nextPage);
        ShowPage(nextPage, model);
        activePage = nextPage;
        Panel.SetZIndex(previousPage, 0);
        previousPage.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(previousPage, KeyboardNavigationMode.None);

        // Keep the actual departing view alive so its scroll position stays visible through the transition.
        var direction = Math.Sign(presentedPage - previousIndex);
        var previousOffset = (TranslateTransform)previousPage.RenderTransform;
        previousOffset.BeginAnimation(TranslateTransform.XProperty,
            Animate(previousOffset.X, -26 * direction, PageDeparture, EasingMode.EaseIn));
        previousPage.BeginAnimation(OpacityProperty,
            Animate(previousPage.Opacity, 0, PageDeparture, EasingMode.EaseOut, FillBehavior.HoldEnd));

        var slide = Animate(38 * direction, 0, PageArrival, EasingMode.EaseOut);
        slide.Completed += (_, _) =>
        {
            if (transitionVersion == version)
                ClearPage(previousPage);
        };
        ((TranslateTransform)nextPage.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slide);
        nextPage.BeginAnimation(OpacityProperty, Animate(0, 1, PageArrival, EasingMode.EaseIn));
        if (moveFocus)
            FocusSelectedTab();
    }

    private static void ShowPage(ContentControl page, ShellViewModel model)
    {
        page.Content = model.CurrentPage;
        AutomationProperties.SetName(page, model.PageTitle);
        page.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(page, KeyboardNavigationMode.Continue);
        Panel.SetZIndex(page, 1);
        page.Visibility = Visibility.Visible;
    }

    private static void ClearPage(ContentControl page)
    {
        page.Visibility = Visibility.Collapsed;
        page.Content = null;
        ResetMotion(page);
    }

    private static void ResetMotion(ContentControl page)
    {
        ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
        page.BeginAnimation(OpacityProperty, null);
    }

    private static DoubleAnimation Animate(double from, double to, TimeSpan duration, EasingMode easingMode,
        FillBehavior fill = FillBehavior.Stop) => new(from, to, duration)
    {
        EasingFunction = new CubicEase { EasingMode = easingMode },
        FillBehavior = fill
    };

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void ToggleMaximized(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void FocusOnboarding(object sender, DependencyPropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsVisible)
                return;

            if (OnboardingOverlay.IsVisible)
                OnboardingChooseButton.Focus();
            else if ((bool)e.OldValue)
                FocusSelectedTab();
        });
    }

    private void FocusSelectedTab() => NavigationPanel.Children.OfType<Button>()
        .FirstOrDefault(button => Equals(button.CommandParameter, shell?.SelectedPageKey))?.Focus();

    protected override void OnClosed(EventArgs e)
    {
        if (shell is not null)
            shell.PropertyChanged -= OnShellPropertyChanged;
        ++transitionVersion;
        ClearPage(PageHost);
        ClearPage(AlternatePageHost);
        base.OnClosed(e);
    }
}
