using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace Chispa;

/// <summary>Transcripción a demanda sin reconocedor de Windows, modelo grande, grabación ni altavoz.</summary>
internal static class DirectedVoiceChecks
{
    private sealed class PendingUtterance : IUtteranceSource
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<byte[]?> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]?> FullUtterance(RecognizedAudio? heard, DateTime started, int patienceMs = 300)
        {
            Entered.TrySetResult();
            return Pending.Task;
        }
    }

    /// <summary>Cancelación del oído sin abrir micrófono, cargar Whisper ni reproducir voz.</summary>
    internal static async Task RunCancellationChecks(Action<string, bool, string> check)
    {
        var cfg = new Config { Cerebro = "propio", UmbralComando = 0 };
        foreach (var (label, action) in new (string, Action<Ears>)[]
        {
            ("apagar la escucha", ears => ears.Listening = false),
            ("pausar por reproducción de voz", ears => ears.Paused = true),
            ("perder presencia", ears => ears.PresenceAllowed = false),
            ("recargar el reconocedor", ears => ears.Reload()),
            ("cerrar el oído", ears => ears.Dispose()),
        })
        {
            using var ears = new Ears(cfg);
            ears.Listening = true; // Sin Open(): no se solicita dispositivo de audio.
            using var pending = new CancellationTokenSource();
            ears.Interpretation = pending;
            try
            {
                action(ears);
                check($"{label} cancela una interpretación en curso", pending.IsCancellationRequested, "");
            }
            finally { ears.Interpretation = null; }
        }

        using var diagnosticEars = new Ears(cfg);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            bool stopped = false;
            try { diagnosticEars.TestWave([], ct: canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check("una prueba de voz cancelada se detiene antes de reconocer o transcribir", stopped, "");
        }

        var recognizer = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "es");
        using var voice = new SpeechSynthesizer();
        var spanish = voice.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "es");
        if (recognizer == null || spanish == null)
        {
            check("captura pendiente requiere reconocedor y voz sintética españoles instalados", false, "No se abrió micrófono ni se reprodujo audio.");
            return;
        }
        voice.SelectVoice(spanish.VoiceInfo.Name);
        using var wav = new MemoryStream();
        voice.SetOutputToAudioStream(wav, new SpeechAudioFormatInfo(Pcm.Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        voice.Speak("Salta.");
        voice.SetOutputToNull();
        using var audio = new MemoryStream(Pcm.ToWav(wav.ToArray()));
        using var engine = new SpeechRecognitionEngine(recognizer);
        var grammar = new GrammarBuilder { Culture = recognizer.Culture };
        grammar.Append(new SemanticResultKey("cmd", new SemanticResultValue("salta", "saltar")));
        engine.LoadGrammar(new Grammar(grammar) { Name = "cmd_libre" });
        engine.SetInputToWaveStream(audio);
        RecognitionResult? result = engine.Recognize(TimeSpan.FromSeconds(3));
        check("la prueba obtiene audio reconocido real sin dispositivo ni altavoz", result?.Audio != null, result?.Text ?? "sin resultado");
        if (result?.Audio == null) return;

        var source = new PendingUtterance();
        using var timeout = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        Task<Heard> work = diagnosticEars.TestInterpret(result, false, source, DateTime.UtcNow, timeout.Token);
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            timeout.CancelAfter(TimeSpan.FromMilliseconds(50));
            bool stopped = false;
            try { await work.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { stopped = true; }
            check("el límite cancela la captura pendiente sin esperar su fuente",
                stopped && !source.Pending.Task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(2), $"{clock.ElapsedMilliseconds} ms");
            source.Pending.TrySetResult(null);
            await Task.Yield();
            check("una captura tardía no revive una interpretación cancelada", work.IsCanceled, "");
        }
        finally { timeout.Cancel(); source.Pending.TrySetResult(null); }
    }

    public static int Run(TextWriter output)
    {
        var cfg = new Config { Cerebro = "propio" };
        using var ears = new Ears(cfg);
        if (!ears.CanTranscribeExplicitly) { output.WriteLine("Falta el modelo Whisper en oido."); return 1; }
        var voice = VoiceCatalog.Current(cfg);
        var render = VoiceLab.Renderer(voice);
        var body = new Diagnostics.FakeBody(output) { Quiet = true };
        var mind = new Mind(cfg, body, new Memory());
        string? originalData = Environment.GetEnvironmentVariable("CHISPA_DATOS");
        string temp = Path.Combine(Path.GetTempPath(), "chispa-oido-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CHISPA_DATOS", temp);
        int failed = 0;
        try
        {
            RunCancellationChecks((label, ok, detail) =>
            {
                if (!ok) failed++;
                output.WriteLine($"{(ok ? "ok" : "FALLA")} {label}{(detail.Length > 0 ? " · " + detail : "")}");
            }).GetAwaiter().GetResult();
            output.WriteLine($"Escucha dirigida: {voice.Menu}, sin activar Windows Speech ni el micrófono.");
            foreach (var (said, expected) in new[]
            {
                ("Abre el navegador.", "abre: https://www.google.com"),
                ("Qué hora es.", "dice: "),
                ("Más grande.", "tamaño: mas_grande"),
                ("Recuerda que mi color favorito es el azul.", "Anotado"),
            })
            {
                var (samples, rate) = render(said);
                var wav = Pcm.ToWav(VoiceLab.ToPcm16(Pcm.Resample(samples, rate, Pcm.Rate)));
                string text = ears.TestExplicitWave(wav) ?? "";
                int before = body.Did.Count;
                mind.Hear(text, HeardFrom.Named).GetAwaiter().GetResult();
                bool ok = body.Did.Skip(before).Any(d => d.Contains(expected));
                if (!ok) failed++;
                output.WriteLine($"{(ok ? "ok" : "FALLA")} «{said}» → «{text}» → {string.Join(" | ", body.Did.Skip(before))}");
            }
            string silence = ears.TestExplicitWave(Pcm.ToWav(new byte[Pcm.BytesPerSecond])) ?? "";
            if (silence.Length != 0) failed++;
            output.WriteLine($"{(silence.Length == 0 ? "ok" : "FALLA")} silencio descartado");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            TraceLog.FlushOnExit();
            Environment.SetEnvironmentVariable("CHISPA_DATOS", originalData);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            SupertonicVoice.Unload();
        }
    }
}
