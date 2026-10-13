using System.Diagnostics;
using System.Speech.Recognition;
using System.Text;

namespace Chispa;

/// <summary>
/// Compara las voces instaladas con medidas objetivas («Chispa.exe --voces»). Si una voz suena agradable
/// solo lo puede decir una persona; lo que sí se puede medir es cuánto se le entiende (un reconocedor de
/// voz transcribe lo que dice), si es grave o aguda, lo brillante o suave del timbre y lo rápido que se genera.
/// </summary>
internal static class VoiceLab
{
    private static readonly string[] sentences =
    [
        "Hola, soy Chispa. Estoy aquí para ayudarte.",
        "El perro corre por el parque y la niña juega con la pelota.",
        "Mañana tengo una reunión importante con mi familia.",
        "¿Quieres que te cuente un chiste o prefieres escuchar música?",
        "Recuerda apagar la luz de la cocina antes de dormir.",
        "Tu color favorito es el azul y te gusta mucho el café.",
    ];

    private sealed record Row(VoiceOption Voice, double Understood, double Pitch, double Brightness, double Speed, double LoadMs, double MemoryMb);

    /// <param name="only">Si no está vacío, mide solo las voces cuyo identificador aparece (p. ej. "F5,M5").</param>
    public static int Run(TextWriter o, string only = "")
    {
        var recognizer = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "es");
        if (recognizer == null)
        {
            o.WriteLine("Hace falta el reconocimiento de voz en español de Windows para medir.");
            return 1;
        }

