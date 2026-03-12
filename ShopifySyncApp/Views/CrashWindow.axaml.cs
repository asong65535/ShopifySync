using Avalonia.Controls;

namespace ShopifySyncApp.Views;

public partial class CrashWindow : Window
{
    public CrashWindow(Exception ex)
    {
        InitializeComponent();
        var tb = this.FindControl<TextBox>("ErrorText")!;
        tb.Text = $"{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}";
    }
}
