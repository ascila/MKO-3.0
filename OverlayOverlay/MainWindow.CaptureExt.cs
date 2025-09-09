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
    private async void Capture_RunFlow_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ctx = ContextProvider.Get();
            var transcript = _transcript.ToString();
            if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(ctx.Cv) || string.IsNullOrWhiteSpace(ctx.JobDescription))
                return;

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

            // Extract question (auto derives contextual keywords from Setup)
            string question = string.Empty;
            bool isQuestion = false;
            try
            {
                var keyOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                _questionExtractor ??= new QuestionExtractor(keyOpenAi ?? string.Empty);
                AppendLog("AI: extracting interview question...");
                var res = await _questionExtractor.ExtractAsync(transcript);
                isQuestion = res.isQuestion;
                question = (res.question ?? string.Empty).Trim();
                AppendLog("AI: extraction completed");
            }
            catch (Exception ex)
            {
                AppendLog("AI extraction error: " + ex.Message);
            }

            if (isQuestion && !string.IsNullOrWhiteSpace(question))
            {
                // Add pending QnA
                var id = DateTime.UtcNow.Ticks;
                var item = new QnA
                {
                    Id = id,
                    Question = question,
                    Status = "pending",
                    Language = GetSelectedLanguageCode(),
                    Source = "capture",
                    Context = new QnAContext { ImageDataUrl = imageDataUrl, ClipboardText = clipboardText },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    QuestionNumber = QnAStore.GetHistory().Count + 1
                };
                QnAStore.Add(item);
                _lastQuestion = question;
                Dispatcher.Invoke(UpdateTranscriptUi);

                // Build conversation history from answered
                var history = QnAStore.GetAnsweredPairs(10).ToArray();

                // Generate answer (stubbed)
                var input = new AnswerInput
                {
                    Question = question,
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
                    QnAStore.Update(id, q => { q.Answer = answer; q.Status = "answered"; });
                    AppendLog("Answer generated (stub)");
                }
            }
            else
            {
                AppendLog("Not a question according to AI (ignored)");
            }
        }
        catch (Exception ex)
        {
            AppendLog("Capture flow error: " + ex.Message);
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
        }
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