        var rows = new List<Row>();
        string[] wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var voice in VoiceCatalog.All())
        {
            if (wanted.Length > 0 && !wanted.Contains(voice.Id, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                using var me = Process.GetCurrentProcess();
                long before = me.PrivateMemorySize64;
                var render = Renderer(voice);
                var load = Stopwatch.StartNew();
                render("Hola.");   // la primera frase carga los modelos: se mide aparte
                double loadMs = load.ElapsedMilliseconds;
                me.Refresh();
                double memory = Math.Max(0, (me.PrivateMemorySize64 - before) / 1048576.0);

                double matched = 0, expected = 0, audioSeconds = 0, brightness = 0;
                var pitches = new List<double>();
                var clock = new Stopwatch();
                foreach (string sentence in sentences)
                {
                    clock.Start();
                    var (wav, rate) = render(sentence);
                    clock.Stop();
                    audioSeconds += wav.Length / (double)rate;

                    float[] at16 = Pcm.Resample(wav, rate, Pcm.Rate);
                    string[] said = Words(sentence), heard = Words(Transcribe(recognizer, at16));
                    matched += CommonInOrder(said, heard);
                    expected += said.Length;
                    var (pitch, bright) = Timbre(at16);
                    if (pitch > 0) pitches.Add(pitch);
                    brightness += bright / sentences.Length;
                }
                pitches.Sort();
                rows.Add(new Row(voice, matched / expected, pitches.Count > 0 ? pitches[pitches.Count / 2] : 0, brightness,
                                 audioSeconds / Math.Max(0.001, clock.Elapsed.TotalSeconds), loadMs, memory));
            }
            catch (Exception ex) { o.WriteLine($"{voice.Menu}: no se pudo medir ({ex.Message})"); }
            finally { SupertonicVoice.Unload(); }
        }

        o.WriteLine($"{"Voz",-34} {"Se entiende",11} {"Tono",8} {"Brillo",7} {"Rapidez",9} {"Carga",8} {"Memoria",8}");
        foreach (var r in rows.OrderByDescending(r => r.Understood).ThenBy(r => r.Brightness))
            o.WriteLine($"{r.Voice.Menu,-34} {r.Understood,10:P0} {r.Pitch,5:F0} Hz {r.Brightness,7:F3} {r.Speed,8:F1}x {r.LoadMs,5:F0} ms {r.MemoryMb,5:F0} MB");
        o.WriteLine();
        o.WriteLine("Se entiende: palabras que acierta el reconocedor de Windows. Tono: más Hz = más aguda.");
        o.WriteLine("Brillo: más bajo = timbre más suave. Rapidez: segundos de voz por segundo de cálculo.");
        o.WriteLine("Orden de claridad: " + string.Join(", ", rows.OrderByDescending(r => r.Understood).ThenBy(r => r.Brightness).Select(r => r.Voice.Id)));
        return 0;
    }

    /// <summary>Cómo generar una frase con cada tipo de voz, sin reproducirla.</summary>
    internal static Func<string, (float[] Wav, int Rate)> Renderer(VoiceOption voice)
    {
        var cfg = new Config { Voz = voice.Id };
        if (int.TryParse(Environment.GetEnvironmentVariable("CHISPA_VOZ_PASOS"), out int steps)) cfg.PasosVoz = steps;   // para comparar ajustes
        switch (voice.Kind)
        {
            case "supertonic":
            {
                var engine = new SupertonicVoice(cfg, voice.Id);
                return text => (engine.Render(text), engine.SampleRate);
            }
            case "piper":
            {
                var engine = PiperVoice.Open(cfg, voice.Id) ?? throw new InvalidOperationException("no se pudo abrir");
                return text => (ToFloat(engine.Render(text)), engine.SampleRate);
            }
            default:
            {
                var mouth = new Mouth(cfg);
                return text => (ToFloat(mouth.SynthesizeWav(text).AsSpan(Pcm.WavHeader)), Pcm.Rate);
            }
        }
    }

    private static string Transcribe(RecognizerInfo recognizer, float[] at16)
    {
        // Un poco de silencio antes y después, como en una grabación real.
        var padded = new float[at16.Length + Pcm.Rate];
        at16.CopyTo(padded, Pcm.Rate / 3);
        byte[] pcm = ToPcm16(padded);

        using var engine = new SpeechRecognitionEngine(recognizer);
        engine.LoadGrammar(new DictationGrammar());
        var text = new StringBuilder();
        using var done = new ManualResetEventSlim();
        engine.SpeechRecognized += (_, e) => text.Append(e.Result.Text).Append(' ');
        engine.RecognizeCompleted += (_, _) => done.Set();
        engine.SetInputToWaveStream(new MemoryStream(Pcm.ToWav(pcm)));
        engine.RecognizeAsync(RecognizeMode.Multiple);
        done.Wait(TimeSpan.FromSeconds(40));
        return text.ToString();
    }

    private static string[] Words(string text) => Reflexes.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Cuántas palabras de lo dicho aparecen, en el mismo orden, en lo oído.</summary>
    private static int CommonInOrder(string[] said, string[] heard)
    {
        var best = new int[said.Length + 1, heard.Length + 1];
        for (int i = 1; i <= said.Length; i++)
            for (int j = 1; j <= heard.Length; j++)
                best[i, j] = said[i - 1] == heard[j - 1] ? best[i - 1, j - 1] + 1 : Math.Max(best[i - 1, j], best[i, j - 1]);
        return best[said.Length, heard.Length];
    }

    /// <summary>Tono medio de la voz (Hz) y brillo del timbre, medidos en los tramos con voz.</summary>
    private static (double Pitch, double Brightness) Timbre(float[] at16)
    {
        const int frame = 640, hop = 320;   // 40 ms, cada 20 ms
        double loudest = 0;
        for (int start = 0; start + frame <= at16.Length; start += hop) loudest = Math.Max(loudest, Energy(at16, start, frame));

        var pitches = new List<double>();
        double high = 0, all = 0;
        for (int start = 0; start + frame <= at16.Length; start += hop)
        {
            double energy = Energy(at16, start, frame);
            if (energy < loudest * 0.05) continue;   // silencio o casi

            for (int i = start + 1; i < start + frame; i++)
            {
                double step = at16[i] - at16[i - 1];
                high += step * step;
            }
            all += energy;

            // Tono: el desfase (entre 70 y 400 Hz) con el que la onda más se parece a sí misma.
            double bestScore = 0.45;
            int bestLag = 0;
            for (int lag = Pcm.Rate / 400; lag <= Pcm.Rate / 70; lag++)
            {
                double sum = 0;
                for (int i = start; i + lag < start + frame; i++) sum += at16[i] * at16[i + lag];
                double score = sum / energy;
                if (score > bestScore) (bestScore, bestLag) = (score, lag);
            }
            if (bestLag > 0) pitches.Add((double)Pcm.Rate / bestLag);
        }
        pitches.Sort();
        return (pitches.Count > 0 ? pitches[pitches.Count / 2] : 0, all > 0 ? high / all : 0);

        static double Energy(float[] wav, int start, int count)
        {
            double sum = 0;
            for (int i = start; i < start + count; i++) sum += wav[i] * wav[i];
            return sum;
        }
    }

    /// <summary>
    /// Compara los dos oídos («Chispa.exe --oido»): varias voces dicen frases como las que diría el usuario
    /// y se mide cuántas palabras acierta el dictado de Windows y cuántas el oído fino (Whisper).
    /// </summary>
    public static int RunEars(TextWriter o)
    {
        var recognizer = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "es");
        if (recognizer == null || Transcriber.ModelPath == null)
        {
            o.WriteLine("Hacen falta el reconocimiento de voz en español de Windows y el modelo de la carpeta «oido».");
            return 1;
        }
        string[] spoken =
        [
            "¿Qué tiempo hace hoy en Santo Domingo?",
            "Recuérdame en un rato que saque la basura.",
            "¿Quién escribió Don Quijote de la Mancha?",
            "Abre el navegador y busca recetas de arroz con pollo.",
            "Mi color favorito es el verde y me gusta mucho el café.",
            "Sube el volumen un poco, por favor.",
            "Cuéntame un chiste que no sea muy malo.",
            "Quiero que abras el navegador y luego me digas qué hora es.",
        ];
        var speakers = VoiceCatalog.All().Where(v => v.Id is "F5" or "M5" or "F2" or "M2").ToList();
        if (speakers.Count == 0) speakers = VoiceCatalog.All().TakeLast(1).ToList();

        using var fine = new Transcriber(new Config());
        var loading = Stopwatch.StartNew();
        fine.Hear(Pcm.ToWav(new byte[Pcm.BytesPerSecond]));   // carga el modelo fuera de la medida
        o.WriteLine($"Carga del oído fino: {loading.ElapsedMilliseconds} ms");
        bool brief = Environment.GetEnvironmentVariable("CHISPA_OIDO_BREVE") == "1";   // solo los totales, para comparar ajustes
        double windowsHit = 0, fineHit = 0, total = 0, windowsMs = 0, fineMs = 0, seconds = 0;
        foreach (var speaker in speakers)
        {
            var render = Renderer(speaker);
            foreach (string sentence in spoken)
            {
                var (wav, rate) = render(sentence);
                float[] at16 = Pcm.Resample(wav, rate, Pcm.Rate);
                seconds += at16.Length / (double)Pcm.Rate;
                string[] said = Words(sentence);
                total += said.Length;

                var clock = Stopwatch.StartNew();
                string byWindows = Transcribe(recognizer, at16);
                windowsMs += clock.ElapsedMilliseconds;
                clock.Restart();
                string byFine = fine.Hear(Pcm.ToWav(ToPcm16(at16))) ?? "";
                fineMs += clock.ElapsedMilliseconds;

                int a = CommonInOrder(said, Words(byWindows)), b = CommonInOrder(said, Words(byFine));
                windowsHit += a;
                fineHit += b;
                if (!brief && (a < said.Length || b < said.Length))
                {
                    o.WriteLine($"[{speaker.Menu}] {sentence}");
                    o.WriteLine($"     Windows ({a}/{said.Length}): {byWindows.Trim()}");
                    o.WriteLine($"     Fino    ({b}/{said.Length}): {byFine}");
                }
            }
            SupertonicVoice.Unload();
        }
        int count = speakers.Count * spoken.Length;
        o.WriteLine();
        o.WriteLine($"{count} frases, {seconds:F0} segundos de voz, dichas por: {string.Join(", ", speakers.Select(s => s.Menu))}");
        o.WriteLine($"Dictado de Windows: acierta el {windowsHit / total:P0} de las palabras, {windowsMs / count:F0} ms por frase");
        o.WriteLine($"Oído fino (Whisper): acierta el {fineHit / total:P0} de las palabras, {fineMs / count:F0} ms por frase");
        using var me = Process.GetCurrentProcess();
        o.WriteLine($"Memoria del proceso con el oído fino cargado: {me.PrivateMemorySize64 / 1048576.0:F0} MB");
        return 0;
    }

    internal static byte[] ToPcm16(float[] wav)
    {
        byte[] pcm = new byte[wav.Length * 2];
        for (int i = 0; i < wav.Length; i++)
        {
            short sample = (short)Math.Clamp(wav[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    private static float[] ToFloat(ReadOnlySpan<byte> pcm16)
    {
        var wav = new float[pcm16.Length / 2];
        for (int i = 0; i < wav.Length; i++) wav[i] = (short)(pcm16[i * 2] | pcm16[i * 2 + 1] << 8) / 32768f;
        return wav;
    }
}
