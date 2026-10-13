using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Chispa;

/// <summary>
/// Voz neuronal Supertonic: la más natural de las tres, con diez voces (F1–F5 de mujer, M1–M5 de hombre).
/// Funciona dentro del propio programa. Sus modelos pesan unos 400 MB, así que se cargan al decir la
/// primera frase y se sueltan al dormir; las frases muy cortas ("¿Sí?") se guardan ya dichas para
/// contestar al instante sin cargar nada.
/// Archivos en voz\supertonic (ver <see cref="VoiceCatalog.Folder"/>): los modelos en "onnx" y las voces en "voice_styles".
/// </summary>
internal sealed class SupertonicVoice : INaturalVoice
{
    private const int FirstChunk = 90;        // letras del primer trozo: corto, para empezar a sonar cuanto antes
    private const int MaxChunk = 220;         // letras de los siguientes, que se generan mientras suena el anterior
    private const int CacheUpTo = 28;         // frases de hasta estas letras se guardan ya dichas

    /// <summary>Las cuatro redes y sus tablas. Son comunes a todas las voces.</summary>
    private sealed class Models : IDisposable
    {
        public readonly InferenceSession Duration, Encoder, Estimator, Vocoder;
        public readonly int[] Indexer;
        public readonly int SampleRate = 44100, BaseChunk = 512, Compress = 6, LatentDim = 24;

        public Models(string dir)
        {
            using var loadPhase = TraceLog.Measure("voz.cargar", data: new { provider = "CPU" });
            // Pocos hilos: medido en un equipo de 16, con 2–4 genera voz a ~5 veces el tiempo real y con 12 baja a 2,5.
            // CHISPA_VOZ_HILOS lo cambia para probar.
            int threads = int.TryParse(Environment.GetEnvironmentVariable("CHISPA_VOZ_HILOS"), out int forced)
                ? Math.Clamp(forced, 1, 4)
                : Environment.ProcessorCount >= 8 ? 4 : 2;
            using var options = new SessionOptions
            {
                IntraOpNumThreads = threads,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            // Sin esto, los hilos de cálculo se quedan dando vueltas a plena CPU entre frase y frase.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            var loading = Stopwatch.StartNew();
            var allocated = new List<InferenceSession>();
            InferenceSession Open(string file)
            {
                var session = new InferenceSession(Path.Combine(dir, file), options);
                allocated.Add(session);
                return session;
            }
            try
            {
                Duration = Open("duration_predictor.onnx");
                Encoder = Open("text_encoder.onnx");
                Estimator = Open("vector_estimator.onnx");
                Vocoder = Open("vocoder.onnx");
                Indexer = JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(dir, "unicode_indexer.json"))) ?? [];

                using var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tts.json")));
                if (cfg.RootElement.TryGetProperty("ae", out var ae))
                {
                    if (ae.TryGetProperty("sample_rate", out var sr)) SampleRate = sr.GetInt32();
                    if (ae.TryGetProperty("base_chunk_size", out var bc)) BaseChunk = bc.GetInt32();
                }
                if (cfg.RootElement.TryGetProperty("ttl", out var ttl))
                {
                    if (ttl.TryGetProperty("chunk_compress_factor", out var cf)) Compress = cf.GetInt32();
                    if (ttl.TryGetProperty("latent_dim", out var ld)) LatentDim = ld.GetInt32();
                }
                Log.Write($"Voz Supertonic: proveedor=CPU, hilos={threads}, carga_ms={loading.ElapsedMilliseconds}.");
            }
            catch (Exception ex)
            {
                foreach (var session in allocated) session.Dispose();
                loadPhase.Fail(ex);
                throw;
            }
        }

