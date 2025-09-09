using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using OverlayOverlay.Services;
using OverlayOverlay.Models;

namespace OverlayOverlay;

public partial class MainWindow
{
    private bool _captureRunning;

    private async void Capture_RunFlow_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _captureRunning = true;
            try { _autoExtractTimer.Stop(); } catch { }
            var ctx = ContextProvider.Get();
            var transcript = _transcript.ToString();
            if (string.IsNullOrWhiteSpace(ctx.Cv) || string.IsNullOrWhiteSpace(ctx.JobDescription)) return;

            // Decide question to answer: prefer latest pending from history; fallback to extracting from transcript
            var latestPending = QnAStore.GetHistory().FirstOrDefault(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase));
            string questionToAnswer = latestPending?.Question ?? string.Empty;
            if (string.IsNullOrWhiteSpace(questionToAnswer))
            {
                if (string.IsNullOrWhiteSpace(transcript)) return;
                // Extract from transcript if no pending exists
                try
                {
                    var keyOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    _questionExtractor ??= new QuestionExtractor(keyOpenAi ?? string.Empty);
                    AppendLog("AI: extracting interview question (no pending found)...");
                    var res0 = await _questionExtractor.ExtractAsync(transcript);
                    if (res0.isQuestion) questionToAnswer = (res0.question ?? string.Empty).Trim();
                }
                catch { }
                if (string.IsNullOrWhiteSpace(questionToAnswer)) return;
                // Insert as pending so UI shows it
                var id0 = DateTime.UtcNow.Ticks;
                var tmp = new QnA { Id = id0, Question = questionToAnswer, Status = "pending", Language = GetSelectedLanguageCode(), Source = "capture", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                QnAStore.Add(tmp);
                latestPending = tmp;
                RefreshQnAHistoryUi();
            }

            // Per spec: when pressing Capture, clear other unanswered questions and the transcript
            try
            {
                var keepId = latestPending?.Id ?? -1;
                QnAStore.RemoveWhere(q => !string.IsNullOrWhiteSpace(q.Question) && !string.Equals(q.Status, "answered", StringComparison.OrdinalIgnoreCase) && q.Id != keepId);
                RefreshQnAHistoryUi();
            }
            catch { }
            try
            {
                _transcript.Clear();
                _partialLine = string.Empty;
                if (FindName("LiveTranscriptBox") is TextBlock ltb)
                    ltb.Text = _asrOn ? "Listening..." : "Waiting for audio...";
            }
            catch { }

            // UI: disable and show spinner
            try
            {
                if (FindName("BtnCapture") is Button capBtn)
                {
                    capBtn.IsEnabled = false;
                    if (capBtn.Content is StackPanel sp && sp.Children.OfType<TextBlock>().LastOrDefault() is TextBlock tb)
                        tb.Text = "Capturing...";
                }
                if (FindName("CaptureLabel") is FrameworkElement label) label.Visibility = Visibility.Collapsed;
                if (FindName("CaptureSpinner") is FrameworkElement spin) spin.Visibility = Visibility.Visible;
                var rot = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(2))) { RepeatBehavior = RepeatBehavior.Forever };
                if (FindName("CaptureRotate") is RotateTransform r) r.BeginAnimation(RotateTransform.AngleProperty, rot);
            }
            catch { }

            // Clipboard: image or text
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

            if (!string.IsNullOrWhiteSpace(questionToAnswer))
            {
                // Dedupe: skip if same as most recent
                var recent = QnAStore.GetHistory().FirstOrDefault();
                if (recent != null && Normalize(recent.Question) == Normalize(questionToAnswer))
                {
                    AppendLog("Duplicate question ignored (same as latest)");
                }
                else
                {
                // Add pending QnA
                var id = DateTime.UtcNow.Ticks;
                var item = latestPending ?? new QnA
                {
                    Id = id,
                    Question = questionToAnswer,
                    Status = "pending",
                    Language = GetSelectedLanguageCode(),
                    Source = "capture",
                    Context = new QnAContext { ImageDataUrl = imageDataUrl, ClipboardText = clipboardText },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    QuestionNumber = QnAStore.GetHistory().Count + 1
                };
                if (latestPending == null) QnAStore.Add(item);
                _lastQuestion = questionToAnswer;
                Dispatcher.Invoke(UpdateTranscriptUi);
                RefreshQnAHistoryUi();

                // Build conversation history from answered
                var history = QnAStore.GetAnsweredPairs(10).ToArray();

                // Generate answer (stubbed)
                var input = new AnswerInput
                {
                    Question = questionToAnswer,
                    Language = GetSelectedLanguageCode(),
                    Cv = ctx.Cv ?? string.Empty,
                    JobDescription = ctx.JobDescription ?? string.Empty,
                    PersonalProfile = ctx.PersonalProfile,
                    ProjectInfo = ctx.ProjectInfo,
                    ConversationHistory = history.Length > 0 ? history : null,
                    IsFollowUp = false,
                    ImageDataUrl = imageDataUrl,
                    ClipboardText = clipboardText,
                    DocumentId = string.IsNullOrWhiteSpace(ctx.DocumentId) ? null : ctx.DocumentId,
                    QuestionNumber = item.QuestionNumber
                };
                var result = await AnswerGenerator.GenerateAsync(input);
                var answer = (result.Answer ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    QnAStore.Update(item.Id, q => { q.Answer = answer; q.Status = "answered"; });
                    AppendLog("Answer generated (stub)");
                    RefreshQnAHistoryUi();
                    FlashCaptureButton(success: true);
                }
                else
                {
                    FlashCaptureButton(success: false);
                }
                }
            }
            else
            {
                AppendLog("Not a question according to AI (ignored)");
                FlashCaptureButton(success: false);
            }
        }
        catch (Exception ex)
        {
            AppendLog("Capture flow error: " + ex.Message);
            FlashCaptureButton(success: false);
        }
        finally
        {
            // Restore UI
            try
            {
                if (FindName("CaptureRotate") is RotateTransform rs) rs.BeginAnimation(RotateTransform.AngleProperty, null);
                if (FindName("CaptureSpinner") is FrameworkElement spin) spin.Visibility = Visibility.Collapsed;
                if (FindName("CaptureLabel") is FrameworkElement label) label.Visibility = Visibility.Visible;
                if (FindName("BtnCapture") is Button capBtn)
                {
                    capBtn.IsEnabled = true;
                    if (capBtn.Content is StackPanel sp && sp.Children.OfType<TextBlock>().LastOrDefault() is TextBlock tb)
                        tb.Text = "Capture";
                }
            }
            catch { }
            _captureRunning = false;
            try { _autoExtractTimer.Start(); } catch { }
        }
    }

    private static string Normalize(string s)
        => (s ?? string.Empty).Trim().ToLowerInvariant().Replace("  ", " ");

    private void FlashCaptureButton(bool success)
    {
        try
        {
            if (FindName("BtnCapture") is not Button capBtn) return;
            var ok = success;
            var from = ok ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68); // green/red
            // original target color from theme
            var toBrush = TryFindResource("SoftAccentBrush") as SolidColorBrush;
            var to = toBrush is SolidColorBrush sb ? sb.Color : Color.FromRgb(111, 168, 255);
            capBtn.Background = new SolidColorBrush(from);
            var anim = new ColorAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(900))) { FillBehavior = FillBehavior.Stop };
            anim.Completed += (_, __) =>
            {
                try { capBtn.Background = new SolidColorBrush(to); } catch { }
            };
            if (capBtn.Background is SolidColorBrush b)
            {
                b.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            }
        }
        catch { }
    }

    private static string? BitmapSourceToDataUrl(BitmapSource bmp)
    {
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var b64 = Convert.ToBase64String(ms.ToArray());
            return "data:image/png;base64," + b64;
        }
        catch { return null; }
    }
}
