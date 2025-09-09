using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace OverlayOverlay;

public partial class RenameDialog : Window
{
    private readonly HashSet<string> _existing;
    private readonly string _current;
    public string Value { get; private set; } = string.Empty;

    public RenameDialog(string currentName, IEnumerable<string> existingNames)
    {
        InitializeComponent();
        _current = currentName;
        _existing = new HashSet<string>(existingNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _existing.Remove(currentName);
        NameBox.Text = currentName;
        Loaded += (s, e) => { try { NameBox.Focus(); NameBox.SelectAll(); } catch { } };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BtnOk_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BtnCancel_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowError("Name cannot be empty."); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { ShowError("Name contains invalid characters."); return; }
        if (_existing.Contains(name)) { ShowError("A session with that name already exists."); return; }

        Value = name;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrText.Text = message;
        ErrText.Visibility = Visibility.Visible;
    }
}