        public void Dispose()
        {
            Duration.Dispose();
            Encoder.Dispose();
            Estimator.Dispose();
            Vocoder.Dispose();
        }
    }

    /// <summary>El timbre de una voz: dos tablas de números que acompañan al texto.</summary>
    private sealed record Style(float[] Ttl, int[] TtlDims, float[] Dp, int[] DpDims);

    private sealed class Session
    {
        public required StreamPlayer Player;
        public readonly CancellationTokenSource Cancel = new();
    }

    private static readonly object gate = new();
    private static Models? shared;
    private static int warming, generation;

    private readonly Config cfg;
    private readonly string styleName;
    private Style? style;
    private Session? current;

    public event Action? Finished;
    public event Action<string>? Failed;

    public bool Speaking => current != null;
    public long LastStartMs { get; private set; }
    public int SampleRate => 44100;

    public static string Folder => Path.Combine(VoiceCatalog.Folder, "supertonic");

    /// <summary>Voces instaladas ("F1"… "M5"), o ninguna si falta el motor.</summary>
    public static string[] Styles
    {
        get
        {
            string styles = Path.Combine(Folder, "voice_styles");
            if (!File.Exists(Path.Combine(Folder, "onnx", "vector_estimator.onnx")) || !Directory.Exists(styles)) return [];
            return Directory.GetFiles(styles, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToArray();
        }
    }

    public SupertonicVoice(Config cfg, string styleName)
    {
        this.cfg = cfg;
        this.styleName = styleName;
    }

    public bool Say(string text)
    {
        Stop();
        try
        {
            double pitch = Math.Clamp(cfg.TonoVoz, 0.7, 1.5);
            // El tono sube reproduciendo más deprisa; se compensa generando más despacio.
            double speed = Math.Clamp((1.0 + 0.04 * cfg.VelocidadVoz) / pitch, 0.7, 1.6);
            var player = new StreamPlayer((int)(SampleRate * pitch));
            var session = current = new Session { Player = player };
            var ui = SynchronizationContext.Current;
            var clock = Stopwatch.StartNew();
            player.Finished += () => Post(ui, () =>
            {
                if (current != session) return;   // la cortaron para decir otra cosa
                current = null;
                session.Cancel.Dispose();
                Finished?.Invoke();
            });

            // La caché de frases dichas guarda en disco lo que ella contesta: solo si el usuario la activa (Config.CacheVoz).
            string? cached = cfg.CacheVoz && text.Length <= CacheUpTo ? CachePath(text, speed) : null;
            if (cached != null && File.Exists(cached))
            {
                byte[] pcm = File.ReadAllBytes(cached);
                LastStartMs = clock.ElapsedMilliseconds;
                player.Add(pcm, pcm.Length);
                player.Complete();
                return true;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    using var whole = new MemoryStream();
                    foreach (float[] wav in RenderChunks(text, speed, session.Cancel.Token))
                    {
                        byte[] pcm = ToPcm16(wav);
                        if (session.Cancel.IsCancellationRequested) return;
                        if (!player.Started) LastStartMs = clock.ElapsedMilliseconds;
                        player.Add(pcm, pcm.Length);
                        whole.Write(pcm);
                    }
                    if (!player.Started) throw new InvalidOperationException("no produjo sonido");
                    player.Complete();
                    if (cached != null) SaveToCache(cached, whole.ToArray());
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Write($"La voz Supertonic no pudo hablar: {ex.Message}");
                    Post(ui, () =>
                    {
                        if (current != session) return;
                        current = null;
                        player.Dispose();
                        session.Cancel.Dispose();
                        Failed?.Invoke(text);
                    });
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"La voz Supertonic no pudo arrancar: {ex.Message}");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (current is not { } session) return;
        current = null;
        session.Cancel.Cancel();
        session.Cancel.Dispose();
        session.Player.Dispose();
    }

    public void Release() => Unload();

    /// <summary>
    /// Carga los modelos en segundo plano (unos 2 segundos) para que la primera respuesta no se haga esperar.
    /// Se llama cuando el usuario empieza a hablarle.
    /// </summary>
    public void Warm()
    {
        if (shared != null || Interlocked.Exchange(ref warming, 1) != 0) return;
        int epoch = Volatile.Read(ref generation);
        _ = Task.Run(() =>
        {
            try
            {
                lock (gate)
                {
                    if (shared != null || epoch != generation) return;
                    shared = new Models(Path.Combine(Folder, "onnx"));
                    style ??= LoadStyle(Path.Combine(Folder, "voice_styles", styleName + ".json"));
                    Synthesize(shared, style, "Hola.", 1.0, 2, CancellationToken.None);   // la primera pasada es la más lenta
                }
            }
            catch (Exception ex) { Log.Write($"No pude preparar la voz Supertonic: {ex.Message}"); }
            finally { Volatile.Write(ref warming, 0); }
        });
    }

    /// <summary>Suelta los modelos (unos 400 MB). La próxima frase que no esté ya dicha los vuelve a cargar.
    /// No espera a que termine lo que se esté generando: lo desmonta en segundo plano, sin congelar la interfaz.</summary>
    public static void Unload()
    {
        Interlocked.Increment(ref generation);
        _ = Task.Run(() =>
        {
            lock (gate)   // espera, fuera del hilo de la interfaz, a que acabe la frase que se esté generando
            {
                shared?.Dispose();
                shared = null;
            }
        });
    }

    /// <summary>Diagnóstico: genera el texto entero y devuelve el sonido (44,1 kHz) sin reproducirlo.</summary>
    public float[] Render(string text, double speed = 1.04) =>
        RenderChunks(text, speed, CancellationToken.None).SelectMany(w => w).ToArray();

    // ---------- generación ----------

    /// <summary>Genera el texto trozo a trozo, para poder empezar a sonar antes de terminar.</summary>
    private IEnumerable<float[]> RenderChunks(string text, double speed, CancellationToken ct)
    {
        bool first = true;
        foreach (string chunk in Split(text))
        {
            ct.ThrowIfCancellationRequested();
            float[] wav;
            lock (gate)   // una sola frase a la vez usa los modelos
            {
                using var generationPhase = TraceLog.Measure("voz.sintetizar", data: new { provider = "CPU", caracteres = chunk.Length, pasos = Math.Clamp(cfg.PasosVoz, 2, 16) });
                shared ??= new Models(Path.Combine(Folder, "onnx"));
                style ??= LoadStyle(Path.Combine(Folder, "voice_styles", styleName + ".json"));
                wav = Synthesize(shared, style, chunk, speed, Math.Clamp(cfg.PasosVoz, 2, 16), ct);
            }
            if (!first) yield return new float[(int)(0.25 * SampleRate)];   // respiro entre trozos
            first = false;
            yield return wav;
        }
    }

    /// <param name="steps">Pasos de refinado: más pasos, algo más de calidad y más tiempo.</param>
    private static float[] Synthesize(Models m, Style s, string text, double speed, int steps, CancellationToken ct)
    {
        long[] ids = Encode(m, Prepare(text));
        if (ids.Length == 0) return [];
        int length = ids.Length;

        var textIds = new DenseTensor<long>(ids, [1, length]);
        var textMask = Ones(1, 1, length);
        var styleTtl = new DenseTensor<float>(s.Ttl, [1, s.TtlDims[1], s.TtlDims[2]]);
        var styleDp = new DenseTensor<float>(s.Dp, [1, s.DpDims[1], s.DpDims[2]]);

        // 1. Cuánto dura la frase.
        float duration;
        using (var result = m.Duration.Run(
        [
            NamedOnnxValue.CreateFromTensor("text_ids", textIds),
            NamedOnnxValue.CreateFromTensor("style_dp", styleDp),
            NamedOnnxValue.CreateFromTensor("text_mask", textMask),
        ]))
            duration = result[0].AsEnumerable<float>().First() / (float)speed;

        // 2. El texto, convertido en lo que la red de sonido entiende.
        DenseTensor<float> textEmb;
        using (var result = m.Encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("text_ids", textIds),
            NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
            NamedOnnxValue.CreateFromTensor("text_mask", textMask),
        ]))
            textEmb = result[0].AsTensor<float>().ToDenseTensor();

        // 3. Parte de ruido y lo refina paso a paso hasta que es voz.
        int frame = m.BaseChunk * m.Compress;
        int frames = Math.Max(1, (int)Math.Ceiling(duration * m.SampleRate / frame));
        int channels = m.LatentDim * m.Compress;
        var latent = Noise(channels, frames);
        var latentMask = Ones(1, 1, frames);
        var total = new DenseTensor<float>(new float[] { steps }, [1]);
        for (int step = 0; step < steps; step++)
        {
            ct.ThrowIfCancellationRequested();
            using var result = m.Estimator.Run(
            [
                NamedOnnxValue.CreateFromTensor("noisy_latent", latent),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmb),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
                NamedOnnxValue.CreateFromTensor("text_mask", textMask),
                NamedOnnxValue.CreateFromTensor("latent_mask", latentMask),
                NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(new float[] { step }, [1])),
                NamedOnnxValue.CreateFromTensor("total_step", total),
            ]);
            latent = result[0].AsTensor<float>().ToDenseTensor();
        }

        // 4. Lo convierte en onda de sonido y recorta lo que sobra del último bloque.
        using var audio = m.Vocoder.Run([NamedOnnxValue.CreateFromTensor("latent", latent)]);
        float[] wav = audio[0].AsEnumerable<float>().ToArray();
        int keep = Math.Min(wav.Length, (int)(duration * m.SampleRate));
        return keep > 0 && keep < wav.Length ? wav[..keep] : wav;
    }

    /// <summary>Deja el texto como lo espera el modelo: limpio, con punto final y marcado como español.</summary>
    private static string Prepare(string text)
    {
        string s = text.Normalize(NormalizationForm.FormKD);
        s = Regex.Replace(s, @"[☀-➿]|\p{Cs}", "");                 // símbolos y emojis
        s = Regex.Replace(s, "[–‑—]", "-").Replace('_', ' ');
        s = Regex.Replace(s, "[“”«»]", "\"");
        s = Regex.Replace(s, "[‘’´`]", "'");
        s = Regex.Replace(s, @"[\[\]|/#→←]", " ");
        s = Regex.Replace(s, @"[♥☆♡©\\]", "").Replace("@", " arroba ");
        s = Regex.Replace(s, @" ([,.!?;:'])", "$1");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return "";
        if (!Regex.IsMatch(s, @"[.!?;:,'"")\]}…]$")) s += ".";
        return $"<es>{s}</es>";
    }

    /// <summary>Cada letra, a su número en la tabla del modelo; las que no conoce se saltan.</summary>
    private static long[] Encode(Models m, string prepared)
    {
        var ids = new List<long>(prepared.Length);
        foreach (char ch in prepared)
            if (ch < m.Indexer.Length && m.Indexer[ch] >= 0) ids.Add(m.Indexer[ch]);
        return ids.ToArray();
    }

    /// <summary>Parte el texto en trozos de pocas frases; el primero, corto.</summary>
    internal static IEnumerable<string> Split(string text)
    {
        var chunk = new StringBuilder();
        int limit = FirstChunk;
        foreach (string sentence in Regex.Split(text.Trim(), @"(?<=[.!?…])\s+"))
        {
            if (sentence.Length == 0) continue;
            if (chunk.Length > 0 && chunk.Length + sentence.Length + 1 > limit)
            {
                yield return chunk.ToString();
                chunk.Clear();
                limit = MaxChunk;
            }
            string remaining = sentence;
            while (remaining.Length > limit)
            {
                if (chunk.Length > 0)
                {
                    yield return chunk.ToString();
                    chunk.Clear();
                    limit = MaxChunk;
                    continue;
                }
                int cut = remaining.LastIndexOf(' ', limit - 1, limit);
                if (cut < 1) cut = limit;
                yield return remaining[..cut].Trim();
                remaining = remaining[cut..].TrimStart();
                limit = MaxChunk;
            }
            if (chunk.Length > 0) chunk.Append(' ');
            chunk.Append(remaining);
        }
        if (chunk.Length > 0) yield return chunk.ToString();
    }

    private static Style LoadStyle(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        (float[] Data, int[] Dims) Read(string key)
        {
            var node = doc.RootElement.GetProperty(key);
            int[] dims = node.GetProperty("dims").EnumerateArray().Select(d => d.GetInt32()).ToArray();
            var data = new List<float>(dims.Aggregate(1, (a, b) => a * b));
            Flatten(node.GetProperty("data"), data);
            return (data.ToArray(), dims);
        }
        var (ttl, ttlDims) = Read("style_ttl");
        var (dp, dpDims) = Read("style_dp");
        return new Style(ttl, ttlDims, dp, dpDims);

        static void Flatten(JsonElement element, List<float> into)
        {
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Flatten(child, into);
            else into.Add(element.GetSingle());
        }
    }

    private static DenseTensor<float> Ones(params int[] dims)
    {
        var tensor = new DenseTensor<float>(dims);
        tensor.Buffer.Span.Fill(1f);
        return tensor;
    }

    /// <summary>Ruido gaussiano de partida.</summary>
    private static DenseTensor<float> Noise(int channels, int frames)
    {
        var tensor = new DenseTensor<float>([1, channels, frames]);
        var span = tensor.Buffer.Span;
        for (int i = 0; i < span.Length; i++)
        {
            double u1 = 1.0 - Random.Shared.NextDouble(), u2 = Random.Shared.NextDouble();
            span[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
        return tensor;
    }

    private static byte[] ToPcm16(float[] wav)
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

    // ---------- frases cortas ya dichas ----------

    private string CachePath(string text, double speed)
    {
        string key = $"{styleName}|{speed.ToString("0.00", CultureInfo.InvariantCulture)}|{text}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(Folder, "dichas", $"{styleName}-{hash}.pcm");
    }

    private static void SaveToCache(string path, byte[] pcm)
    {
        try
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, pcm);
            // Que no crezca sin límite: se quedan las 120 más recientes.
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.pcm").OrderByDescending(f => f.LastWriteTimeUtc).Skip(120)) old.Delete();
        }
        catch (Exception ex) { Log.Write($"No pude guardar una frase ya dicha: {ex.Message}"); }
    }

    private static void Post(SynchronizationContext? ui, Action action)
    {
        if (ui != null) ui.Post(_ => action(), null);
        else action();
    }
}
