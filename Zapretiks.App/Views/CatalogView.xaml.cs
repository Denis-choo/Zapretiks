using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Zapretiks.App.Views;

public partial class CatalogView : UserControl
{
    private readonly KeyBinding findShortcut;
    private Window? shortcutOwner;

    public CatalogView()
    {
        InitializeComponent();
        findShortcut = new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control) { CommandTarget = this };
        Loaded += (_, _) =>
        {
            shortcutOwner = Window.GetWindow(this);
            shortcutOwner?.InputBindings.Add(findShortcut);
        };
        Unloaded += (_, _) =>
        {
            shortcutOwner?.InputBindings.Remove(findShortcut);
            shortcutOwner = null;
        };
    }

    private void OnFiltersReset(object sender, RoutedEventArgs e) => SearchField.Focus();

    private void CanFocusSearch(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = IsEnabled && IsHitTestVisible;

    private void FocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        SearchField.Focus();
        SearchField.SelectAll();
    }

    private void ClearSearch(object sender, RoutedEventArgs e)
    {
        SearchField.SetCurrentValue(TextBox.TextProperty, string.Empty);
        SearchField.Focus();
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
            DetailScroll?.ScrollToTop();
    }
}
