using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Chispa;

/// <summary>
/// Oído fino: transcribe una grabación con Whisper, dentro del propio programa y sin internet.
/// Entiende mucho mejor que el dictado de Windows. El modelo ocupa unos cientos de MB en memoria,
/// así que se carga cuando alguien empieza a hablarle y se suelta al dormir.
/// El archivo del modelo (ggml-*.bin) va en la carpeta "oido", junto al programa.
/// </summary>
internal sealed class Transcriber : IDisposable
{
    // Frases que Whisper se inventa cuando solo hay ruido o silencio (las aprendió de subtítulos de vídeos).
    private static readonly Regex invented = new(
        @"subtitulos|amara org|gracias por ver|suscribete|suscribanse|no olvides suscribirte|hasta la proxima|^gracias$|^musica$|^aplausos$|^bueno$",
        RegexOptions.CultureInvariant);

    private readonly object gate = new();
    private readonly string hint;
    private WhisperFactory? factory;
    private WhisperProcessor? processor;
    private int processorContext;   // para cuánta duración de audio está montado el procesador actual
    private int warming, generation;
    private bool gpu = Environment.GetEnvironmentVariable("CHISPA_OIDO_GPU") != "0";
    private static readonly object runtimeGate = new();
    private static bool configured;
    private static IDisposable? nativeLogger;
    private static readonly Queue<string> nativeEvidence = new();

    internal static string RuntimeBackend => RuntimeOptions.LoadedLibrary?.ToString() ?? "sin cargar";
    internal static bool GpuBackendVerified { get; private set; }
    internal static string[] NativeEvidence { get { lock (runtimeGate) return nativeEvidence.ToArray(); } }
    internal bool IsLoaded => processor != null;
    internal long LastDurationMs { get; private set; }

    /// <summary>El modelo instalado, o null si no hay ninguno.</summary>
    public static string? ModelPath { get; } = FindModel();

    public Transcriber(Config cfg)
    {
        // Le da contexto para que escriba bien su nombre; sin órdenes, para que no se las invente si solo hay ruido.
        hint = $"Conversación en español con {cfg.Nombre}, una asistente.";
    }

    /// <summary>Carga el modelo en segundo plano para que la primera frase no espere.</summary>
    public void Warm()
    {
        if (processor != null || Interlocked.Exchange(ref warming, 1) != 0) return;
        int epoch = Volatile.Read(ref generation);
        _ = Task.Run(() =>
        {
            try
            {
                lock (gate) if (epoch == generation) Load(ContextFor(Pcm.Rate * 4));
            }
            catch (Exception ex) { Log.Write($"No pude preparar el oído fino: {ex.Message}"); }
            finally { Volatile.Write(ref warming, 0); }
        });
    }

