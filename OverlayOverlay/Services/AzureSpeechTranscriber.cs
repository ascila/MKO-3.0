using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
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

    public event Action<string>? PartialTranscription;
    public event Action<string>? FinalTranscription;

    public AzureSpeechTranscriber(string key, string region)
    {
        _key = key;
        _region = region;
    }

    public async Task StartAsync(IWaveProvider sourceProvider, string language, CancellationToken cancellationToken = default)
    {
        if (_recognizer != null) return;

        var config = SpeechConfig.FromSubscription(_key, _region);
        config.SpeechRecognitionLanguage = string.IsNullOrWhiteSpace(language) ? "en-US" : language;

        // Azure espera PCM 16kHz mono 16-bit little-endian
        var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(format);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);
        _recognizer = new SpeechRecognizer(config, _audioConfig);

        _recognizer.Recognizing += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Result?.Text))
                PartialTranscription?.Invoke(e.Result.Text);
        };
        _recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
                FinalTranscription?.Invoke(e.Result.Text);
        };

        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

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
        _audioConfig?.Dispose();
    }
}
