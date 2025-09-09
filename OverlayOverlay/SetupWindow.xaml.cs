using System;
using System.Windows;
using System.Windows.Controls;
using OverlayOverlay.Models;
using OverlayOverlay.Services;
using Microsoft.VisualBasic;
using System.IO;

namespace OverlayOverlay;

public partial class SetupWindow : Window
{
    private SetupContext _ctx;
    private bool _dirty;
    public SetupWindow()
    {
        InitializeComponent();
        this.Title = $"Setup - {ContextProvider.SessionName}";
        _ctx = ContextProvider.Load();
        CvBox.Text = _ctx.Cv;
        JdBox.Text = _ctx.JobDescription;
        ProjectBox.Text = _ctx.ProjectInfo;
        PersonalBox.Text = _ctx.PersonalProfile;
        DocIdBox.Text = _ctx.DocumentId;

        CvBox.TextChanged += OnChanged;
        JdBox.TextChanged += OnChanged;
        ProjectBox.TextChanged += OnChanged;
        PersonalBox.TextChanged += OnChanged;
        DocIdBox.TextChanged += OnChanged;

        _dirty = false;
        UpdateButtons();
    }

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        _ctx.Cv = CvBox.Text;
        _ctx.JobDescription = JdBox.Text;
        _ctx.ProjectInfo = ProjectBox.Text;
        _ctx.PersonalProfile = PersonalBox.Text;
        _ctx.DocumentId = DocIdBox.Text?.Trim() ?? string.Empty;
        _dirty = true;
        UpdateButtons();
    }

    private void ClearCv_Click(object sender, RoutedEventArgs e) { CvBox.Clear(); }
    private void ClearJd_Click(object sender, RoutedEventArgs e) { JdBox.Clear(); }
    private void ClearProject_Click(object sender, RoutedEventArgs e) { ProjectBox.Clear(); }
    private void ClearPersonal_Click(object sender, RoutedEventArgs e) { PersonalBox.Clear(); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSessionName()) return;
        ContextProvider.Save(_ctx);
        _dirty = false;
        UpdateButtons();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty)
        {
            if (!EnsureSessionName()) return;
            ContextProvider.Save(_ctx);
        }
        var main = new MainWindow();
        main.Show();
        this.Close();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty)
        {
            var res = MessageBox.Show("Save changes before leaving?", "Setup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (res == MessageBoxResult.Cancel) return;
            if (res == MessageBoxResult.Yes) { ContextProvider.Save(_ctx); _dirty = false; }
        }
        new SessionManagerWindow().Show();
        this.Close();
    }

    private void UpdateButtons()
    {
        // Save: only visible when there are unsaved changes
        BtnSave.IsEnabled = _dirty;
        BtnSave.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;

        // Next: only visible + enabled when required fields present
        bool hasCv = !string.IsNullOrWhiteSpace(CvBox.Text);
        bool hasJd = !string.IsNullOrWhiteSpace(JdBox.Text);
        bool ready = hasCv && hasJd;
        BtnNext.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.IsEnabled = ready;
    }

    private bool EnsureSessionName()
    {
        // If the session folder already exists, we have a name
        var currentPath = ContextProvider.GetSessionFolderPath();
        if (Directory.Exists(currentPath)) return true;

        // Ask for a name on first save
        var suggested = ContextProvider.SessionName;
        var input = Interaction.InputBox("Enter session name:", "Save Session", suggested);
        if (string.IsNullOrWhiteSpace(input)) return false;

        // Validate filename
        if (input.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("Invalid characters in name.");
            return false;
        }
        var parent = Path.GetDirectoryName(currentPath)!;
        var newPath = Path.Combine(parent, input.Trim());
        if (Directory.Exists(newPath))
        {
            MessageBox.Show("A session with that name already exists.");
            return false;
        }
        ContextProvider.SetSession(input.Trim());
        this.Title = $"Setup - {ContextProvider.SessionName}";
        return true;
    }
}


