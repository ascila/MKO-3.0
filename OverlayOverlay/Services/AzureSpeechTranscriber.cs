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
    private TranslationRecognizer? _translator;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private CancellationTokenSource? _cts;

    public event Action<string>? PartialTranscription;
    public event Action<string>? FinalTranscription;

    public AzureSpeechTranscriber(string key, string region)
    {
        _key = key;
        _region = region;
    }

    // language: target language for translation (e.g., "en-US" or "es-ES").
    // Source language is auto-detected between EN/ES for robustness in interviews.
    public async Task StartAsync(IWaveProvider sourceProvider, string language, CancellationToken cancellationToken = default)
    {
        if (_recognizer != null || _translator != null) return;

        // Target language normalization for translation map (Azure expects BCP-47 but translations dictionary uses short codes)
        var targetLangBcp = string.IsNullOrWhiteSpace(language) ? "en-US" : language;
        var targetShort = MapToShortLanguage(targetLangBcp); // "en" or "es"

        // Azure Speech Translation config
        var tconfig = SpeechTranslationConfig.FromSubscription(_key, _region);
        tconfig.SpeechRecognitionLanguage = "en-US"; // placeholder; actual source auto-detect below
        tconfig.AddTargetLanguage(targetShort);
        // Optional: tweak silence timeouts for responsiveness
        tconfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "2000");
        tconfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "500");

        // Auto-detect source between EN/ES (extendable)
        var autoDetect = AutoDetectSourceLanguageConfig.FromLanguages(new[] { "en-US", "es-ES" });

        // Azure espera PCM 16kHz mono 16-bit little-endian
        var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(format);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);

        _translator = new TranslationRecognizer(tconfig, autoDetect, _audioConfig);

        _translator.Recognizing += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.TranslatingSpeech)
            {
                if (e.Result.Translations.TryGetValue(targetShort, out var text) && !string.IsNullOrWhiteSpace(text))
                    PartialTranscription?.Invoke(text);
            }
        };
        _translator.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.TranslatedSpeech)
            {
                if (e.Result.Translations.TryGetValue(targetShort, out var text) && !string.IsNullOrWhiteSpace(text))
                    FinalTranscription?.Invoke(text);
            }
        };

        await _translator.StartContinuousRecognitionAsync().ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => PumpAudioAsync(sourceProvider, _pushStream!, _cts.Token));
    }

    private static async Task PumpAudioAsync(IWaveProvider sourceProvider, PushAudioInputStream pushStream, CancellationToken ct)
    {
        // Convertir sourceProvider a 16kHz mono 16-bit usando chain de SampleProviders
        var sourceWaveFormat = sourceProvider.WaveFormat;
        ISampleProvider sample = sourceProvider.ToSampleProvider();
        if (sourceWaveFormat.Channels == 2)
        {
            sample = new StereoToMonoSampleProvider(sample) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        var resampled = new WdlResamplingSampleProvider(sample, 16000);
        var wave16 = new SampleToWaveProvider16(resampled);

        var buffer = new byte[3200]; // 100ms @ 16kHz mono 16-bit = 3200 bytes
        while (!ct.IsCancellationRequested)
        {
            int read = wave16.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                pushStream.Write(buffer, read);
            }
            else
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        }
        pushStream.Close();
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_translator != null)
        {
            try { await _translator.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { }
            _translator.Dispose();
            _translator = null;
        }
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
        _recognizer?.Dispose();
        _translator?.Dispose();
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
