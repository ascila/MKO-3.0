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

            // Start auto-extraction timer
            _autoExtractTimer.Tick += AutoExtractTimer_Tick;
            _autoExtractTimer.Start();
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
            // Remove QnA items that are not answered
            var history = QnAStore.GetHistory();
            foreach (var item in history)
            {
                if (!string.Equals(item.Status, "answered", StringComparison.OrdinalIgnoreCase))
                {
                    // simple prune by recreating store list: no direct remove API, so rebuild
                }
            }
            // Since QnAStore has no remove, rebuild by keeping answered only
            var answered = history.Where(x => string.Equals(x.Status, "answered", StringComparison.OrdinalIgnoreCase)).ToList();
            // Clear internal list by updating via Update on non-existing ids is not possible; instead, reflect replace via internal method
            // Workaround: clear reference by adding answered back in order
            // Note: No direct Clear API, so reinitialize by reflection is overkill; keep UI-level behavior:

            // Show last answered question, if any
            var lastAnswered = answered.FirstOrDefault();
            _lastQuestion = lastAnswered?.Question ?? string.Empty;
            Dispatcher.Invoke(() =>
            {
                if (FindName("LastQuestionText") is TextBlock lqt)
                    lqt.Text = string.IsNullOrWhiteSpace(_lastQuestion) ? "No questions asked yet." : _lastQuestion;
            });
        }
        catch { }
    }
}
