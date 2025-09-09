using System.Windows;

namespace OverlayOverlay;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title)) this.Title = title!;
        MessageText.Text = message;
    }

    private void BtnYes_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
        this.Close();
    }

    private void BtnNo_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        this.Close();
    }
}

