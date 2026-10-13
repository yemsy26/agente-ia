using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Chispa;

/// <summary>
/// Palabra de activación «chispa»: un modelo entrenado con el flujo de openWakeWord (chispa_wake.onnx)
/// sobre las mismas características que ese ecosistema usa en streaming. Por cada trama de 80 ms
/// (1280 muestras a 16 kHz) se calcula el mel-espectrograma (melspectrogram.onnx), se guarda en una
/// historia de 76 marcos, de ahí sale una incrustación de 96 números (embedding_model.onnx) y la red
/// final mira las últimas 16 incrustaciones (1,28 s) para dar una puntuación entre 0 y 1.
/// </summary>
internal sealed class WakeWordEngine : IDisposable
{
    public const int ChunkSamples = 1280;             // 80 ms a 16 kHz
    public const float Threshold = 0.5f;
    private const int MelHistory = 76, MelBins = 32, FeatureFrames = 16, EmbeddingSize = 96;
    private const int TailSamples = ChunkSamples + 480;   // 80 ms más los 30 ms previos que pide el mel
    private const int DebounceMs = 1500;                  // silencio tras una detección para no repetirla

    public static string? ModelPath { get; } = Oido.Model("chispa_wake.onnx");
    public static string? MelPath { get; } = Oido.Model("melspectrogram.onnx");
    public static string? EmbeddingPath { get; } = Oido.Model("embedding_model.onnx");
    public static bool ModelsInstalled => ModelPath != null && MelPath != null && EmbeddingPath != null;

    private readonly InferenceSession? model, melModel, embedModel;
    private readonly string melInputName = "input", embedInputName = "input_1";
    private readonly object gate = new();
    private readonly float[] mel = new float[MelHistory * MelBins];   // empieza en unos, como en Python
    private readonly List<float[]> features = new();                  // las últimas incrustaciones
    private readonly List<float> pending = new();                     // muestras aún sin procesar
    private readonly float[] tail = new float[TailSamples];           // audio reciente para el mel
    private readonly float[] recent = new float[3];                   // suavizado de la puntuación
    private int tailCount;
    private int framesSeen;                     // marcos procesados desde Reset (los primeros se descartan)
    private float lastScore;
    private DateTime firedUtc = DateTime.MinValue;

    /// <summary>Se oyó «chispa». Puede dispararse desde un hilo del fondo.</summary>
    public event Action? Woke;

    public bool Available => model != null;
    public float Score { get { lock (gate) return lastScore; } }

    public WakeWordEngine()
    {
        if (!ModelsInstalled) return;
        try
        {
            melModel = new InferenceSession(MelPath!);
            embedModel = new InferenceSession(EmbeddingPath!);
            model = new InferenceSession(ModelPath!);
            melInputName = melModel.InputMetadata.First().Key;
            embedInputName = embedModel.InputMetadata.First().Key;
            for (int i = 0; i < mel.Length; i++) mel[i] = 1f;   // igual que el buffer inicial de openWakeWord
        }
        catch (Exception ex) { Log.Write($"Palabra de activación no disponible: {ex.Message}"); }
    }

    /// <summary>Olvida lo anterior: para empezar una escucha nueva (o tras un cambio de dispositivo).</summary>
    public void Reset()
    {
        lock (gate)
        {
            pending.Clear();
            features.Clear();
            tailCount = 0;
            framesSeen = 0;
            lastScore = 0;
            for (int i = 0; i < recent.Length; i++) recent[i] = 0;
            for (int i = 0; i < mel.Length; i++) mel[i] = 1f;
        }
    }

