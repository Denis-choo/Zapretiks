using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Zapretiks.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e) => DetailScroll?.ScrollToTop();

    private void OnEditorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && e.OldValue is true && IsVisible)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                DetailScroll.ScrollToTop();
                ProfilesList.Focus();
                if (ProfilesList.SelectedItem is { } selected)
                    ProfilesList.ScrollIntoView(selected);
            });
    }
}
