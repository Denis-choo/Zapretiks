using System.Windows;
using System.Windows.Controls;

namespace Zapretiks.App.Views;

public partial class CatalogView : UserControl
{
    public CatalogView() => InitializeComponent();

    private void OnFiltersReset(object sender, RoutedEventArgs e) => SearchField.Focus();

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
            DetailScroll?.ScrollToTop();
    }
}
