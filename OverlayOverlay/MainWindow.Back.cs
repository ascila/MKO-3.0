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
            bool regenAdded = false;
            foreach (var item in list)
            {
                var exp = new Expander
                {
                    IsExpanded = false,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // Header: Question text (bold, larger) + Regenerate on the right for latest answered
                // Use DockPanel with LastChildFill so the question fills and the button docks right
                var headerDock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
                if (!regenAdded && !string.IsNullOrWhiteSpace(item.Answer))
                {
                    var regenBtn = new Button
                    {
                        Width = 28,
                        Height = 28,
                        Margin = new Thickness(0),
                        ToolTip = "Regenerate answer",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    regenBtn.Tag = item.Id;
                    regenBtn.Click += RegenerateLatest_Click;
                    var icon = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE72C", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    regenBtn.Content = icon;
                    DockPanel.SetDock(regenBtn, Dock.Right);
                    headerDock.Children.Add(regenBtn);
                    regenAdded = true;
                }
                var questionHeader = new TextBlock
                {
                    Text = item.Question,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16
                };
                headerDock.Children.Add(questionHeader);
                exp.Header = headerDock;

                // Content: answer text and bottom actions
                var content = new StackPanel();
                var ans = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Text = string.IsNullOrWhiteSpace(item.Answer) ? "(pending)" : item.Answer };
                content.Children.Add(ans);

                var bottomBar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
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
                    Padding = new Thickness(12),
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
