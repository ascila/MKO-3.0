using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OverlayOverlay.Services;

public class AzureSpeechTranscriber : IDisposable
{
    private readonly string _key;
    private readonly string _region;
    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private CancellationTokenSource? _cts;
    private DateTime? _speechStartUtc;
    private DateTime? _speechEndUtc;
    private bool _firstPartialReported;
    private string _lastPartial = string.Empty;
    private string _lastFinalEmitted = string.Empty;
    private System.Timers.Timer? _partialDebounce;
    private const int PartialDebounceMs = 600; // finalize after inactivity

    public event Action<string>? PartialTranscription;
    public event Action<string>? FinalTranscription;
    public event Action<string>? DebugLog;

    public AzureSpeechTranscriber(string key, string region)
    {
        _key = key;
        _region = region;
    }

    // language: preferred BCP-47 (e.g., "en-US" or "es-ES").
    // Source language is auto-detected between EN/ES for robustness in interviews.
    public async Task StartAsync(IWaveProvider sourceProvider, string language, CancellationToken cancellationToken = default)
    {
        if (_recognizer != null) return;

        // Target language normalization for translation map (Azure expects BCP-47 but translations dictionary uses short codes)
        var targetLangBcp = string.IsNullOrWhiteSpace(language) ? "en-US" : language;
        var targetShort = MapToShortLanguage(targetLangBcp); // "en" or "es"

        // Azure Speech config (standard transcription)
        var sconfig = SpeechConfig.FromSubscription(_key, _region);
        sconfig.SpeechRecognitionLanguage = "en-US"; // overridden below by 'recogLang'
        // Optional: tweak silence timeouts for responsiveness
        sconfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "1500");
        sconfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "250");
        // Ask SDK to deliver partials as soon as possible
        try { sconfig.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "1"); } catch { }
        // Temporarily disable dictation/segmentation tweaks while we stabilize
        // try { sconfig.EnableDictation(); } catch { }
        // try { sconfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "800"); } catch { }
        try
        {
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "azure-speech-sdk.log");
            sconfig.SetProperty(PropertyId.Speech_LogFilename, logPath);
            DebugLog?.Invoke($"Azure SDK log: {logPath}");
        }
        catch { }

        // Force recognition language if provided; default to en-US
        var recogLang = string.IsNullOrWhiteSpace(language) ? "en-US" : language;
        sconfig.SpeechRecognitionLanguage = recogLang;

        // Azure espera PCM 16kHz mono 16-bit little-endian
        var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(format);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);

        _recognizer = new SpeechRecognizer(sconfig, _audioConfig);

        _recognizer.Recognizing += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizingSpeech)
            {
                var text = e.Result.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    PartialTranscription?.Invoke(text);
                    _lastPartial = text;
                    // Debounce to produce a client-side final if no updates for a short while
                    try
                    {
                        _partialDebounce ??= new System.Timers.Timer(PartialDebounceMs) { AutoReset = false };
                        _partialDebounce.Stop();
                        _partialDebounce.Interval = PartialDebounceMs;
                        _partialDebounce.Elapsed -= OnPartialDebounceElapsed;
                        _partialDebounce.Elapsed += OnPartialDebounceElapsed;
                        _partialDebounce.Start();
                    }
                    catch { }
                    if (!_firstPartialReported && _speechStartUtc.HasValue)
                    {
                        var ms = (int)(DateTime.UtcNow - _speechStartUtc.Value).TotalMilliseconds;
                        DebugLog?.Invoke($"First partial in ~{ms} ms");
                        _firstPartialReported = true;
                    }
                }
                DebugLog?.Invoke($"Recognizing: '{(text?.Length > 60 ? text.Substring(0,60)+"..." : text)}'");
            }
            else
            {
                DebugLog?.Invoke($"Recognizing event reason: {e.Result.Reason}");
            }
        };
        _recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech)
            {
                var text = e.Result.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!string.Equals(text, _lastFinalEmitted, StringComparison.Ordinal))
                    {
                        _lastFinalEmitted = text;
                        FinalTranscription?.Invoke(text);
                    }
                }
                var now = DateTime.UtcNow;
                var fromStart = _speechStartUtc.HasValue ? (int)(now - _speechStartUtc.Value).TotalMilliseconds : (int?)null;
                var fromEnd = _speechEndUtc.HasValue ? (int)(now - _speechEndUtc.Value).TotalMilliseconds : (int?)null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    DebugLog?.Invoke($"Recognized: '{(text?.Length > 60 ? text.Substring(0,60)+"..." : text)}'" +
                        (fromStart.HasValue ? $" | latency_start={fromStart}ms" : string.Empty) +
                        (fromEnd.HasValue ? $" latency_end={fromEnd}ms" : string.Empty));
                }
            }
            else if (e.Result.Reason == ResultReason.NoMatch)
            {
                var details = NoMatchDetails.FromResult(e.Result);
                DebugLog?.Invoke($"NoMatch: {details.Reason}");
            }
        };
        _recognizer.Canceled += (_, e) =>
        {
            DebugLog?.Invoke($"Canceled: {e.Reason} {(string.IsNullOrWhiteSpace(e.ErrorDetails)?"":"- "+e.ErrorDetails)}");
        };
        _recognizer.SessionStarted += (_, __) => DebugLog?.Invoke("SessionStarted");
        _recognizer.SessionStopped += (_, __) => DebugLog?.Invoke("SessionStopped");
        _recognizer.SpeechStartDetected += (_, __) => { _speechStartUtc = DateTime.UtcNow; _speechEndUtc = null; _firstPartialReported = false; _lastPartial = string.Empty; DebugLog?.Invoke("SpeechStartDetected"); };
        _recognizer.SpeechEndDetected += (_, __) => { _speechEndUtc = DateTime.UtcNow; DebugLog?.Invoke("SpeechEndDetected"); };

        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => PumpAudioAsync(sourceProvider, _pushStream!, _cts.Token));
    }

    private async Task PumpAudioAsync(IWaveProvider sourceProvider, PushAudioInputStream pushStream, CancellationToken ct)
    {
        // Convertir sourceProvider a 16kHz mono 16-bit usando chain de SampleProviders
        var sourceWaveFormat = sourceProvider.WaveFormat;
        // Build processing chain with gentle gain reduction to avoid hot input
        ISampleProvider sample = sourceProvider.ToSampleProvider();
        if (sourceWaveFormat.Channels == 2)
        {
            sample = new StereoToMonoSampleProvider(sample) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        // Keep unity gain to avoid too-low levels to Azure
        var trimmed = new VolumeSampleProvider(sample) { Volume = 1.0f };
        var resampled = new WdlResamplingSampleProvider(trimmed, 16000);
        var wave16 = new SampleToWaveProvider16(resampled);

        var buffer = new byte[640]; // ~20ms @ 16kHz mono 16-bit = 640 bytes
        var bytesThisSecond = 0;
        double rmsAccum = 0;
        int rmsCount = 0;
        var lastTick = DateTime.UtcNow;
        const int bytesPerSecond = 16000 * 2; // PCM16 mono
        while (!ct.IsCancellationRequested)
        {
            int read = wave16.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                pushStream.Write(buffer, read);
                // Compute RMS for diagnostics
                for (int i = 0; i < read; i += 2)
                {
                    short s = BitConverter.ToInt16(buffer, i);
                    double norm = s / 32768.0;
                    rmsAccum += norm * norm;
                    rmsCount++;
                }
                bytesThisSecond += read;
                // Pace according to audio duration written
                int delayMs = (int)Math.Round((read * 1000.0) / bytesPerSecond);
                if (delayMs > 0)
                {
                    try { await Task.Delay(delayMs, ct).ConfigureAwait(false); } catch { }
                }
            }
            else
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
            var now = DateTime.UtcNow;
            if ((now - lastTick).TotalSeconds >= 1.0)
            {
                double rms = rmsCount > 0 ? Math.Sqrt(rmsAccum / rmsCount) : 0.0;
                double dbfs = rms > 0 ? 20.0 * Math.Log10(rms) : -120.0;
                DebugLog?.Invoke($"Audio pump: {bytesThisSecond} B/s to Azure, RMS {dbfs:F1} dBFS");
                bytesThisSecond = 0;
                rmsAccum = 0;
                rmsCount = 0;
                lastTick = now;
            }
        }
        pushStream.Close();
    }

    private void OnPartialDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            var text = _lastPartial?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, _lastFinalEmitted, StringComparison.Ordinal))
            {
                _lastFinalEmitted = text;
                DebugLog?.Invoke($"ClientFinalized after {PartialDebounceMs}ms inactivity");
                FinalTranscription?.Invoke(text);
            }
        }
        catch { }
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_partialDebounce != null) { _partialDebounce.Stop(); _partialDebounce.Dispose(); _partialDebounce = null; } } catch { }
        if (_recognizer != null)
        {
            try { await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { }
            _recognizer.Dispose();
            _recognizer = null;
        }
        _audioConfig?.Dispose();
        _audioConfig = null;
        _pushStream = null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        try { _partialDebounce?.Stop(); } catch { }
        _partialDebounce?.Dispose();
        _recognizer?.Dispose();
        _audioConfig?.Dispose();
    }

    private static string MapToShortLanguage(string bcp47)
    {
        if (string.IsNullOrWhiteSpace(bcp47)) return "en";
        var s = bcp47.Trim().ToLowerInvariant();
        if (s.StartsWith("es")) return "es";
        if (s.StartsWith("en")) return "en";
        return s.Length >= 2 ? s.Substring(0, 2) : "en";
    }
}