    /// <summary>
    /// Alimenta muestras PCM de 16 kHz, mono, sin normalizar: el valor del int16 como float,
    /// que es lo que el modelo de mel espera (openWakeWord le pasa int16 convertido a float32).
    /// Rápido: pensado para el hilo del micrófono.
    /// </summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        if (model == null || samples.Length == 0) return;
        lock (gate)
        {
            pending.AddRange(samples.ToArray());
            while (pending.Count >= ChunkSamples)
            {
                var chunk = pending.GetRange(0, ChunkSamples);
                pending.RemoveRange(0, ChunkSamples);
                ProcessChunk(chunk);
            }
        }
    }

    /// <summary>
    /// Lo mismo, pero con PCM de 16 bits (lo que da NAudio). Los valores van sin normalizar:
    /// el modelo de mel espera muestras en el rango del int16, igual que en openWakeWord.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> pcm16)
    {
        if (model == null || pcm16.Length < 2) return;
        var samples = new float[pcm16.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm16[i * 2] | pcm16[i * 2 + 1] << 8);
        Feed(samples);
    }

    private void ProcessChunk(List<float> chunk)
    {
        // El mel ve el audio nuevo más los 30 ms previos, como en _streaming_melspectrogram.
        int keep = Math.Min(tailCount, TailSamples - ChunkSamples);
        if (keep < tailCount)
        {
            Array.Copy(tail, tailCount - keep, tail, 0, keep);
            tailCount = keep;
        }
        for (int i = 0; i < ChunkSamples; i++) tail[tailCount + i] = chunk[i];
        tailCount += ChunkSamples;

        try
        {
            var melInput = new DenseTensor<float>(tail.AsSpan(0, tailCount).ToArray(), new[] { 1, tailCount });
            using var melOut = melModel!.Run([NamedOnnxValue.CreateFromTensor(melInputName, melInput)]);
            float[] melTensor = melOut.First().AsTensor<float>().ToArray();   // (1, 1, marcos, 32) plano
            int newFrames = melTensor.Length / MelBins;                       // n/160 − 3 marcos nuevos
            // Transformación que equipara el modelo ONNX con el original de TensorFlow: x/10 + 2.
            for (int f = 0; f < newFrames; f++)
            {
                Array.Copy(mel, MelBins, mel, 0, MelBins * (MelHistory - 1));
                for (int j = 0; j < MelBins; j++)
                    mel[MelBins * (MelHistory - 1) + j] = melTensor[f * MelBins + j] / 10f + 2f;
            }

            // Incrustación desde los últimos 76 marcos de mel.
            var embedInput = new DenseTensor<float>((float[])mel.Clone(), new[] { 1, MelHistory, MelBins, 1 });
            using var embedOut = embedModel!.Run([NamedOnnxValue.CreateFromTensor(embedInputName, embedInput)]);
            features.Add(embedOut.First().AsTensor<float>().ToArray());   // 96 números
            if (features.Count > FeatureFrames) features.RemoveAt(0);

            framesSeen++;
            if (framesSeen > 5 && features.Count == FeatureFrames)   // como Python: los primeros marcos valen 0
            {
                var flat = new float[FeatureFrames * EmbeddingSize];
                for (int i = 0; i < FeatureFrames; i++) Array.Copy(features[i], 0, flat, i * EmbeddingSize, EmbeddingSize);
                var netInput = new DenseTensor<float>(flat, new[] { 1, FeatureFrames, EmbeddingSize });
                using var netOut = model!.Run([NamedOnnxValue.CreateFromTensor(model.InputMetadata.First().Key, netInput)]);
                lastScore = netOut.First().AsTensor<float>().ToArray()[0];
                recent[0] = recent[1]; recent[1] = recent[2]; recent[2] = lastScore;
                if (Average(recent) >= Threshold) Fire();
            }
            else lastScore = 0;
        }
        catch (Exception ex) { Log.Write($"Palabra de activación: {ex.Message}"); }
    }

    /// <summary>
    /// Dispara una vez por palabra: la media de los tres últimos marcos debe superar el umbral
    /// y además se guarda un segundo y medio de silencio para no repetir la misma detección.
    /// </summary>
    private void Fire()
    {
        DateTime now = DateTime.UtcNow;
        if (now - firedUtc < TimeSpan.FromMilliseconds(DebounceMs)) return;
        firedUtc = now;
        ThreadPool.QueueUserWorkItem(_ => { try { Woke?.Invoke(); } catch (Exception ex) { Log.Write($"Al avisar del despertar: {ex.Message}"); } });
    }

    private static float Average(float[] v)
    {
        float sum = 0;
        foreach (float x in v) sum += x;
        return sum / v.Length;
    }

    /// <summary>
    /// Diagnóstico: pasa una grabación entera (WAV de 16 kHz) como si llegara del micrófono.
    /// Devuelve la puntuación más alta y cuántas tramas superaron el umbral.
    /// </summary>
    public (float MaxScore, int FramesAbove) TestWave(byte[] wav)
    {
        if (wav.Length <= Pcm.WavHeader) return (0, 0);
        var samples = Pcm.ToFloat16k(wav);   // viene normalizado a [-1, 1] (así lo quieren Whisper y el VAD)
        for (int i = 0; i < samples.Length; i++) samples[i] *= 32768f;   // el mel, en cambio, quiere el rango del int16
        Reset();
        float max = 0;
        int above = 0;
        for (int i = 0; i + ChunkSamples <= samples.Length; i += ChunkSamples)
        {
            Feed(samples.AsSpan(i, ChunkSamples));
            max = Math.Max(max, Score);
            if (Score >= Threshold) above++;
        }
        return (max, above);
    }

    public void Dispose()
    {
        lock (gate)
        {
            model?.Dispose();
            melModel?.Dispose();
            embedModel?.Dispose();
            pending.Clear();
            features.Clear();
        }
    }
}
