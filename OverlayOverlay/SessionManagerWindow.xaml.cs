using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OverlayOverlay.Services;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.VisualBasic;

namespace OverlayOverlay;

public partial class SessionManagerWindow : Window
{
    private record SessionRow(string Name, string Updated, string Path);
    private List<SessionRow> _allRows = new();
    private string _sortBy = "Updated"; // default desc by updated
    private bool _sortAsc = false;

    public SessionManagerWindow()
    {
        InitializeComponent();
        LoadSessions();
    }

    private string SessionsRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverlayOverlay", "Sessions");

    private void LoadSessions()
    {
        Directory.CreateDirectory(SessionsRoot);
        var rows = new List<SessionRow>();
        foreach (var dir in Directory.GetDirectories(SessionsRoot))
        {
            var name = System.IO.Path.GetFileName(dir);
            var ctx = System.IO.Path.Combine(dir, "SetupContext.json");
            var updated = File.Exists(ctx) ? File.GetLastWriteTime(ctx).ToString("yyyy-MM-dd HH:mm") : "-";
            rows.Add(new SessionRow(name, updated, dir));
        }
        _allRows = rows;
        ApplyFilterAndSort();
        BtnLoad.IsEnabled = BtnDelete.IsEnabled = false;
        // Empty state visibility
        if (FindName("EmptyText") is TextBlock empty)
            empty.Visibility = _allRows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        // Ensure something is selected when items exist
        if (SessionsList.Items.Count > 0 && SessionsList.SelectedIndex < 0)
            SessionsList.SelectedIndex = 0;
    }

