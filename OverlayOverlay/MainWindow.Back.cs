using System;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;
using OverlayOverlay.Services;
using OverlayOverlay.Models;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;

namespace OverlayOverlay;

public partial class MainWindow
{
    private readonly DispatcherTimer _autoExtractTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _lastAutoExtractTranscriptLen = 0;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        try
        {
            // Hook enhanced Clear handler after original one
            var clearBtn = FindButtonByLabel("Clear Transcript");
            if (clearBtn != null)
            {
                clearBtn.Click += EnhancedClearTranscript_Click;
            }

            // Rewire Capture button to new flow
            if (FindName("BtnCapture") is Button capBtn)
            {
                try { capBtn.Click -= CaptureQuestion_Click; } catch { }
                capBtn.Click += Capture_RunFlow_Click;
            }

            // Auto-extraction disabled: only extract/respond on Capture
            try { _autoExtractTimer.Stop(); } catch { }

            // Initial render of Q&A history
            RefreshQnAHistoryUi();

            // Ensure overlay is capturable by default (user can re-enable hiding)
            try { ApplyDisplayAffinity(false); } catch { }

            // Wire capture-exclude toggle
            try
            {
                if (FindName("ExcludeFromCaptureToggle") is CheckBox exToggle)
                {
                    exToggle.IsChecked = false;
                    exToggle.Checked += ExcludeFromCaptureToggle_Changed;
                    exToggle.Unchecked += ExcludeFromCaptureToggle_Changed;
                }
            }
            catch { }
        }
        catch { }
        // Ensure initial section reflow after first layout
        try { AdjustSectionHeightsByWindow(); } catch { }
    }

    // Dynamically adjust Q&A panel height:
    // - Compact (160) when no cards expanded
    // - Grow to show the expanded card plus at least two collapsed cards,
    //   capped at 60% of the window height
    private void AdjustQnAPanelHeight()
    {
        try
        {
            if (FindName("QnAScroll") is not ScrollViewer sc) return;
            if (FindName("QnAHistoryItems") is not StackPanel host) return;

            // If there is no content, keep compact
            if (host.Children.Count == 0)
            {
                sc.Height = double.NaN; // stretch to row height
                return;
            }

            // Find expanded card (Border -> Expander)
            var pairs = host.Children.OfType<Border>()
                          .Select(b => new { Border = b, Expander = b.Child as Expander })
                          .ToList();
            var expanded = pairs.FirstOrDefault(p => (p.Expander?.IsExpanded ?? false));
            // Ensure layout is current so ActualHeight is valid
            host.UpdateLayout();

            // Use full row height; Expander row is already sized by layout logic

            if (expanded == null)
            {
                // Let the ScrollViewer fill its Grid row (60% assigned elsewhere)
                sc.Height = double.NaN;
                return;
            }

            // Expanded present: keep section at base target height and bring expanded into view
            int idxExp = pairs.FindIndex(p => ReferenceEquals(p, expanded));
            if (idxExp < 0) idxExp = 0;
            sc.Height = double.NaN; // fill row; just scroll to the expanded card
            try { expanded.Border.BringIntoView(); } catch { }
        }
        catch { }
    }

    private async void AutoExtractTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (!_asrOn) return;
            if (!string.IsNullOrWhiteSpace(_partialLine)) return; // wait for final segments
            var text = _transcript.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length < 20) return;
            if (text.Length <= _lastAutoExtractTranscriptLen) return;
            await TryAutoExtractQuestionAsync(text);
            _lastAutoExtractTranscriptLen = text.Length;
        }
        catch { }
    }

    private async void BackToSetup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_asrOn)
            {
                var dlg = new ConfirmDialog("A session is running. Do you want to exit and stop it?", "Confirm Exit")
                {
                    Owner = this
                };
                var ok = dlg.ShowDialog() == true;
                if (!ok) return;
                await SafeStopSessionAsync();
            }

            // Clear all in-memory session UI/state before leaving
            try { _autoExtractTimer.Stop(); } catch { }
            try { QnAStore.Clear(); } catch { }
            try { _transcript.Clear(); } catch { }
            _partialLine = string.Empty;
            _lastQuestion = string.Empty;
            try { RefreshQnAHistoryUi(); } catch { }
            try { if (FindName("LiveTranscriptBox") is TextBlock ltb) ltb.Text = "Waiting for audio..."; } catch { }
            try { if (FindName("LastQuestionText") is TextBlock lqt) lqt.Text = "No questions asked yet."; } catch { }

            var setup = new SetupWindow();
            setup.Show();
            this.Close();
        }
        catch (System.Exception ex)
        {
            // Fallback: log error
            try { AppendLog("Back to setup error: " + ex.Message); } catch { }
        }
    }

    private void ExcludeFromCaptureToggle_Changed(object? sender, RoutedEventArgs e)
    {
        try
        {
            bool exclude = (FindName("ExcludeFromCaptureToggle") as CheckBox)?.IsChecked == true;
            ApplyDisplayAffinity(exclude);
        }
        catch { }
    }

    private void ApplyDisplayAffinity(bool exclude)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            uint mode = exclude ? 0x11u : 0u; // 0x11 = ExcludeFromCapture; 0 = WDA_NONE
            SetWindowDisplayAffinity(hwnd, mode);
        }
        catch { }
    }

    private async Task SafeStopSessionAsync()
    {
        try { await _cloudTranscriber?.StopAsync()!; } catch { }
        try { _asrOn = false; } catch { }
        try { StopAudioMonitor(); } catch { }
        try { StopMicMonitor(); } catch { }
    }

    private async Task TryAutoExtractQuestionAsync(string fullTranscript)
    {
        try
        {
            string q = string.Empty;
            var keyOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(keyOpenAi))
            {
                _questionExtractor ??= new QuestionExtractor(keyOpenAi!);
                var res = await _questionExtractor.ExtractAsync(fullTranscript);
                q = res.isQuestion ? res.question : string.Empty;
            }
            else
            {
                q = FallbackExtractQuestion(fullTranscript);
            }

            if (!string.IsNullOrWhiteSpace(q) && !string.Equals(q, _lastQuestion, StringComparison.Ordinal))
            {
                _lastQuestion = q;
                AppendLog("Auto-extract: question detected");
                try
                {
                    var top = QnAStore.GetHistory().FirstOrDefault();
                    if (top == null || !string.Equals((top.Question ?? string.Empty).Trim(), q.Trim(), StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(top.Answer))
                    {
                        QnAStore.Add(new QnA
                        {
                            Id = DateTime.UtcNow.Ticks,
                            Question = q,
                            Status = "pending",
                            Language = GetSelectedLanguageCode(),
                            Source = "auto",
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        });
                        RefreshQnAHistoryUi();
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppendLog("Auto-extract error: " + ex.Message);
        }
    }

    private void EnhancedClearTranscript_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Remove unanswered questions from store
            QnAStore.RemoveWhere(q => !string.Equals(q.Status, "answered", StringComparison.OrdinalIgnoreCase));

            // Show last answered question (if any)
            var lastAnswered = QnAStore.GetHistory().FirstOrDefault();
            _lastQuestion = lastAnswered?.Question ?? string.Empty;
            Dispatcher.Invoke(() =>
            {
                if (FindName("LastQuestionText") is TextBlock lqt)
                    lqt.Text = string.IsNullOrWhiteSpace(_lastQuestion) ? "No questions asked yet." : _lastQuestion;
            });
            RefreshQnAHistoryUi();
        }
        catch { }
    }

    private void RefreshQnAHistoryUi()
    {
        try
        {
            if (FindName("QnAHistoryItems") is not StackPanel host) return;
            host.Children.Clear();
            var list = QnAStore.GetHistory().ToList();
            int idx = 1; // Start from Q1 at the top (newest first)
            bool expandedAnswered = false;
            foreach (var item in list)
            {
                var exp = new Expander
                {
                    IsExpanded = false,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                try
                {
                    if (Application.Current.Resources["QnACardExpanderStyle"] is Style expStyle)
                        exp.Style = expStyle;
                }
                catch { }

                // Header layout: [expand square] [title (wrap)] [regenerate square]
                // Tighter header spacing to make cards less bulky
                var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Expand square button (toggles expander)
                var expandBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(0),
                    ToolTip = "Expand/collapse",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var expIcon = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE70D", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                // \uE70D = ChevronDown; rotate when expanded
                var expRotate = new RotateTransform(0);
                expIcon.RenderTransformOrigin = new Point(0.5, 0.5);
                expIcon.RenderTransform = expRotate;
                expandBtn.Content = expIcon;
                expandBtn.Click += (_, __) => { try { exp.IsExpanded = !exp.IsExpanded; } catch { } };
                Grid.SetColumn(expandBtn, 0);
                headerGrid.Children.Add(expandBtn);

                // Question title (wrap, centered vertically)
                var questionHeader = new TextBlock
                {
                    Text = item.Question,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(questionHeader, 1);
                headerGrid.Children.Add(questionHeader);

                // Regenerate button: always present; enabled only if answered
                var regenBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(0),
                    ToolTip = "Regenerate answer",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsEnabled = !string.IsNullOrWhiteSpace(item.Answer)
                };
                regenBtn.Tag = item.Id;
                regenBtn.Click += RegenerateLatest_Click;
                var regenIcon = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE72C", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                regenBtn.Content = regenIcon;
                Grid.SetColumn(regenBtn, 2);
                headerGrid.Children.Add(regenBtn);

                // Bind icon rotation to expander state via events
                exp.Expanded += (_, __) =>
                {
                    try { expRotate.Angle = 180; } catch { }
                    // Collapse other expanders to ensure only one is expanded
                    try
                    {
                        if (FindName("QnAHistoryItems") is StackPanel host2)
                        {
                            foreach (var b in host2.Children.OfType<Border>())
                            {
                                if (b.Child is Expander other && !ReferenceEquals(other, exp))
                                    other.IsExpanded = false;
                            }
                        }
                    }
                    catch { }
                    try { exp.BringIntoView(); } catch { }
                    try { Dispatcher.BeginInvoke(new Action(AdjustQnAPanelHeight), DispatcherPriority.Background); } catch { }
                };
                exp.Collapsed += (_, __) =>
                {
                    try { expRotate.Angle = 0; } catch { }
                    try { Dispatcher.BeginInvoke(new Action(AdjustQnAPanelHeight), DispatcherPriority.Background); } catch { }
                };

                exp.Header = headerGrid;
                // Tidy default header site visuals
                exp.Loaded += (_, __) =>
                {
                    try
                    {
                        exp.ApplyTemplate();
                        if (exp.Template.FindName("HeaderSite", exp) is ToggleButton hb)
                        {
                            hb.Padding = new Thickness(0);
                            hb.Margin = new Thickness(0);
                            hb.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                            hb.BorderThickness = new Thickness(0);
                            hb.Background = Brushes.Transparent;
                        }
                    }
                    catch { }
                };

                // Content: answer inside a subtle bordered box + bottom actions
                var content = new StackPanel();
                var ansBox = new Border
                {
                    Background = (Brush)new BrushConverter().ConvertFromString("#1212120F")!,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E5E7EB")!,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var ans = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Text = string.IsNullOrWhiteSpace(item.Answer) ? "(pending)" : item.Answer };
                ansBox.Child = ans;
                content.Children.Add(ansBox);

                // Slightly reduce gap between answer box and bottom actions
                var bottomBar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                // Copy button left with label
                var copyBtn = new Button { Height = 28, Padding = new Thickness(8, 2, 8, 2), ToolTip = "Copy question and answer" };
                copyBtn.Tag = item.Id;
                copyBtn.Click += CopyQnA_Click;
                var copyStack = new StackPanel { Orientation = Orientation.Horizontal };
                var copyIcon = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE8C8", Margin = new Thickness(0,0,6,0), VerticalAlignment = VerticalAlignment.Center };
                copyStack.Children.Add(copyIcon);
                copyStack.Children.Add(new TextBlock { Text = "Copy", VerticalAlignment = VerticalAlignment.Center });
                copyBtn.Content = copyStack;
                DockPanel.SetDock(copyBtn, Dock.Left);
                bottomBar.Children.Add(copyBtn);
                content.Children.Add(bottomBar);
                exp.Content = content;

                // Card container around each expander
                var card = new Border
                {
                    Background = (Brush)new BrushConverter().ConvertFromString("#F3F4F6")!,
                    BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E5E7EB")!,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    // Tighter card padding (top/bottom) while preserving readability
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 6, 0, 0),
                    Child = exp
                };
                // Expand the most recent answered item
                if (!expandedAnswered && !string.IsNullOrWhiteSpace(item.Answer))
                {
                    exp.IsExpanded = true;
                    expandedAnswered = true;
                }

                host.Children.Add(card);
                idx++;
            }
            // After rendering list, adjust panel height once and log scroll metrics
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { AdjustQnAPanelHeight(); } catch { }
                    try { LogScrollMetrics("postBuild:QnA", FindName("QnAScroll") as ScrollViewer); } catch { }
                }), DispatcherPriority.Background);
            }
            catch { }
        }
        catch { }
    }

    private void CopyQnA_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not long id) return;
            var item = QnAStore.GetHistory().FirstOrDefault(x => x.Id == id);
            if (item == null) return;
            var answer = string.IsNullOrWhiteSpace(item.Answer) ? "(pending)" : item.Answer;
            var text = $"{item.Question}\n\n{answer}";
            Clipboard.SetText(text);
        }
        catch { }
    }

    private void CopyAllQuestions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var list = QnAStore.GetHistory().ToList();
            if (list.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            int idx = 1;
            foreach (var q in list)
            {
                sb.AppendLine($"Q{idx}: {q.Question}");
                idx++;
            }
            Clipboard.SetText(sb.ToString().Trim());
        }
        catch { }
    }

    private async void RegenerateLatest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not long id) return;
            var history = QnAStore.GetHistory();
            var item = history.FirstOrDefault(x => x.Id == id);
            if (item == null || string.IsNullOrWhiteSpace(item.Question)) return;

            // Clear old answer to indicate pending
            QnAStore.Update(id, q => { q.Answer = null; q.Status = "pending"; });
            RefreshQnAHistoryUi();

            // Gather context
            var ctx = ContextProvider.Get();
            string? imageDataUrl = null;
            string? clipboardText = null;
            try
            {
                var img = Clipboard.GetImage();
                if (img != null) imageDataUrl = BitmapSourceToDataUrl(img);
                if (string.IsNullOrWhiteSpace(imageDataUrl))
                {
                    var txt = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(txt)) clipboardText = txt;
                }
            }
            catch { }

            // Conversation history excluding this question
            var conv = Services.QnAStore.GetAnsweredPairs(10)
                        .Where(p => !string.Equals(p.question, item.Question, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

            var input = new AnswerInput
            {
                Question = item.Question,
                Language = GetSelectedLanguageCode(),
                Cv = ctx.Cv ?? string.Empty,
                JobDescription = ctx.JobDescription ?? string.Empty,
                PersonalProfile = ctx.PersonalProfile,
                ProjectInfo = ctx.ProjectInfo,
                ConversationHistory = conv.Length > 0 ? conv : null,
                IsFollowUp = false,
                ImageDataUrl = imageDataUrl,
                ClipboardText = clipboardText,
                DocumentId = string.IsNullOrWhiteSpace(ctx.DocumentId) ? null : ctx.DocumentId,
                QuestionNumber = item.QuestionNumber > 0 ? item.QuestionNumber : history.Count
            };

            var result = await AnswerGenerator.GenerateAsync(input);
            var answer = (result.Answer ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(answer))
            {
                QnAStore.Update(id, q => { q.Answer = answer; q.Status = "answered"; q.UpdatedAt = DateTime.UtcNow; });
            }
            else
            {
                QnAStore.Update(id, q => { q.Answer = "(no answer)"; q.Status = "answered"; q.UpdatedAt = DateTime.UtcNow; });
            }
            RefreshQnAHistoryUi();
        }
        catch { }
    }
}
