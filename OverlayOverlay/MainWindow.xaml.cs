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
using System.Windows.Media.Animation;
using System.Diagnostics;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;

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
    private bool _asrOn;
    private string _partialLine = string.Empty;
    private Storyboard? _captureSb;
    private bool _measureOnly = false; // when true, only [MEASURE] logs are written
    

    public MainWindow()
    {
        InitializeComponent();
        _levelTimer.Tick += (_, __) => UpdateLevelUi();
    }

    private void ShowToast(string message, int ms = 2000)
    {
        try
        {
            if (FindName("ToastText") is TextBlock tt && FindName("ToastPopup") is Popup tp)
            {
                tt.Text = message;
                tp.IsOpen = true;
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                t.Tick += (_, __) => { try { tp.IsOpen = false; } catch { } t.Stop(); };
                t.Start();
            }
        }
        catch { }
    }

    private void AppendLog(string message)
    {
        try
        {
            // Filter non-measure logs if measure-only mode is active
            if (_measureOnly && !(message?.StartsWith("[MEASURE]") ?? false)) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (FindName("DebugLogBox") is TextBox tb)
                {
                    tb.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                    tb.ScrollToEnd();
                }
            });
        }
        catch { }
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

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Prevent maximize state (avoid Windows snap-to-maximize)
        try
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
            }
        }
        catch { }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Inicializar opacidad desde slider
        this.Opacity = OpacitySlider.Value;
        try { this.Top = 0; this.Height = SystemParameters.WorkArea.Height; } catch { }
        // Tema por defecto
        if (FindName("ThemeLight") is RadioButton rb) rb.IsChecked = true;
        // Mantener pegado arriba
        this.Top = 0;
        // Cargar lista de micrÃ³fonos
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

        // Cargar integraciÃ³n desde variables de entorno (con defaults)
        _apiUrl = Environment.GetEnvironmentVariable("OVERLAY_API_URL") ?? "http://localhost:9002/api/capture";
        _apiKey = Environment.GetEnvironmentVariable("OVERLAY_API_KEY") ?? "autoscribe-overlay-secret-key-12345";
        _sessionId = Environment.GetEnvironmentVariable("OVERLAY_SESSION_ID") ?? "local-dev-session";
        if (FindName("ApiUrlBox") is TextBox apiUrlBox) apiUrlBox.Text = _apiUrl;
        if (FindName("ApiKeyBox") is TextBox apiKeyBox) apiKeyBox.Text = _apiKey;
        if (FindName("SessionIdBox") is TextBox sessionBox) sessionBox.Text = _sessionId;

        // Initialize info panels
        if (FindName("LastQuestionText") is TextBlock lqt) lqt.Text = "No questions asked yet.";
        if (FindName("LiveTranscriptBox") is TextBlock ltb) ltb.Text = "Waiting for audio...";
        // Idioma por defecto visible: EN activo
        try { if (FindName("LangEN") is ToggleButton en) en.IsChecked = true; } catch { }
        try { if (FindName("LangES") is ToggleButton es) es.IsChecked = false; } catch { }
        // Estado inicial del botÃ³n Start: verde (stopped)
        try { if (FindName("BtnStart") is Button bs) { bs.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); bs.Foreground = Brushes.White; } } catch { }
        AppendLog("UI loaded");
        // Layout sizing hooks
        try { this.SizeChanged += (_, __) => AdjustSectionHeightsByWindow(); } catch { }
        try
        {
            if (FindName("DebugToggle") is CheckBox dbg)
            {
                dbg.Checked += (_, __) => AdjustSectionHeightsByWindow();
                dbg.Unchecked += (_, __) => AdjustSectionHeightsByWindow();
            }
        }
        catch { }
        // No dependemos de colapsar/expandir secciones para el reparto 60/20/20
        AdjustSectionHeightsByWindow();

        // Ensure wheel scrolling works even with hidden scrollbars
        try
        {
            // Reflow rows when sections are expanded/collapsed
            WireSectionExpanders();
            AttachWheelScroll(FindName("QnAScroll") as ScrollViewer);
            AttachWheelScroll(FindName("LiveScroll") as ScrollViewer);
            AttachWheelScroll(FindName("DebugScroll") as ScrollViewer);
            // Capture preview wheel globally to route to the nearest ScrollViewer under mouse
            this.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(Global_PreviewMouseWheel), true);

            // Log initial metrics after first layout pass
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogScrollMetrics("init:QnA", FindName("QnAScroll") as ScrollViewer);
                LogScrollMetrics("init:Live", FindName("LiveScroll") as ScrollViewer);
                LogScrollMetrics("init:Debug", FindName("DebugScroll") as ScrollViewer);
            }), DispatcherPriority.Background);
        }
        catch { }
    }

    private void WireSectionExpanders()
    {
        try
        {
            void hook(string tag, Expander? ex)
            {
                if (ex == null) return;
                ex.Expanded += (_, __) => { try { AdjustSectionHeightsByWindow(); AdjustWindowHeightForSections(); MeasureAndLogSection(tag); } catch { } };
                ex.Collapsed += (_, __) => { try { AdjustSectionHeightsByWindow(); AdjustWindowHeightForSections(); MeasureAndLogSection(tag); } catch { } };
            }
            hook("QnA",  FindName("QnASection") as Expander);
            hook("Live", FindName("LiveSection") as Expander);
            hook("Debug",FindName("DebugSection") as Expander);
        }
        catch { }
    }

    // Enter measure-only mode, clear log, and write layout metrics for the given section
    private void MeasureAndLogSection(string tag)
    {
        try
        {
            // Enable measure-only output and clear previous logs as requested
            _measureOnly = true;
            Dispatcher.Invoke(() => { if (FindName("DebugLogBox") is TextBox tb) tb.Clear(); });

            if (tag == "QnA")
            {
                LogMeasureForSection(
                    "QnA",
                    FindName("RowQnA") as RowDefinition,
                    FindName("QnAHeader") as FrameworkElement,
                    headerGrid: FindName("QnAHeader") as FrameworkElement,
                    leftToggle: FindChild<ToggleButton>(FindName("QnAHeader") as DependencyObject),
                    title: FindBoldText(FindName("QnAHeader") as DependencyObject),
                    subtitle: FindNonBoldText(FindName("QnAHeader") as DependencyObject),
                    rightBtns: new FrameworkElement?[] { FindName("BtnCopyAllQuestions") as FrameworkElement }
                );
            }
            else if (tag == "Live")
            {
                LogMeasureForSection(
                    "Live",
                    FindName("RowLive") as RowDefinition,
                    FindName("LiveHeader") as FrameworkElement,
                    headerGrid: FindName("LiveHeader") as FrameworkElement,
                    leftToggle: FindChild<ToggleButton>(FindName("LiveHeader") as DependencyObject),
                    title: FindBoldText(FindName("LiveHeader") as DependencyObject),
                    subtitle: FindNonBoldText(FindName("LiveHeader") as DependencyObject),
                    rightBtns: Array.Empty<FrameworkElement?>()
                );
            }
            else if (tag == "Debug")
            {
                // Only log Debug if the panel is visible
                bool dbgVisible = (FindName("DebugToggle") as CheckBox)?.IsChecked == true;
                if (dbgVisible)
                {
                    LogMeasureForSection(
                        "Debug",
                        FindName("RowDebug") as RowDefinition,
                        FindName("DebugHeader") as FrameworkElement,
                        headerGrid: FindName("DebugHeader") as FrameworkElement,
                        leftToggle: FindChild<ToggleButton>(FindName("DebugHeader") as DependencyObject),
                        title: FindBoldText(FindName("DebugHeader") as DependencyObject),
                        subtitle: FindNonBoldText(FindName("DebugHeader") as DependencyObject),
                        rightBtns: new FrameworkElement?[] { FindName("BtnCopyLogs") as FrameworkElement, FindName("BtnTestAzure") as FrameworkElement }
                    );
                }
            }
        }
        catch { }
    }

    private void LogMeasureForSection(string tag, RowDefinition? row, FrameworkElement? header,
                                      FrameworkElement? headerGrid,
                                      FrameworkElement? leftToggle,
                                      FrameworkElement? title,
                                      FrameworkElement? subtitle,
                                      FrameworkElement?[]? rightBtns)
    {
        try
        {
            double rowH = row?.ActualHeight ?? double.NaN;
            double headerH = header?.ActualHeight ?? double.NaN;

            AppendLog($"[MEASURE] {tag}: rowH={rowH:0.0} headerH={headerH:0.0}");

            if (headerGrid != null)
            {
                var origin = GetTopLeft(headerGrid);
                AppendLog($"[MEASURE] {tag} header@ ({origin.X:0.0},{origin.Y:0.0})");
            }

            if (leftToggle != null)
            {
                var p = GetTopLeft(leftToggle);
                AppendLog($"[MEASURE] {tag} leftToggle@ ({p.X:0.0},{p.Y:0.0})");
            }
            if (title != null)
            {
                var p = GetTopLeft(title);
                AppendLog($"[MEASURE] {tag} title@ ({p.X:0.0},{p.Y:0.0})");
            }
            if (subtitle != null)
            {
                var p = GetTopLeft(subtitle);
                AppendLog($"[MEASURE] {tag} subtitle@ ({p.X:0.0},{p.Y:0.0})");
            }
            if (rightBtns != null)
            {
                for (int i = 0; i < rightBtns.Length; i++)
                {
                    var rb = rightBtns[i];
                    if (rb == null) continue;
                    var p = GetTopLeft(rb);
                    AppendLog($"[MEASURE] {tag} rightBtn[{i}]@ ({p.X:0.0},{p.Y:0.0})");
                }
            }
            AppendLog($"[MEASURE] ----");
        }
        catch { }
    }

    private Point GetTopLeft(FrameworkElement el)
    {
        try
        {
            var t = el.TransformToAncestor(this);
            var p = t.Transform(new Point(0, 0));
            return p;
        }
        catch { return new Point(double.NaN, double.NaN); }
    }

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var ch = VisualTreeHelper.GetChild(parent, i);
            if (ch is T tt) return tt;
            var found = FindChild<T>(ch);
            if (found != null) return found;
        }
        return null;
    }

    private static TextBlock? FindBoldText(DependencyObject? parent)
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var ch = VisualTreeHelper.GetChild(parent, i);
            if (ch is TextBlock tb && tb.FontWeight == FontWeights.Bold) return tb;
            var found = FindBoldText(ch);
            if (found != null) return found;
        }
        return null;
    }

    private static TextBlock? FindNonBoldText(DependencyObject? parent)
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var ch = VisualTreeHelper.GetChild(parent, i);
            if (ch is TextBlock tb && tb.FontWeight != FontWeights.Bold) return tb;
            var found = FindNonBoldText(ch);
            if (found != null) return found;
        }
        return null;
    }

    private void AdjustWindowHeightForSections()
    {
        try
        {
            bool dbgVisible = true;
            try { dbgVisible = (FindName("DebugToggle") as CheckBox)?.IsChecked == true; } catch { }
            bool qExp = (FindName("QnASection") as Expander)?.IsExpanded ?? true;
            bool lExp = (FindName("LiveSection") as Expander)?.IsExpanded ?? true;
            bool dExp = dbgVisible ? ((FindName("DebugSection") as Expander)?.IsExpanded ?? true) : false;

            bool anyCollapsed = (!qExp) || (!lExp) || (dbgVisible && !dExp);

            // Available vertical size (DIPs)
            double maxH = SystemParameters.WorkArea.Height;

            if (anyCollapsed)
            {
                // Let WPF compute compact height, then lock it
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var prev = this.SizeToContent;
                        this.SizeToContent = SizeToContent.Height;
                        this.UpdateLayout();
                        double h = Math.Min(this.ActualHeight, maxH);
                        this.SizeToContent = SizeToContent.Manual;
                        this.Height = h;
                        this.Top = 0; // keep pinned to top
                    }
                    catch { }
                }), DispatcherPriority.Background);
            }
            else
            {
                // Restore full working height when all expanded
                this.SizeToContent = SizeToContent.Manual;
                this.Height = maxH;
                this.Top = 0;
            }
        }
        catch { }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    // Window nudge buttons: move horizontally by one third of the current screen's work area
    private void NudgeLeft_Click(object sender, RoutedEventArgs e) => NudgeByThird(-1);
    private void NudgeRight_Click(object sender, RoutedEventArgs e) => NudgeByThird(1);

    private void NudgeByThird(int dir)
    {
        try
        {
            // Determine the monitor work area (device pixels)
            var hwnd = new WindowInteropHelper(this).Handle;
            var mon = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf<MONITORINFO>();
            if (!GetMonitorInfo(mon, ref mi)) return;

            // Convert to DIPs using current DPI
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double workLeft = mi.rcWork.left / dpiX;
            double workWidth = (mi.rcWork.right - mi.rcWork.left) / dpiX;

            double third = workWidth / 3.0;

            // Snap to discrete thirds and move one step
            double idxNow = Math.Round((this.Left - workLeft) / third);
            int idx = (int)idxNow + dir;
            if (idx < 0) idx = 0; if (idx > 2) idx = 2;
            double targetLeft = workLeft + idx * third;

            // Keep fully on screen if overlay wider than a third
            double maxLeft = workLeft + Math.Max(0, workWidth - this.Width);
            targetLeft = Math.Max(workLeft, Math.Min(targetLeft, maxLeft));
            this.Left = targetLeft;
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
            this.Opacity = e.NewValue;
    }

    // Cursor-related handlers removed per user request

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

        // Si cloud ASR estÃ¡ activo, reiniciar con el nuevo idioma
        if (_asrOn)
            _ = RestartCloudTranscriberWithCurrentLanguageAsync();
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
            AppendLog("System audio monitor started");

            // Preparar buffer para streaming cloud (se crea una vez)
            _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(1)
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo iniciar el monitor de audio: " + ex.Message, "Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (FindName("AudioMonitorToggle") is ToggleButton tb) tb.IsChecked = false;
            AppendLog("Error starting system audio: " + ex.Message);
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
        AppendLog("System audio monitor stopped");
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
        // Apply slight visual trim to avoid constant red
        var target = Math.Min(1.0, max * 0.8);
        _level = _level * 0.7 + target * 0.3; // suavizado

        // alimentar buffer para transcripciÃ³n cloud
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
            MessageBox.Show("No se pudo iniciar el micrÃ³fono: " + ex.Message, "Mic", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var target = Math.Min(1.0, max * 0.8);
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
            fill.Background = new SolidColorBrush(_level > 0.8 ? Color.FromRgb(239, 68, 68) : Color.FromRgb(16, 185, 129));
        }

        // Mic level bar
        double micMax = 60;
        if (FindName("MicLevelBar") is Border micBar) micMax = micBar.ActualWidth > 0 ? micBar.ActualWidth : micBar.Width;
        var widthMic = micMax * _levelMic;
        if (FindName("MicFill") is Border mfill)
        {
            mfill.Width = Math.Max(2, widthMic);
            mfill.Background = new SolidColorBrush(_levelMic > 0.8 ? Color.FromRgb(239, 68, 68) : Color.FromRgb(16, 185, 129));
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
        try { AdjustSectionHeightsByWindow(); } catch { }
    }

    private void AdjustSectionHeightsByWindow()
    {
        try
        {
            if (FindName("RowQnA") is not RowDefinition rowQ) return;
            if (FindName("RowLive") is not RowDefinition rowL) return;
            if (FindName("RowDebug") is not RowDefinition rowD) return;

            // Compute available height from Q&A header top to bottom of WorkArea (screen)
            double available = GetAvailableFromQnATopToScreenBottom();
            if (available <= 0)
            {
                // Try again after layout stabilizes
                Dispatcher.BeginInvoke(new Action(AdjustSectionHeightsByWindow), DispatcherPriority.Background);
                return;
            }

            bool dbgVisible = true;
            try { dbgVisible = (FindName("DebugToggle") as CheckBox)?.IsChecked == true; } catch { }

            var expQ = (FindName("QnASection") as Expander)?.IsExpanded ?? true;
            var expL = (FindName("LiveSection") as Expander)?.IsExpanded ?? true;
            var expD = dbgVisible ? ((FindName("DebugSection") as Expander)?.IsExpanded ?? true) : false;

            // Header fallback heights to avoid collapsing to 0 before layout settles
            double hQHeader = (FindName("QnAHeader") as FrameworkElement)?.ActualHeight ?? 0.0;
            double hLHeader = (FindName("LiveHeader") as FrameworkElement)?.ActualHeight ?? 0.0;
            double hDHeader = (FindName("DebugHeader") as FrameworkElement)?.ActualHeight ?? 0.0;
            if (hQHeader < 56) hQHeader = 56; // minimum compact header height
            if (hLHeader < 56) hLHeader = 56;
            if (dbgVisible && hDHeader < 56) hDHeader = 56;

            // Base targets (over whole available)
            double baseQ = available * 0.60;
            double baseL = available * (dbgVisible ? 0.20 : 0.40);
            double baseD = dbgVisible ? (available * 0.20) : 0.0;

            // Collapsed heights: use a uniform target so all cards have same compact size
            const double CollapsedRow = 70.0;
            double collapsedSum = (expQ ? 0 : CollapsedRow) + (expL ? 0 : CollapsedRow) + ((dbgVisible && !expD) ? CollapsedRow : 0);
            double restAvail = Math.Max(0, available - collapsedSum);

            // Expanded base sum
            double sumBaseExp = (expQ ? baseQ : 0) + (expL ? baseL : 0) + (expD ? baseD : 0);
            double scale = sumBaseExp > 0 ? (restAvail / sumBaseExp) : 0;

            double hQ = expQ ? Math.Max(180, baseQ * scale) : CollapsedRow;
            double hL = expL ? Math.Max(100, baseL * scale) : CollapsedRow;
            double hD = expD ? Math.Max(100, baseD * scale) : (dbgVisible ? CollapsedRow : 0.0); // if visible but collapsed, fixed compact height

            // Final clamp if rounding overflow
            double total = hQ + hL + hD + (expD ? 0 : (dbgVisible ? 0 : 0));
            if (total > available && (expQ || expL || expD))
            {
                double over = total - available;
                double sumAdjustables = (expQ ? hQ - 180 : 0) + (expL ? hL - 100 : 0) + (expD ? hD - 100 : 0);
                if (sumAdjustables > 0)
                {
                    double f = Math.Max(0, 1.0 - (over / sumAdjustables));
                    if (expQ) hQ = 180 + (hQ - 180) * f;
                    if (expL) hL = 100 + (hL - 100) * f;
                    if (expD) hD = 100 + (hD - 100) * f;
                }
            }

            rowQ.Height = new GridLength(hQ, GridUnitType.Pixel);
            rowL.Height = new GridLength(hL, GridUnitType.Pixel);
            rowD.Height = new GridLength(hD, GridUnitType.Pixel);

            // Center headers and unify header box when collapsed
            try
            {
                // Account for Border padding=10 around the Expander when setting compact min heights
                const double HeaderCompactMin = CollapsedRow - 20.0; // 70 - (10 top + 10 bottom)

                if (FindName("QnAHeader") is FrameworkElement hqEl)
                {
                    hqEl.MinHeight = HeaderCompactMin; // fijo en ambos estados para evitar salto visual
                    hqEl.Margin = new Thickness(hqEl.Margin.Left, 0, hqEl.Margin.Right, 6);
                }
                if (FindName("LiveHeader") is FrameworkElement hlEl)
                {
                    hlEl.MinHeight = HeaderCompactMin;
                    hlEl.Margin = new Thickness(hlEl.Margin.Left, 0, hlEl.Margin.Right, 6);
                }
                if (dbgVisible && FindName("DebugHeader") is FrameworkElement hdEl)
                {
                    hdEl.MinHeight = HeaderCompactMin;
                    hdEl.Margin = new Thickness(hdEl.Margin.Left, 0, hdEl.Margin.Right, 6);
                }
            }
            catch { }

            // Fix viewport heights to enable scrolling (bars ocultas)
            try
            {
                if (FindName("QnAHeader") is FrameworkElement hq && FindName("QnAScroll") is ScrollViewer svq)
                {
                    var contentH = Math.Max(100, hQ - hq.ActualHeight - 16);
                    if (!double.IsNaN(contentH) && contentH > 0) svq.Height = contentH;
                }
                if (FindName("LiveHeader") is FrameworkElement hl && FindName("LiveScroll") is ScrollViewer svl)
                {
                    var contentH = Math.Max(80, hL - hl.ActualHeight - 16);
                    if (!double.IsNaN(contentH) && contentH > 0) svl.Height = contentH;
                }
                if (FindName("DebugHeader") is FrameworkElement hd && FindName("DebugScroll") is ScrollViewer svd)
                {
                    var contentH = Math.Max(80, hD - hd.ActualHeight - 16);
                    if (!double.IsNaN(contentH) && contentH > 0) svd.Height = contentH;
                }
            }
            catch { }

            try { AdjustQnAPanelHeight(); } catch { }
        }
        catch { }
    }

    private double GetAvailableFromQnATopToScreenBottom()
    {
        try
        {
            if (FindName("QnAHeader") is not FrameworkElement q) return 0;
            var ptScreen = q.PointToScreen(new System.Windows.Point(0, 0)); // device pixels
            var src = System.Windows.PresentationSource.FromVisual(this);
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            double qnaTopDip = ptScreen.Y / dpiY; // DIPs
            // WorkArea in DIPs
            return SystemParameters.WorkArea.Bottom - qnaTopDip;
        }
        catch { return 0; }
    }

    private void AttachWheelScroll(ScrollViewer? sv)
    {
        if (sv == null) return;
        // Smooth pixel scrolling regardless of focus
        sv.PreviewMouseWheel += (s, e) =>
        {
            try
            {
                double step = Math.Sign(e.Delta) * 120; // normalize to 120-tick units
                double before = sv.VerticalOffset;
                sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(before - step, sv.ScrollableHeight)));
                LogScrollMetrics($"wheel:{(sv.Name ?? "scroll")}", sv, e.Delta, before, sv.VerticalOffset);
                e.Handled = true;
            }
            catch { }
        };
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void Global_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        try
        {
            var origin = e.OriginalSource as DependencyObject;
            var sv = FindAncestor<ScrollViewer>(origin);
            if (sv == null) return;
            double step = Math.Sign(e.Delta) * 120;
            double before = sv.VerticalOffset;
            sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(before - step, sv.ScrollableHeight)));
            LogScrollMetrics($"global:{(sv.Name ?? "scroll")}", sv, e.Delta, before, sv.VerticalOffset);
            e.Handled = true;
        }
        catch { }
    }

    // Debug helpers: log extent/viewport/offsets to Debug panel
    private void LogScrollMetrics(string tag, ScrollViewer? sv, int? delta = null, double? before = null, double? after = null)
    {
        try
        {
            if (sv == null) { AppendLog($"scroll[{tag}]: <null>"); return; }
            string name = string.IsNullOrWhiteSpace(sv.Name) ? "(unnamed)" : sv.Name;
            string d = delta.HasValue ? $" d={delta.Value}" : string.Empty;
            string ba = (before.HasValue || after.HasValue) ? $" off={before?.ToString("0.0") ?? "-"}->{after?.ToString("0.0") ?? sv.VerticalOffset.ToString("0.0")}" : $" off={sv.VerticalOffset:0.0}";
            AppendLog($"scroll[{tag}] {name}: ext={sv.ExtentHeight:0.0} view={sv.ViewportHeight:0.0} scr={sv.ScrollableHeight:0.0}{d}{ba}");
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

    // Popup de settings tambiÃ©n fuera de captura
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
                    // si el monitor no estÃ¡ activo, creamos un capturador efÃ­mero sÃ³lo para transcript
                    _loopback = new WasapiLoopbackCapture();
                    _loopback.DataAvailable += (s, ev) => { try { _loopbackBuffer?.AddSamples(ev.Buffer, 0, ev.BytesRecorded); } catch { } };
                    _loopback.RecordingStopped += (_, __) => { _loopback?.Dispose(); _loopback = null; };
                    _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1) };
                    _loopback.StartRecording();
                }

                _cloudTranscriber = new AzureSpeechTranscriber(key, region);
                _cloudTranscriber.DebugLog += msg => AppendLog(msg);
                _cloudTranscriber.PartialTranscription += text => { _partialLine = text ?? string.Empty; Dispatcher.Invoke(UpdateTranscriptUi); };
                _cloudTranscriber.FinalTranscription += async text =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _transcript.AppendLine(text);
                            _partialLine = string.Empty;
                            UpdateTranscriptUi();
                        }
                    });
                    try { } catch { }
                    var prev = text ?? string.Empty;
                    if (prev.Length > 120) prev = prev.Substring(0, 120) + "...";
                    AppendLog("Final transcript: " + prev);
                    Dispatcher.Invoke(UpdateTranscriptUi);
                };

                AppendLog($"Azure Speech: connecting (region={region}, key=****{(key.Length >= 4 ? key[^4..] : key)})");
                await _cloudTranscriber.StartAsync(new BufferedToProvider(_loopbackBuffer), GetSelectedLanguageCode(), default);
                _asrOn = true;
                try { if (FindName("LiveTranscriptBox") is TextBlock ltbStart) ltbStart.Text = "Listening..."; } catch { }
                AppendLog("Azure Speech: connected and streaming");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo iniciar la transcripciÃ³n cloud: " + ex.Message, "Cloud", MessageBoxButton.OK, MessageBoxImage.Warning);
                tb.IsChecked = false;
            }
        }
        else
        {
            try { await _cloudTranscriber?.StopAsync()!; } catch { }
            _cloudTranscriber = null;
            _asrOn = false;
            try { if (FindName("LiveTranscriptBox") is TextBlock ltbStop) ltbStop.Text = "Waiting for audio..."; } catch { }
            AppendLog("Azure Speech: stopped");
    }
    }

    private void ClearTranscript_Click(object sender, RoutedEventArgs e)
    {
        _transcript.Clear();
        _lastQuestion = string.Empty;
        _partialLine = string.Empty;
        try { this.Title = "Overlay"; } catch { }
        // Update panels
        try
        {
            if (FindName("LiveTranscriptBox") is TextBlock ltb) ltb.Text = _asrOn ? "Listening..." : "Waiting for audio...";
            if (FindName("LastQuestionText") is TextBlock lqt) lqt.Text = "No questions asked yet.";
        }
        catch { }
        AppendLog("Transcript cleared");
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
                _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1) };
                _loopback.DataAvailable += (s, ev) => { try { _loopbackBuffer?.AddSamples(ev.Buffer, 0, ev.BytesRecorded); } catch { } };
                try { _loopback.StartRecording(); } catch { }
            }

            _cloudTranscriber = new AzureSpeechTranscriber(key, region);
            _cloudTranscriber.DebugLog += msg => AppendLog(msg);
            _cloudTranscriber.PartialTranscription += text => { };
            _cloudTranscriber.FinalTranscription += async text =>
            {
                Dispatcher.Invoke(() => { if (!string.IsNullOrWhiteSpace(text)) _transcript.AppendLine(text); });
                try { } catch { }
            };

            await _cloudTranscriber.StartAsync(new BufferedToProvider(_loopbackBuffer), GetSelectedLanguageCode());
        }
        catch { }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_asrOn)
            {
                if (_loopback == null)
                {
                    StartAudioMonitor();
                }
                if (_loopbackBuffer == null && _loopback != null)
                {
                    _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(1)
                    };
                    _loopback.DataAvailable += (s, ev) => { try { _loopbackBuffer?.AddSamples(ev.Buffer, 0, ev.BytesRecorded); } catch { } };
                }

                var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? string.Empty;
                var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
                {
                    MessageBox.Show("Configura AZURE_SPEECH_KEY/AZURE_SPEECH_REGION en variables de entorno.", "Cloud", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _cloudTranscriber = new AzureSpeechTranscriber(key, region);
                _cloudTranscriber.DebugLog += msg => AppendLog(msg);
                _cloudTranscriber.PartialTranscription += text => { _partialLine = text ?? string.Empty; Dispatcher.Invoke(UpdateTranscriptUi); };
                _cloudTranscriber.FinalTranscription += async text =>
                {
                    Dispatcher.Invoke(() => { if (!string.IsNullOrWhiteSpace(text)) { _transcript.AppendLine(text); _partialLine = string.Empty; UpdateTranscriptUi(); } });
                    try { } catch { }
                    Dispatcher.Invoke(UpdateTranscriptUi);
                };

                AppendLog($"Azure Speech: connecting (region={region}, key=****{(key.Length >= 4 ? key[^4..] : key)})");
                await _cloudTranscriber.StartAsync(new BufferedToProvider(_loopbackBuffer!), GetSelectedLanguageCode());
                _asrOn = true;
                AppendLog("Azure Speech: connected and streaming");
                if (FindName("BtnStart") is Button btnStart)
                {
                    if (btnStart.Content is StackPanel sp && sp.Children.Count >= 2 && sp.Children[1] is TextBlock tb)
                        tb.Text = "Stop";
                    btnStart.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // red while running (Stop)
                    btnStart.Foreground = Brushes.White;
                }
            }
            else
            {
                try { await _cloudTranscriber?.StopAsync()!; } catch { }
                _cloudTranscriber = null;
                _asrOn = false;
                AppendLog("Azure Speech: stopped");
                if (FindName("BtnStart") is Button btnStart)
                {
                    if (btnStart.Content is StackPanel sp && sp.Children.Count >= 2 && sp.Children[1] is TextBlock tb)
                        tb.Text = "Start";
                    btnStart.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // green when ready to start
                    btnStart.Foreground = Brushes.White;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Error al iniciar/detener transcripciÃ³n: " + ex.Message, "Start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            // Mostrar progreso mientras se extrae y envÃ­a
            try
            {
                var capRef = FindName("BtnCapture") as Button;
                if (capRef != null)
                {
                    capRef.IsEnabled = false;
                    if (capRef.Content is StackPanel sp && sp.Children.Count >= 2 && sp.Children[1] is TextBlock tb)
                        tb.Text = "Capturing...";
                }
                // spinner animation handled via RotateTransform below
            }
            catch { }
            // Mostrar spinner dentro del botÃ³n y ocultar etiqueta
            try
            {
                if (FindName("CaptureLabel") is FrameworkElement label) label.Visibility = Visibility.Collapsed;
                if (FindName("CaptureSpinner") is FrameworkElement spin) spin.Visibility = Visibility.Visible;
                // Start single dotted ring rotation (2s linear, infinite)
                var rot = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(2))) { RepeatBehavior = RepeatBehavior.Forever };
                if (FindName("CaptureRotate") is RotateTransform r) r.BeginAnimation(RotateTransform.AngleProperty, rot);
                // no green tint per request
            }
            catch { }
            var text = _transcript.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("No hay transcripciÃ³n suficiente todavÃ­a.", "Capturar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Extraer pregunta localmente si hay clave de IA, si no, usa Ãºltima oraciÃ³n con '?'
            string q;
            var keyOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(keyOpenAi))
            {
                _questionExtractor ??= new QuestionExtractor(keyOpenAi!);
                AppendLog("OpenAI: extracting question from transcript...");
                var res = await _questionExtractor.ExtractAsync(text);
                AppendLog("OpenAI: extraction completed");
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
                this.Title = $"Overlay â€” Q: {q}";
                AppendLog("Capture: pregunta capturada");

                // Enviar a la aplicaciÃ³n web
                try
                {
                    var client = new AutoScribeClient(_apiUrl, _apiKey);
                    var sid = string.IsNullOrWhiteSpace(_sessionId) ? "local-dev-session" : _sessionId;
                    AppendLog($"Backend: POST {_apiUrl} (session={sid})");
                    await client.SendQuestionAsync(q, sid);
                    AppendLog("Backend: sent successfully");
                }
                catch (Exception ex2)
                {
                    AppendLog("Error enviando a la aplicación web");
                    AppendLog("Error enviando a la aplicación web");
                }
            }
            else
            {
                AppendLog("Capture: no se detectó una pregunta clara");
            }
        }
        catch (Exception ex)
        {
            AppendLog("Error capturando pregunta");
        }
        finally
        {
            try
            {
                if (_captureSb != null) { _captureSb.Stop(); _captureSb = null; }
                // no fill cleanup needed
                // Detener spinner y restaurar etiqueta
                try
                {
                // Stop rotation
                if (FindName("CaptureRotate") is RotateTransform rs) rs.BeginAnimation(RotateTransform.AngleProperty, null);
                    if (FindName("CaptureSpinner") is FrameworkElement spin) spin.Visibility = Visibility.Collapsed;
                    if (FindName("CaptureLabel") is FrameworkElement label) label.Visibility = Visibility.Visible;
                    // no tint fade needed
                }
                catch { }
                if (FindName("BtnCapture") is Button capBtn1)
                {
                    capBtn1.IsEnabled = true;
                    if (capBtn1.Content is StackPanel sp1 && sp1.Children.Count >= 2 && sp1.Children[1] is TextBlock tb1)
                        tb1.Text = "Capture";
                }
            }
            catch { }
        }
    }

    private static string FallbackExtractQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // Busca la Ãºltima oraciÃ³n con signo de interrogaciÃ³n
        var parts = text.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var s = parts[i].Trim();
            if (s.EndsWith("?", StringComparison.Ordinal))
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
    
    private void UpdateTranscriptUi()
    {
        try
        {
            var baseText = _transcript.ToString();
            var full = string.IsNullOrWhiteSpace(_partialLine)
                ? baseText
                : (baseText.Length > 0 ? baseText + "\n" + _partialLine : _partialLine);
            if (string.IsNullOrWhiteSpace(full))
                full = _asrOn ? "Listening..." : "Waiting for audio...";
            if (FindName("LiveTranscriptBox") is TextBlock ltb) ltb.Text = full.Trim();
            if (!string.IsNullOrWhiteSpace(_lastQuestion))
            {
                if (FindName("LastQuestionText") is TextBlock lqt) lqt.Text = _lastQuestion;
            }
        }
        catch { }
    }

    private void CopyQuestion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var q = string.IsNullOrWhiteSpace(_lastQuestion) ? (FindName("LastQuestionText") as TextBlock)?.Text ?? string.Empty : _lastQuestion;
            if (!string.IsNullOrWhiteSpace(q)) Clipboard.SetText(q);
        }
        catch { }
    }

    private void CopyLive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FindName("LiveTranscriptBox") is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                Clipboard.SetText(tb.Text);
            }
        }
        catch { }
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FindName("DebugLogBox") is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                Clipboard.SetText(tb.Text);
                AppendLog("Logs copied to clipboard");
            }
        }
        catch { }
    }

    private async void TestAzure_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? string.Empty;
            var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
            {
                AppendLog("Test Azure: missing AZURE_SPEECH_KEY/REGION");
                return;
            }
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", key);
            var url = $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken";
            var resp = await http.PostAsync(url, new System.Net.Http.StringContent(string.Empty));
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text))
                AppendLog($"Test Azure: token OK (len={text.Length})");
            else
                AppendLog($"Test Azure: failed {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            AppendLog("Test Azure error: " + ex.Message);
        }
    }

    private void OpenSetup_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try { new SetupWindow { Owner = this }.Show(); }
        catch (System.Exception ex) { AppendLog("Setup open error: " + ex.Message); }
    }

    private void Analyze_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            string clipText = string.Empty;
            try { clipText = System.Windows.Clipboard.GetText() ?? string.Empty; } catch { }
            var img = default(System.Windows.Media.Imaging.BitmapSource);
            try { img = System.Windows.Clipboard.GetImage(); } catch { }
            string imgInfo = img != null ? $"Image {img.PixelWidth}x{img.PixelHeight}" : string.Empty;
            if (string.IsNullOrWhiteSpace(clipText) && string.IsNullOrWhiteSpace(imgInfo)) { AppendLog("Analyze: clipboard empty"); return; }
            AppendLog($"Analyze: clipboard => {(string.IsNullOrWhiteSpace(clipText) ? "no text" : "text")}, {(string.IsNullOrWhiteSpace(imgInfo) ? "no image" : imgInfo)}");
        }
        catch (System.Exception ex) { AppendLog("Analyze error: " + ex.Message); }
    }

    private void FollowUp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Toggle a simple flag (can be used later when answering)
        AppendLog("Follow-up: next answer will be short (flag toggled)");
    }

    private void Regenerate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        AppendLog("Regenerate requested (stub)");
    }
}