    /// <summary>
    /// Lo dicho en una grabación WAV (mono, 16 bits). Devuelve texto vacío si no había habla
    /// y null si no pudo transcribir (entonces toca usar el dictado de Windows).
    /// </summary>
    public string? Hear(byte[] wav, CancellationToken ct = default)
    {
        using var phase = TraceLog.Measure("oido.transcribir", data: new { runtime = RuntimeBackend, bytes = wav.Length });
        try
        {
            float[] samples = Pcm.ToFloat16k(wav);
            if (!HasSignal(samples)) { phase.Mark("sin_senal"); return ""; }
            var watch = Stopwatch.StartNew();
            var text = new StringBuilder();
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                Load(ContextFor(samples.Length));
                foreach (var segment in processor!.ProcessAsync(samples, ct).ToBlockingEnumerable(ct))
                    if (segment.NoSpeechProbability < .6f) text.Append(segment.Text);
            }
            string result = Clean(text.ToString());
            LastDurationMs = watch.ElapsedMilliseconds;
            TraceLog.Event("oido.resultado", data: new { runtime = RuntimeBackend, gpu_confirmada = GpuBackendVerified, audio_ms = samples.Length * 1000L / Pcm.Rate, ms = LastDurationMs, caracteres = result.Length });
            Log.Write($"Oído fino: runtime={RuntimeBackend}, gpu_solicitada={gpu}, audio_ms={samples.Length * 1000L / Pcm.Rate}, transcripcion_ms={LastDurationMs}, caracteres={result.Length}.");
            return result;
        }
        catch (OperationCanceledException ex) { phase.Fail(ex); throw; }
        catch (Exception ex)
        {
            phase.Fail(ex);
            Log.Write($"El oído fino falló: {ex.Message}");
            return null;
        }
    }

    /// <summary>Suelta el modelo de la memoria. No espera a que termine lo que se esté transcribiendo:
    /// invalida esta generación y desmonta en segundo plano, sin congelar la interfaz.</summary>
    public void Release()
    {
        Interlocked.Increment(ref generation);
        var wasProcessor = processor;   // si una transcripción nueva monta otro, no se toca el nuevo
        _ = Task.Run(() =>
        {
            lock (gate)
            {
                if (processor != wasProcessor) return;
                processor?.Dispose();
                processor = null;
                factory?.Dispose();
                factory = null;
            }
        });
    }

    public void Dispose() => Release();

    /// <summary>
    /// Whisper está pensado para trozos de 30 segundos y tarda lo mismo con una frase de 3. Decirle que
    /// solo mire el tramo que hace falta (en unidades de 20 ms, con margen) lo acelera en proporción.
    /// CHISPA_OIDO_CTX=0 lo desactiva, para comparar.
    /// </summary>
    private static int ContextFor(int samples)
    {
        if (Environment.GetEnvironmentVariable("CHISPA_OIDO_CTX") == "0") return 1500;
        double seconds = samples / (double)Pcm.Rate;
        return seconds <= 4 ? 256 : seconds <= 8 ? 448 : seconds <= 14 ? 768 : 1500;
    }

    private void Load(int context)
    {
        if (processor != null && processorContext == context) return;
        processor?.Dispose();
        if (factory == null)
        {
            using var loadingPhase = TraceLog.Measure("oido.cargar", data: new { gpu_solicitada = gpu });
            ConfigureRuntime();
            if (ModelPath == null) throw new FileNotFoundException("No hay un modelo Whisper en la carpeta oido.");
            var loading = Stopwatch.StartNew();
            try { factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions { UseGpu = gpu }); }
            catch (Exception ex) when (gpu)
            {
                TraceLog.Failure("oido.gpu_respaldo", ex);
                gpu = false;
                Log.Write($"No pude cargar Whisper con GPU: {ex.GetType().Name}: {ex.Message}. Intento CPU.");
                factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions { UseGpu = false });
            }
            Log.Write($"Oído fino cargado: runtime={RuntimeBackend}, gpu_solicitada={gpu}, carga_ms={loading.ElapsedMilliseconds}, modelo={Path.GetFileName(ModelPath)}.");
            TraceLog.Event("oido.modelo_cargado", data: new { runtime = RuntimeBackend, gpu_solicitada = gpu, ms = loading.ElapsedMilliseconds });
        }
        // Medido en un equipo de 16 hilos: 0,59 s por frase con 6 y 0,72 s con 4. CHISPA_OIDO_HILOS lo cambia para probar.
        int threads = int.TryParse(Environment.GetEnvironmentVariable("CHISPA_OIDO_HILOS"), out int forced)
            ? Math.Clamp(forced, 1, 8)
            : Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        processor = factory!.CreateBuilder()
            .WithLanguage("es")
            .WithThreads(threads)
            .WithPrompt(hint)
            .WithNoContext()        // cada frase por separado: lo anterior no debe influir
            .WithSingleSegment()
            .WithTemperatureInc(0)  // una sola pasada: sin reintentos que doblan el tiempo
            .WithAudioContextSize(context)
            .WithGreedySamplingStrategy(greedy => greedy.WithBestOf(1))
            .Build();
        processorContext = context;
        TraceLog.Event("oido.dispositivo", data: new { runtime = RuntimeBackend, gpu_confirmada = GpuBackendVerified, contexto = context, hilos = threads });
    }

    private static void ConfigureRuntime()
    {
        lock (runtimeGate)
        {
            if (configured) return;
            RuntimeOptions.RuntimeLibraryOrder = Environment.GetEnvironmentVariable("CHISPA_OIDO_GPU") == "0"
                ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                : [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            nativeLogger = LogProvider.AddLogger((_, raw) =>
            {
                // Solo evidencia de dispositivo/asignación, nunca el texto de una conversación.
                string line = raw?.Trim() ?? "";
                bool nativeDevice = line.StartsWith("ggml_cuda_", StringComparison.Ordinal) ||
                    line.StartsWith("Device ", StringComparison.Ordinal) ||
                    line.StartsWith("whisper_", StringComparison.Ordinal) &&
                        (line.Contains("CUDA", StringComparison.OrdinalIgnoreCase) || line.Contains("GPU", StringComparison.OrdinalIgnoreCase));
                if (!nativeDevice) return;
                if (line.Contains("using CUDA", StringComparison.Ordinal)) GpuBackendVerified = true;
                if (line.Contains("no GPU found", StringComparison.Ordinal)) GpuBackendVerified = false;
                lock (runtimeGate)
                {
                    nativeEvidence.Enqueue(line.Length <= 500 ? line : line[..500]);
                    while (nativeEvidence.Count > 16) nativeEvidence.Dequeue();
                }
                Log.Write($"Whisper dispositivo: {line}");
            });
            configured = true;
        }
    }

    internal static bool HasSignal(float[] samples)
    {
        if (samples.Length < Pcm.Rate / 4) return false;
        double energy = 0;
        int audible = 0;
        foreach (float sample in samples) { energy += sample * sample; if (Math.Abs(sample) > .005) audible++; }
        return Math.Sqrt(energy / samples.Length) >= .002 && audible >= Pcm.Rate / 20;
    }

    internal static string Clean(string raw)
    {
        // Fuera las acotaciones ("[Música]", "(risas)") y los símbolos de canción.
        string text = Regex.Replace(raw, @"\[[^\]]*\]|\([^)]*\)|[♪♫]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('"', '“', '”', '«', '»').Trim();
        return invented.IsMatch(Reflexes.Normalize(text)) ? "" : text;
    }

    private static string? FindModel()
    {
        string where = Environment.GetEnvironmentVariable("CHISPA_OIDO") is { Length: > 0 } forced
            ? forced
            : Path.Combine(AppContext.BaseDirectory, "oido");
        if (File.Exists(where)) return where;
        if (!Directory.Exists(where)) return null;
        // Si hay varios, el más grande suele ser el mejor.
        return new DirectoryInfo(where).GetFiles("ggml-*.bin").OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
    }
}