    private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSel = SessionsList.SelectedItem != null;
        BtnLoad.IsEnabled = hasSel;
        BtnDelete.IsEnabled = hasSel;
        if (FindName("BtnRename") is Button br) br.IsEnabled = hasSel;
        if (FindName("BtnDuplicate") is Button bd) bd.IsEnabled = hasSel;
        if (FindName("BtnOpenFolder") is Button bof) bof.IsEnabled = hasSel;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var name = $"session-{stamp}";
        ContextProvider.SetSession(name);
        var setup = new SetupWindow();
        setup.Show();
        this.Close();
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is SessionRow row)
        {
            ContextProvider.SetSession(row.Name);
            var setup = new SetupWindow();
            setup.Show();
            this.Close();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is SessionRow row)
        {
            var currentIndex = SessionsList.SelectedIndex;
            if (MessageBox.Show($"Delete session '{row.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try { Directory.Delete(row.Path, true); } catch (Exception ex) { MessageBox.Show(ex.Message); }
                LoadSessions();
                if (SessionsList.Items.Count > 0)
                {
                    var newIndex = currentIndex;
                    if (newIndex >= SessionsList.Items.Count) newIndex = SessionsList.Items.Count - 1;
                    if (newIndex < 0) newIndex = 0;
                    SessionsList.SelectedIndex = newIndex;
                }
            }
        }
    }

    // Context menu creation
    private void SessionsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListView lv) return;
        var cm = new ContextMenu();
        void add(string header, RoutedEventHandler handler, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += handler;
            cm.Items.Add(mi);
        }
        bool hasSel = lv.SelectedItem != null;
        add("Load", (s, _) => Load_Click(s!, new RoutedEventArgs()), hasSel);
        add("Delete", (s, _) => Delete_Click(s!, new RoutedEventArgs()), hasSel);
        cm.Items.Add(new Separator());
        add("Rename", (s, _) => DoRename(), hasSel);
        add("Duplicate", (s, _) => DoDuplicate(), hasSel);
        cm.Items.Add(new Separator());
        add("Open Folder", (s, _) => DoOpenFolder(), hasSel);
        lv.ContextMenu = cm;
    }

    private void DoOpenFolder()
    {
        if (SessionsList.SelectedItem is SessionRow row)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", row.Path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }

    private void DoRename()
    {
        if (SessionsList.SelectedItem is SessionRow row)
        {
            var existing = _allRows.Select(r => r.Name);
            var dlg = new RenameDialog(row.Name, existing) { Owner = this };
            var ok = dlg.ShowDialog() == true;
            if (!ok) return;
            var newName = dlg.Value;
            if (string.IsNullOrWhiteSpace(newName) || newName == row.Name) return;
            var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(row.Path)!, newName);
            try { Directory.Move(row.Path, newPath); }
            catch (Exception ex) { MessageBox.Show(ex.Message); return; }
            LoadSessions();
            SelectByName(newName);
        }
    }

    private void DoDuplicate()
    {
        if (SessionsList.SelectedItem is SessionRow row)
        {
            string baseName = row.Name + "-copy";
            string parent = System.IO.Path.GetDirectoryName(row.Path)!;
            string candidate = System.IO.Path.Combine(parent, baseName);
            int idx = 2;
            while (Directory.Exists(candidate))
            {
                candidate = System.IO.Path.Combine(parent, baseName + idx);
                idx++;
            }
            try { CopyDirectory(row.Path, candidate); }
            catch (Exception ex) { MessageBox.Show(ex.Message); return; }
            LoadSessions();
            SelectByName(System.IO.Path.GetFileName(candidate));
        }
    }

    // Top bar button handlers
    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e) => DoOpenFolder();
    private void BtnRename_Click(object sender, RoutedEventArgs e) => DoRename();
    private void BtnDuplicate_Click(object sender, RoutedEventArgs e) => DoDuplicate();

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file));
            File.Copy(file, dest, true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir)));
        }
    }

    private void SelectByName(string name)
    {
        SessionsList.SelectedItem = _allRows.FirstOrDefault(r => r.Name == name);
    }

    // Search/filter + sorting
    // Removed search box: filter not needed

    private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.GridViewColumnHeader hdr && hdr.Column != null)
        {
            string key = hdr.Column == ColName ? "Name" : hdr.Column == ColUpdated ? "Updated" : "";
            if (string.IsNullOrEmpty(key)) return;
            if (_sortBy == key) _sortAsc = !_sortAsc; else { _sortBy = key; _sortAsc = true; }
            ApplyFilterAndSort();
        }
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<SessionRow> q = _allRows;

        Func<SessionRow, object> keySel = _sortBy == "Name"
            ? r => r.Name
            : r => DateTime.TryParse(r.Updated, out var dt) ? dt : DateTime.MinValue;

        q = _sortAsc ? q.OrderBy(keySel) : q.OrderByDescending(keySel);
        var list = q.ToList();
        SessionsList.ItemsSource = list;

        // Update headers with sort indicators
        if (ColName != null) ColName.Header = _sortBy == "Name" ? ($"Name {( _sortAsc ? '▲' : '▼')} ") : "Name";
        if (ColUpdated != null) ColUpdated.Header = _sortBy == "Updated" ? ($"Updated {( _sortAsc ? '▲' : '▼')} ") : "Updated";

        // Empty state
        if (FindName("EmptyText") is TextBlock empty)
            empty.Visibility = list.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Persist window prefs
    private record WinPrefs(double Width, double Height, string SortBy, bool SortAsc);

    private string PrefsPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverlayOverlay", "SessionManager.json");

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefsPath)!);
            if (File.Exists(PrefsPath))
            {
                var p = JsonSerializer.Deserialize<WinPrefs>(File.ReadAllText(PrefsPath));
                if (p != null)
                {
                    if (p.Width > 200) this.Width = p.Width;
                    if (p.Height > 200) this.Height = p.Height;
                    _sortBy = string.IsNullOrEmpty(p.SortBy) ? _sortBy : p.SortBy;
                    _sortAsc = p.SortAsc;
                }
            }
        }
        catch { /* ignore */ }
        ApplyFilterAndSort();
        if (SessionsList.Items.Count > 0 && SessionsList.SelectedIndex < 0)
            SessionsList.SelectedIndex = 0;
    }

    // Helper: keep at least one item selected when list has items
    private void EnsureSelection()
    {
        if (SessionsList.Items.Count > 0 && SessionsList.SelectedIndex < 0)
            SessionsList.SelectedIndex = 0;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var p = new WinPrefs(this.Width, this.Height, _sortBy, _sortAsc);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(p));
        }
        catch { /* ignore */ }
    }

    private void SessionsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SessionsList.SelectedItem != null)
            Load_Click(sender, e);
    }

    private void SessionsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && BtnLoad.IsEnabled)
        {
            Load_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Delete && BtnDelete.IsEnabled)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            if (e.Key == System.Windows.Input.Key.N)
            {
                New_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}




