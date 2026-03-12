using Avalonia.Controls;
using ShopifySyncApp.ViewModels;

namespace ShopifySyncApp.Views;

public partial class MainWindow : Window
{
    private bool _eventsBound;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_eventsBound) return;
        _eventsBound = true;

        var tabControl = this.FindControl<TabControl>("MainTabs")!;
        tabControl.SelectionChanged += (sender, args) =>
        {
            if (args.Source != tabControl) return;
            if (DataContext is MainViewModel vm && tabControl.SelectedIndex == 1)
                vm.OnHistoryTabActivated();
        };
    }
}
