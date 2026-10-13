using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Chispa;

/// <summary>La carpeta «oido», junto al programa, donde viven los modelos del oído (Whisper, Silero, wake-word).</summary>
internal static class Oido
{
    /// <summary>El modelo de nombre dado en la carpeta del oído, o null si no está.</summary>
    public static string? Model(string fileName)
    {
        string where = Environment.GetEnvironmentVariable("CHISPA_OIDO") is { Length: > 0 } forced
            ? (File.Exists(forced) ? Path.GetDirectoryName(forced) : forced) ?? ""
            : Path.Combine(AppContext.BaseDirectory, "oido");
        if (where.Length == 0) return null;
        string path = Path.Combine(where, fileName);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// Oído de voz frente a ruido con el modelo Silero VAD (ONNX, gratuito, dentro del equipo).
/// Examina tramas de 512 muestras a 16 kHz y dice si contienen voz de persona; la música y el ruido
/// de fondo no cuentan como voz. Si falta el modelo en la carpeta «oido», queda inactivo y quien lo
/// usa cae al umbral de energía de siempre.
/// </summary>
internal sealed class SileroVad : IDisposable
{
    public const int FrameSamples = 512;   // lo que el modelo examina en cada paso
    private const int ContextSamples = 64; // muestras anteriores que el modelo pide con cada trama
    private const float Threshold = 0.5f;  // probabilidad por encima de la cual hay voz
    private const int HangoverFrames = 4;  // cuántas tramas se sostiene «hay voz» tras caer (~130 ms)

    private readonly InferenceSession? session;
    private readonly string inputName, stateName, srName, outputName, stateOutName;
    private float[] state = new float[2 * 128];
    private float[] context = new float[ContextSamples];
    private readonly float[] combined = new float[FrameSamples + ContextSamples];
    private int hangover;
    private bool disposed;

    public static string? ModelPath { get; } = Oido.Model("silero_vad.onnx");
    public bool Available => session != null;

    public SileroVad()
    {
        if (ModelPath == null) return;
        try
        {
            session = new InferenceSession(ModelPath);
            inputName = session.InputMetadata.Keys.First(k => k != "state" && k != "sr");
            stateName = session.InputMetadata.Keys.First(k => k == "state");
            srName = session.InputMetadata.Keys.First(k => k == "sr");
            outputName = session.OutputMetadata.Keys.First(k => k != "stateN");
            stateOutName = session.OutputMetadata.Keys.First(k => k == "stateN");
        }
        catch (Exception ex)
        {
            session?.Dispose();
            Log.Write($"Silero VAD no disponible: {ex.Message}");
        }
    }

    /// <summary>Olvida lo anterior: para empezar una frase nueva.</summary>
    public void Reset()
    {
        state = new float[2 * 128];
        context = new float[ContextSamples];
        hangover = 0;
    }

    /// <summary>La probabilidad de que la trama (512 muestras, 16 kHz) contenga voz.</summary>
    public float Probability(float[] frame)
    {
        if (session == null || frame.Length < FrameSamples) return 0;
        try
        {
            // El modelo quiere, con cada trama, las 64 muestras que la preceden.
            Array.Copy(context, 0, combined, 0, ContextSamples);
            Array.Copy(frame, 0, combined, ContextSamples, FrameSamples);
            var input = new DenseTensor<float>((float[])combined.Clone(), new[] { 1, FrameSamples + ContextSamples });
            var stateTensor = new DenseTensor<float>((float[])state.Clone(), new[] { 2, 1, 128 });
            var srTensor = new DenseTensor<long>(new long[] { 16000 }, new[] { 1 });
            using var results = session.Run(
            [
                NamedOnnxValue.CreateFromTensor(inputName, input),
                NamedOnnxValue.CreateFromTensor(stateName, stateTensor),
                NamedOnnxValue.CreateFromTensor(srName, srTensor),
            ]);
            float prob = results.First(r => r.Name == outputName).AsTensor<float>()[0];
            state = results.First(r => r.Name == stateOutName).AsTensor<float>().ToArray();
            Array.Copy(combined, FrameSamples, context, 0, ContextSamples);
            return prob;
        }
        catch (Exception ex)
        {
            Log.Write($"Silero VAD: {ex.Message}");
            return 0;
        }
    }

    /// <summary>¿Hay voz? Sostiene el sí unos instantes para no cortar la frase entre sílabas.</summary>
    public bool IsSpeech(float[] frame)
    {
        float prob = Probability(frame);
        if (prob >= Threshold) { hangover = HangoverFrames; return true; }
        if (hangover > 0) { hangover--; return true; }
        return false;
    }

    /// <summary>Si está disponible, sí o no en cada trama de la grabación; en caso contrario, null.
    /// Sigue el estado de donde se quedó: quien empieza una frase nueva llama a Reset.</summary>
    public bool[]? Mark(float[] wav)
    {
        if (session == null) return null;
        var marks = new bool[(wav.Length + FrameSamples - 1) / FrameSamples];
        for (int i = 0; i < marks.Length; i++)
        {
            var frame = new float[FrameSamples];
            int at = i * FrameSamples;
            int take = Math.Min(FrameSamples, wav.Length - at);
            Array.Copy(wav, at, frame, 0, take);
            marks[i] = IsSpeech(frame);
        }
        return marks;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session?.Dispose();
    }
}
