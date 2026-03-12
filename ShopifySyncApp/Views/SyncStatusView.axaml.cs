using Avalonia.Controls;

namespace ShopifySyncApp.Views;

public partial class SyncStatusView : UserControl
{
    public SyncStatusView()
    {
        InitializeComponent();

#if DEBUG
        var panel = this.FindControl<StackPanel>("RootPanel")!;
        var crashBtn = new Button
        {
            Content = "[DEBUG] Throw crash",
            Foreground = Avalonia.Media.Brushes.Red,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
        };
        crashBtn.Click += (_, _) => throw new Exception("Manual crash test from DEBUG button.");
        panel.Children.Add(crashBtn);
#endif
    }
}
