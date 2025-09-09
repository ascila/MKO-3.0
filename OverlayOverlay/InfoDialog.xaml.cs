using System.Windows;

namespace OverlayOverlay;

public partial class InfoDialog : Window
{
    public InfoDialog(string message, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title)) this.Title = title!;
        MessageText.Text = message;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
        this.Close();
    }
}

