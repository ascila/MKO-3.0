using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text;
using OverlayOverlay.Services;
using NAudio.Wave;

namespace OverlayOverlay;

public partial class MainWindow : Window
{
    private WasapiLoopbackCapture? _loopback;
    private double _level;
    private WaveInEvent? _mic;
    private double _levelMic;
    private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private int _selectedMicDeviceIndex = -1;
    
    // Faltantes: buffers/servicios/estado
    private BufferedWaveProvider? _loopbackBuffer;
    private AzureSpeechTranscriber? _cloudTranscriber;
    private readonly StringBuilder _transcript = new();
    private string _lastQuestion = string.Empty;
    private string _apiUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _sessionId = string.Empty;
    private QuestionExtractor? _questionExtractor;
    

    public MainWindow()
    {
        InitializeComponent();
        _levelTimer.Tick += (_, __) => UpdateLevelUi();
    }

    // Excluir de capturas si es posible (Windows 10 2004+)
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(hwnd, (uint)DisplayAffinity.ExcludeFromCapture);
        }
        catch { /* Ignorar si no soportado */ }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Inicializar opacidad desde slider
        this.Opacity = OpacitySlider.Value;
        // Tema por defecto
        if (FindName("ThemeLight") is RadioButton rb) rb.IsChecked = true;
        // Mantener pegado arriba
        this.Top = 0;
        // Cargar lista de micrófonos
        RefreshMicDevices();
        if (FindName("MicDeviceCombo") is ComboBox micCb && micCb.Items.Count > 0)
        {
            micCb.SelectedIndex = 0;
        }

        // Enlazar acciones de UI: Capturar pregunta y limpiar transcript
        if (FindName("BtnCapture") is Button btnCap)
            btnCap.Click += CaptureQuestion_Click;
        var clearBtn = FindButtonByLabel("Clear Transcript");
        if (clearBtn != null)
            clearBtn.Click += ClearTranscript_Click;

        // Cargar integración desde variables de entorno (con defaults)
        _apiUrl = Environment.GetEnvironmentVariable("OVERLAY_API_URL") ?? "http://localhost:9002/api/capture";
        _apiKey = Environment.GetEnvironmentVariable("OVERLAY_API_KEY") ?? "autoscribe-overlay-secret-key-12345";
        _sessionId = Environment.GetEnvironmentVariable("OVERLAY_SESSION_ID") ?? "local-dev-session";
        if (FindName("ApiUrlBox") is TextBox apiUrlBox) apiUrlBox.Text = _apiUrl;
        if (FindName("ApiKeyBox") is TextBox apiKeyBox) apiKeyBox.Text = _apiKey;
        if (FindName("SessionIdBox") is TextBox sessionBox) sessionBox.Text = _sessionId;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
            this.Opacity = e.NewValue;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (FindName("ThemeDark") is RadioButton dark && dark.IsChecked == true)
            ApplyTheme("Themes/Dark.xaml");
        else
            ApplyTheme("Themes/Light.xaml");

        this.Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x00, 0x00));
    }

    private void ApplyTheme(string source)
    {
        var dict = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        // Quitar tema previo (Light/Dark) si existe
        for (int i = Application.Current.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var rd = Application.Current.Resources.MergedDictionaries[i];
            if (rd.Source != null && rd.Source.OriginalString.Contains("Themes/"))
            {
                Application.Current.Resources.MergedDictionaries.RemoveAt(i);
            }
        }
        Application.Current.Resources.MergedDictionaries.Add(dict);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (Top != 0) Top = 0; // fuerza pegado arriba
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("SettingsPopup") is Popup pop)
            pop.IsOpen = true;
    }

    private void LangToggle_Click(object sender, RoutedEventArgs e)
    {
        // Exclusividad simple ES/EN
        if (sender == FindName("LangES"))
        {
            if (FindName("LangES") is ToggleButton es && es.IsChecked == true)
                if (FindName("LangEN") is ToggleButton en) en.IsChecked = false;
        }
        else if (sender == FindName("LangEN"))
        {
            if (FindName("LangEN") is ToggleButton en && en.IsChecked == true)
                if (FindName("LangES") is ToggleButton es) es.IsChecked = false;
        }

        // Si cloud ASR está activo, reiniciar con el nuevo idioma
        if (FindName("SystemTranscribeToggle") is ToggleButton cloud && cloud.IsChecked == true)
        {
            _ = RestartCloudTranscriberWithCurrentLanguageAsync();
        }
    }

    private void AudioMonitorToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.IsChecked == true)
            StartAudioMonitor();
        else
            StopAudioMonitor();
    }

    private void MicMonitorToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.IsChecked == true)
            StartMicMonitor();
        else
            StopMicMonitor();
    }

    private void StartAudioMonitor()
    {
        try
        {
            _loopback = new WasapiLoopbackCapture();
            _loopback.DataAvailable += LoopbackOnDataAvailable;
            _loopback.RecordingStopped += (_, __) => { _loopback?.Dispose(); _loopback = null; };
            _loopback.StartRecording();
            _levelTimer.Start();

            // Preparar buffer para streaming cloud (se crea una vez)
            _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5)
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo iniciar el monitor de audio: " + ex.Message, "Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (FindName("AudioMonitorToggle") is ToggleButton tb) tb.IsChecked = false;
        }
    }

    private void StopAudioMonitor()
    {
        try { _levelTimer.Stop(); } catch { }
        try { _loopback?.StopRecording(); } catch { }
        try { _loopback?.Dispose(); } catch { }
        _loopback = null;
        _level = 0;
        UpdateLevelUi();
    }

    private void LoopbackOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float max = 0f;
        var bytes = e.Buffer;
        for (int i = 0; i < e.BytesRecorded - 4; i += 4)
        {
            float sample = BitConverter.ToSingle(bytes, i);
            sample = Math.Abs(sample);
            if (sample > max) max = sample;
        }
        var target = Math.Min(1.0, max);
        _level = _level * 0.7 + target * 0.3; // suavizado

        // alimentar buffer para transcripción cloud
        try { _loopbackBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
    }

    private void StartMicMonitor()
    {
        try
        {
            int deviceCount = WaveInEvent.DeviceCount;
            if (deviceCount == 0)
                throw new InvalidOperationException("No hay dispositivos de entrada de audio disponibles.");
            int deviceIndex = _selectedMicDeviceIndex >= 0 && _selectedMicDeviceIndex < deviceCount
                ? _selectedMicDeviceIndex : 0;
            _mic = new WaveInEvent { DeviceNumber = deviceIndex, WaveFormat = new WaveFormat(44100, 1) };
            _mic.DataAvailable += MicOnDataAvailable;
            _mic.RecordingStopped += (_, __) => { _mic?.Dispose(); _mic = null; };
            _mic.StartRecording();
            _levelTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo iniciar el micrófono: " + ex.Message, "Mic", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (FindName("MicMonitorToggle") is ToggleButton tb) tb.IsChecked = false;
        }
    }

    private void StopMicMonitor()
    {
        try { _mic?.StopRecording(); } catch { }
        try { _mic?.Dispose(); } catch { }
        _mic = null;
        _levelMic = 0;
        UpdateLevelUi();
    }

    private void MicOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // 16-bit PCM
        int bytes = e.BytesRecorded;
        short sample;
        float max = 0f;
        for (int i = 0; i < bytes - 2; i += 2)
        {
            sample = BitConverter.ToInt16(e.Buffer, i);
            float v = Math.Abs(sample / 32768f);
            if (v > max) max = v;
        }
        var target = Math.Min(1.0, max);
        _levelMic = _levelMic * 0.7 + target * 0.3;
    }

    private void UpdateLevelUi()
    {
        if (!IsLoaded) return;
        // System level bar
        double sysMax = 60;
        if (FindName("SysLevelBar") is Border sysBar) sysMax = sysBar.ActualWidth > 0 ? sysBar.ActualWidth : sysBar.Width;
        var width = sysMax * _level;
        if (FindName("SysFill") is Border fill)
        {
            fill.Width = Math.Max(2, width);
            fill.Background = new SolidColorBrush(_level > 0.6 ? Color.FromRgb(239, 68, 68) : Color.FromRgb(16, 185, 129));
        }

        // Mic level bar
        double micMax = 60;
        if (FindName("MicLevelBar") is Border micBar) micMax = micBar.ActualWidth > 0 ? micBar.ActualWidth : micBar.Width;
        var widthMic = micMax * _levelMic;
        if (FindName("MicFill") is Border mfill)
        {
            mfill.Width = Math.Max(2, widthMic);
            mfill.Background = new SolidColorBrush(_levelMic > 0.6 ? Color.FromRgb(239, 68, 68) : Color.FromRgb(16, 185, 129));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAudioMonitor();
        StopMicMonitor();
        base.OnClosed(e);
    }

    private void RefreshMicDevices()
    {
        try
        {
            if (FindName("MicDeviceCombo") is not ComboBox micCb) return;
            var currentTag = (micCb.SelectedItem as ComboBoxItem)?.Tag;
            micCb.Items.Clear();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                var name = string.IsNullOrWhiteSpace(caps.ProductName) ? $"Device {i}" : caps.ProductName;
                var item = new ComboBoxItem { Content = name, Tag = i };
                micCb.Items.Add(item);
            }
            if (micCb.Items.Count == 0)
            {
                micCb.Items.Add(new ComboBoxItem { Content = "No devices found", IsEnabled = false });
                _selectedMicDeviceIndex = -1;
            }
            else if (currentTag is int prev && prev >= 0 && prev < WaveInEvent.DeviceCount)
            {
                _selectedMicDeviceIndex = prev;
                foreach (var it in micCb.Items)
                    if (it is ComboBoxItem cbi && cbi.Tag is int tag && tag == prev)
                        micCb.SelectedItem = cbi;
            }
        }
        catch { }
    }

    private void MicDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox micCb && micCb.SelectedItem is ComboBoxItem item && item.Tag is int idx)
        {
            _selectedMicDeviceIndex = idx;
            if (FindName("MicMonitorToggle") is ToggleButton tb && tb.IsChecked == true)
            {
                StopMicMonitor();
                StartMicMonitor();
            }
        }
    }

    // Popup de settings también fuera de captura
    private void SettingsPopup_Opened(object sender, EventArgs e)
    {
        try
        {
            if (sender is Popup pop)
            {
                // Asegurar que el HWND del Popup exista antes de aplicar ExcludeFromCapture
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var src = (HwndSource?)PresentationSource.FromVisual(pop.Child);
                        if (src != null)
                        {
                            SetWindowDisplayAffinity(src.Handle, (uint)DisplayAffinity.ExcludeFromCapture);
                        }
                    }
                    catch { }
                }, DispatcherPriority.Loaded);
                // Reintento corto por si el handle se crea con retraso
                var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                once.Tick += (_, __) =>
                {
                    try
                    {
                        var src2 = (HwndSource?)PresentationSource.FromVisual(pop.Child);
                        if (src2 != null)
                        {
                            SetWindowDisplayAffinity(src2.Handle, (uint)DisplayAffinity.ExcludeFromCapture);
                        }
                    }
                    catch { }
                    finally { (once as DispatcherTimer)?.Stop(); }
                };
                once.Start();
            }
            RefreshMicDevices();
        }
        catch { }
    }

    private async void SystemTranscribeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        if (tb.IsChecked == true)
        {
            try
            {
                // Requiere paquetes: Microsoft.CognitiveServices.Speech
                var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? "";
                var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? "";
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
                    throw new InvalidOperationException("AZURE_SPEECH_KEY/AZURE_SPEECH_REGION no configurados.");

                if (_loopbackBuffer == null)
                {
                    // si el monitor no está activo, creamos un capturador efímero sólo para transcript
                    _loopback = new WasapiLoopbackCapture();
                    _loopback.DataAvailable += (s, ev) => { try { _loopbackBuffer?.AddSamples(ev.Buffer, 0, ev.BytesRecorded); } catch { } };
                    _loopback.RecordingStopped += (_, __) => { _loopback?.Dispose(); _loopback = null; };
                    _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };
                    _loopback.StartRecording();
                }

                _cloudTranscriber = new AzureSpeechTranscriber(key, region);
                _cloudTranscriber.PartialTranscription += text => { /* opcional: mostrar parciales */ };
                _cloudTranscriber.FinalTranscription += async text =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _transcript.AppendLine(text);
                        }
                    });
                    try
                    {
                        var keyOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                        if (!string.IsNullOrWhiteSpace(keyOpenAi))
                        {
                            _questionExtractor ??= new QuestionExtractor(keyOpenAi!);
                            var (isQ, q) = await _questionExtractor.ExtractAsync(text);
                            if (isQ && !string.IsNullOrWhiteSpace(q))
                            {
                                _lastQuestion = q;
                                // Opcional: mostrar de forma simple en el título para validar
                                Dispatcher.Invoke(() => this.Title = $"Overlay — Q: {q}");
                            }
                        }
                    }
                    catch { }
                };

                await _cloudTranscriber.StartAsync(new BufferedToProvider(_loopbackBuffer), GetSelectedLanguageCode(), default);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo iniciar la transcripción cloud: " + ex.Message, "Cloud", MessageBoxButton.OK, MessageBoxImage.Warning);
                tb.IsChecked = false;
            }
        }
        else
        {
            try { await _cloudTranscriber?.StopAsync()!; } catch { }
            _cloudTranscriber = null;
        }
    }

    private void ClearTranscript_Click(object sender, RoutedEventArgs e)
    {
        _transcript.Clear();
        _lastQuestion = string.Empty;
        try { this.Title = "Overlay"; } catch { }
    }

    private void ApiUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) _apiUrl = tb.Text?.Trim() ?? string.Empty;
    }

    private void ApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) _apiKey = tb.Text ?? string.Empty;
    }

    private void SessionIdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) _sessionId = tb.Text ?? string.Empty;
    }

    // Adaptador simple: IWaveProvider que lee del BufferedWaveProvider
    private class BufferedToProvider : IWaveProvider
    {
        private readonly BufferedWaveProvider _buffer;
        public BufferedToProvider(BufferedWaveProvider buffer) { _buffer = buffer; }
        public WaveFormat WaveFormat => _buffer.WaveFormat;
        public int Read(byte[] dest, int offset, int count) => _buffer.Read(dest, offset, count);
    }

    private string GetSelectedLanguageCode()
    {
        try
        {
            if (FindName("LangES") is ToggleButton es && es.IsChecked == true) return "es-ES";
            return "en-US";
        }
        catch { return "en-US"; }
    }

    private async System.Threading.Tasks.Task RestartCloudTranscriberWithCurrentLanguageAsync()
    {
        try
        {
            var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? "";
            var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? "";
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region)) return;

            if (_cloudTranscriber != null)
            {
                try { await _cloudTranscriber.StopAsync(); } catch { }
                _cloudTranscriber = null;
            }

            if (_loopbackBuffer == null)
            {
                _loopback ??= new WasapiLoopbackCapture();
                _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };
                _loopback.DataAvailable += (s, ev) => { try { _loopbackBuffer?.AddSamples(ev.Buffer, 0, ev.BytesRecorded); } catch { } };
                try { _loopback.StartRecording(); } catch { }
            }

            _cloudTranscriber = new AzureSpeechTranscriber(key, region);
            _cloudTranscriber.PartialTranscription += text => { };
            _cloudTranscriber.FinalTranscription += async text =>
            {
                Dispatcher.Invoke(() => { if (!string.IsNullOrWhiteSpace(text)) _transcript.AppendLine(text); });
                try
                {
                    var keyOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    if (!string.IsNullOrWhiteSpace(keyOpenAi))
                    {
                        _questionExtractor ??= new QuestionExtractor(keyOpenAi!);
                        var (isQ, q) = await _questionExtractor.ExtractAsync(text);
                        if (isQ && !string.IsNullOrWhiteSpace(q))
                        {
                            _lastQuestion = q;
                            Dispatcher.Invoke(() => this.Title = $"Overlay — Q: {q}");
                        }
                    }
                }
                catch { }
            };

            await _cloudTranscriber.StartAsync(new BufferedToProvider(_loopbackBuffer), GetSelectedLanguageCode());
        }
        catch { }
    }

    private static Button? FindButtonByLabel(string label)
    {
        foreach (Window w in Application.Current.Windows)
        {
            var btn = FindButtonRecursive(w.Content as DependencyObject, label);
            if (btn != null) return btn;
        }
        return null;
    }

    private static Button? FindButtonRecursive(DependencyObject? parent, string label)
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button b)
            {
                if (b.Content is StackPanel sp)
                {
                    foreach (var c in sp.Children)
                        if (c is TextBlock tb && string.Equals(tb.Text, label, StringComparison.OrdinalIgnoreCase))
                            return b;
                }
                else if (b.Content is TextBlock tb && string.Equals(tb.Text, label, StringComparison.OrdinalIgnoreCase))
                    return b;
            }
            var found = FindButtonRecursive(child, label);
            if (found != null) return found;
        }
        return null;
    }

    private async void CaptureQuestion_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = _transcript.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("No hay transcripción suficiente todavía.", "Capturar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Extraer pregunta localmente si hay clave de IA, si no, usa última oración con '?'
            string q;
            var keyOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(keyOpenAi))
            {
                _questionExtractor ??= new QuestionExtractor(keyOpenAi!);
                var res = await _questionExtractor.ExtractAsync(text);
                q = res.isQuestion ? res.question : string.Empty;
            }
            else
            {
                q = FallbackExtractQuestion(text);
            }
            var isQ = !string.IsNullOrWhiteSpace(q);
            if (isQ && !string.IsNullOrWhiteSpace(q))
            {
                _lastQuestion = q;
                try { Clipboard.SetText(q); } catch { }
                this.Title = $"Overlay — Q: {q}";
                MessageBox.Show($"Pregunta capturada:\n{q}", "Capturar", MessageBoxButton.OK, MessageBoxImage.Information);

                // Enviar a la aplicación web
                try
                {
                    var client = new AutoScribeClient(_apiUrl, _apiKey);
                    var sid = string.IsNullOrWhiteSpace(_sessionId) ? "local-dev-session" : _sessionId;
                    await client.SendQuestionAsync(q, sid);
                }
                catch (Exception ex2)
                {
                    MessageBox.Show("Error enviando a la aplicación web: " + ex2.Message, "Integración", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show("No se detectó una pregunta clara en el fragmento actual.", "Capturar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Error capturando pregunta: " + ex.Message, "Capturar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FallbackExtractQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // Busca la última oración con signo de interrogación
        var parts = text.Split(new[] { '.', '!', '?', '¿' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var s = parts[i].Trim();
            if (s.EndsWith("?", StringComparison.Ordinal) || s.StartsWith("¿"))
                return s.EndsWith("?") ? s : s + "?";
        }
        return string.Empty;
    }

    // Interop para ExclusiveFromCapture
    private enum DisplayAffinity : uint
    {
        None = 0,
        Monitor = 1,
        ExcludeFromCapture = 0x11
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
