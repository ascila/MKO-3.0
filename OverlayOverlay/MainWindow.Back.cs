using System;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;
using OverlayOverlay.Services;
using OverlayOverlay.Models;
using System.Linq;
using System.Windows.Controls;

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

            // Start auto-extraction timer
            _autoExtractTimer.Tick += AutoExtractTimer_Tick;
            _autoExtractTimer.Start();

            // Initial render of Q&A history
            RefreshQnAHistoryUi();
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
                try { Dispatcher.Invoke(UpdateTranscriptUi); } catch { }
                AppendLog("Auto-extract: question detected");
                try
                {
                    var item = new QnA
                    {
                        Id = DateTime.UtcNow.Ticks,
                        Question = q,
                        Status = "pending",
                        Language = GetSelectedLanguageCode(),
                        Source = "auto",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    QnAStore.Add(item);
                    RefreshQnAHistoryUi();
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
            foreach (var item in QnAStore.GetHistory())
            {
                var exp = new Expander
                {
                    Header = $"Q{(item.QuestionNumber > 0 ? item.QuestionNumber : 0)}: {item.Question}",
                    IsExpanded = false,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var content = new StackPanel();
                var ans = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = string.IsNullOrWhiteSpace(item.Answer) ? "(pending)" : item.Answer };
                content.Children.Add(ans);
                exp.Content = content;
                host.Children.Add(exp);
            }
        }
        catch { }
    }
}
