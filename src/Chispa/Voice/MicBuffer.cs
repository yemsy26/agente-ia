using System.Speech.Recognition;
using NAudio.Wave;

namespace Chispa;

/// <summary>De dónde sale la grabación completa de una frase cuando el reconocedor solo se quedó con el principio.</summary>
internal interface IUtteranceSource
{
    /// <summary>
    /// Si el usuario siguió hablando más allá de lo que el reconocedor dio por terminado, espera a que calle
    /// y devuelve la frase entera en WAV. Devuelve null si ya había callado (lo oído estaba completo).
    /// </summary>
    /// <param name="heard">El audio del reconocedor, o null si quien llama no tiene un resultado
    /// (p. ej. el motor de palabra de activación, que solo sabe cuándo se oyó el nombre).</param>
    /// <param name="patienceMs">Cuánto silencio se le concede antes de darlo por callado.</param>
    Task<byte[]?> FullUtterance(RecognizedAudio? heard, DateTime speechStartedUtc, int patienceMs = 300);
}

/// <summary>Utilidades para audio PCM de 16 bits, mono, a 16 kHz.</summary>
internal static class Pcm
{
    public const int Rate = 16000, BytesPerSecond = Rate * 2, BlockMs = 50, BlockBytes = BytesPerSecond * BlockMs / 1000;
    public const int WavHeader = 44;

    /// <summary>Volumen (RMS) de cada bloque de 50 ms.</summary>
    public static double[] Levels(ReadOnlySpan<byte> pcm)
    {
        var levels = new double[pcm.Length / BlockBytes];
        for (int b = 0; b < levels.Length; b++)
        {
            var block = pcm.Slice(b * BlockBytes, BlockBytes);
            double sum = 0;
            for (int i = 0; i < block.Length; i += 2)
            {
                short sample = (short)(block[i] | block[i + 1] << 8);
                sum += (double)sample * sample;
            }
            levels[b] = Math.Sqrt(sum / (BlockBytes / 2));
        }
        return levels;
    }

    /// <summary>
    /// ¿Hay voz entre dos bloques? "Voz" es lo que suena al menos un tercio de fuerte que los momentos
    /// más altos del tramo: se compara con la propia frase, así la música de fondo no cuenta como voz.
    /// </summary>
    public static bool AnyVoice(double[] levels, int fromBlock, int toBlock)
    {
        if (levels.Length == 0) return false;
        var sorted = (double[])levels.Clone();
        Array.Sort(sorted);
        double voice = sorted[(int)((sorted.Length - 1) * 0.9)];
        if (voice < 120) return false;   // casi silencio en todo el tramo
        for (int i = Math.Max(0, fromBlock); i < Math.Min(levels.Length, toBlock); i++)
            if (levels[i] >= voice * 0.33) return true;
        return false;
    }

    /// <summary>Las muestras de un WAV (16 bits, uno o dos canales, cualquier frecuencia) como mono a 16 kHz.</summary>
    public static float[] ToFloat16k(byte[] wav)
    {
        if (wav.Length <= WavHeader) return [];
        int channels = Math.Max(1, (int)BitConverter.ToInt16(wav, 22));
        int rate = BitConverter.ToInt32(wav, 24);
        // Los datos empiezan tras el bloque "data", que no siempre está en el byte 44.
        int start = WavHeader;
        for (int i = 12; i + 8 <= wav.Length; i += 8 + BitConverter.ToInt32(wav, i + 4))
        {
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a')
            {
                start = i + 8;
                break;
            }
            if (BitConverter.ToInt32(wav, i + 4) < 0) break;
        }

        int frames = (wav.Length - start) / (2 * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(wav, start + (f * channels + c) * 2);
            mono[f] = sum / (32768f * channels);
        }
        return Resample(mono, rate, Rate);
    }

    /// <summary>Cambia la frecuencia de muestreo (suficiente para reconocer voz y medir).</summary>
    public static float[] Resample(float[] wav, int from, int to)
    {
        if (from == to || from <= 0 || wav.Length == 0) return wav;
        int window = Math.Max(1, from / to);   // suaviza antes de saltarse muestras
        var smooth = new float[wav.Length];
        double sum = 0;
        for (int i = 0; i < wav.Length; i++)
        {
            sum += wav[i];
            if (i >= window) sum -= wav[i - window];
            smooth[i] = (float)(sum / Math.Min(i + 1, window));
        }
        var result = new float[(int)((long)wav.Length * to / from)];
        for (int i = 0; i < result.Length; i++)
        {
            double at = (double)i * from / to;
            int left = (int)at;
            int right = Math.Min(left + 1, smooth.Length - 1);
            result[i] = (float)(smooth[left] + (smooth[right] - smooth[left]) * (at - left));
        }
        return result;
    }

    public static byte[] ToWav(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[WavHeader + pcm.Length];
        using (var w = new BinaryWriter(new MemoryStream(wav)))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);            // PCM
            w.Write((short)1);            // mono
            w.Write(Rate);
            w.Write(BytesPerSecond);
            w.Write((short)2);            // bytes por muestra
            w.Write((short)16);           // bits por muestra
            w.Write("data"u8);
            w.Write(pcm.Length);
        }
        pcm.CopyTo(wav.AsSpan(WavHeader));
        return wav;
    }
}

/// <summary>
/// Los últimos segundos del micrófono, solo en RAM. Sirve para recuperar entera una frase como
/// "Chispa, salta a la siguiente canción", que el reconocedor ligero corta en "Chispa, salta".
/// Nunca se guarda en disco ni sale del equipo.
/// </summary>
internal sealed class MicBuffer : IUtteranceSource, IDisposable
{
    private const int Seconds = 20;

    private readonly byte[] ring = new byte[Pcm.BytesPerSecond * Seconds];
    private readonly object gate = new();
    private WaveInEvent? input;
    private long written;
    private DateTime lastDataUtc;
    private SileroVad? vad;   // distingue voz de ruido de verdad, si está el modelo
    private volatile bool wantOpen;   // se quiere grabar: si no hay dispositivo, se reintenta en segundo plano
    private Task? retryLoop;          // el bucle que reintenta abrir el micrófono hasta que aparece

    /// <summary>El micrófono dejó de grabar por sí solo (desconexión o fallo): no llegará nada más.</summary>
    public event Action? MicLost;

    /// <summary>Audio recién grabado (PCM de 16 bits, mono, 16 kHz), sin la cabecera WAV.
    /// El búfer solo vale durante la llamada: quien lo use debe copiarlo. Sirve para el
    /// motor de palabra de activación, que lo analiza en directo.</summary>
    public event Action<byte[], int>? Data;

    public void Start()
    {
        wantOpen = true;
        if (input != null) return;
        try { OpenMic(); }
        catch (Exception ex) { Log.Write($"Sin grabación circular del micrófono: {ex.Message}"); }
        // Sin dispositivo ahora: se reintenta en segundo plano, por si se enchufa uno después.
        if (input == null) retryLoop ??= Task.Run(RetryUntilOpen);
    }

    /// <summary>Abre el micrófono circular. Lanza si no hay ningún dispositivo que acepte 16 kHz mono.</summary>
    private void OpenMic()
    {
        var mic = new WaveInEvent { WaveFormat = new WaveFormat(Pcm.Rate, 16, 1), BufferMilliseconds = Pcm.BlockMs };
        mic.DataAvailable += OnData;
        mic.RecordingStopped += (_, _) => OnRecordingStopped(mic);
        input = mic;   // antes de grabar: si el dispositivo muere al instante, el aviso encuentra a quién señalar
        try { mic.StartRecording(); }
        catch { input = null; mic.Dispose(); throw; }
    }

    /// <summary>
    /// Reintenta abrir el micrófono mientras se quiera grabar y no haya dispositivo. Si la persona
    /// enchufa uno después de encender la asistente, la grabación se reanuda sola.
    /// </summary>
    private async Task RetryUntilOpen()
    {
        try
        {
            while (wantOpen && input == null)
            {
                await Task.Delay(2000);
                if (!wantOpen || input != null) break;
                try
                {
                    OpenMic();
                    Log.Write("Oído: el micrófono volvió; grabación circular reanudada.");
                }
                catch (Exception) { /* sigue sin dispositivo: otro intento en dos segundos */ }
            }
        }
        finally { retryLoop = null; }
    }

    private void OnRecordingStopped(WaveInEvent mic)
    {
        lock (gate)
        {
            // En una parada propia (Stop) input ya es null: esto no es una caída.
            if (!ReferenceEquals(input, mic)) return;
            input = null;
            written = 0;
        }
        try { mic.Dispose(); } catch (Exception) { /* ya estaba muerto */ }
        Log.Write("El micrófono dejó de grabar por sí solo; se intentará reabrir.");
        MicLost?.Invoke();
    }

    public void Stop()
    {
        wantOpen = false;   // el bucle de reintentos, si estaba esperando, se detiene
        var mic = input;
        input = null;
        if (mic == null) return;
        try { mic.StopRecording(); } catch (Exception) { /* ya estaba parado */ }
        mic.Dispose();
        lock (gate) written = 0;
    }

    public void Dispose()
    {
        Stop();
        vad?.Dispose();
        vad = null;
    }

    public async Task<byte[]?> FullUtterance(RecognizedAudio? heard, DateTime speechStartedUtc, int patienceMs = 300)
    {
        if (input == null) return null;
        vad ??= new SileroVad();   // si falta el modelo queda null tras el primer intento
        if (vad is { Available: false }) { vad.Dispose(); vad = null; }
        vad?.Reset();
        DateTime fedUntil = DateTime.MinValue;   // en el tiempo de la grabación: hasta dónde ya ha mirado el VAD
        var from = speechStartedUtc.AddMilliseconds(-600);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        var giveUp = DateTime.UtcNow.AddMilliseconds(patienceMs - 300);   // hasta cuándo se espera a que siga hablando
        bool continued = false;
        while (true)
        {
            var (pcm, startUtc) = Snapshot(from);
            double[] levels = Pcm.Levels(pcm);
            int tailBlocks = (continued ? 800 : 300) / Pcm.BlockMs;
            bool talking = vad != null ? VoiceInTail(pcm, startUtc, ref fedUntil) : Pcm.AnyVoice(levels, levels.Length - tailBlocks, levels.Length);
            if (!continued)
            {
                if (talking) continued = true;
                else if (DateTime.UtcNow >= giveUp) return null;
            }
            else if (!talking || DateTime.UtcNow >= deadline) return Pcm.ToWav(pcm);
            await Task.Delay(100);
        }
    }

    /// <summary>¿Hay voz en lo nuevo de la grabación? El VAD recuerda lo anterior y sostiene la voz entre sílabas.</summary>
    private bool VoiceInTail(byte[] pcm, DateTime startUtc, ref DateTime fedUntil)
    {
        if (vad == null) return false;
        if (fedUntil == DateTime.MinValue) fedUntil = startUtc;
        int skip = (int)Math.Clamp((fedUntil - startUtc).TotalSeconds * Pcm.BytesPerSecond, 0, pcm.Length) & ~1;
        if (pcm.Length <= skip) return false;
        int count = (pcm.Length - skip) / 2;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            short s = (short)(pcm[skip + i * 2] | pcm[skip + i * 2 + 1] << 8);
            samples[i] = s / 32768f;
        }
        fedUntil = startUtc.AddSeconds(pcm.Length / (double)Pcm.BytesPerSecond);
        return vad.Mark(samples) is { } marks && Array.Exists(marks, m => m);
    }

    /// <summary>
    /// ¿Se oyó voz en algún momento desde fromUtc? Sirve para confirmar el motor de palabra de activación:
    /// si este oyó «chispa» donde no había voz humana (música, tonos), era un disparo falso.
    /// Con el VAD de Silero si está instalado; si no, se cae al umbral de energía.
    /// </summary>
    public bool VoiceSince(DateTime fromUtc)
    {
        if (input == null) return false;
        var (pcm, startUtc) = Snapshot(fromUtc);
        vad ??= new SileroVad();
        if (vad is { Available: false }) { vad.Dispose(); vad = null; }
        if (vad != null)
        {
            vad.Reset();
            int count = pcm.Length / 2;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                short s = (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8);
                samples[i] = s / 32768f;
            }
            return vad.Mark(samples) is { } marks && Array.Exists(marks, m => m);
        }
        return Pcm.AnyVoice(Pcm.Levels(pcm), 0, pcm.Length / Pcm.BlockBytes);
    }

    /// <summary>
    /// Lo grabado desde fromUtc hasta este instante, en WAV. Null si el micrófono no está grabando
    /// o si aún no hay ni medio segundo. Sirve para la primera pasada del oído fino: transcribir
    /// mientras la persona todavía está hablando, sin esperar al silencio que cierra la frase.
    /// </summary>
    public byte[]? AudioSince(DateTime fromUtc)
    {
        if (input == null) return null;
        var (pcm, _) = Snapshot(fromUtc);
        return pcm.Length >= Pcm.BytesPerSecond / 2 ? Pcm.ToWav(pcm) : null;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (gate)
        {
            int count = Math.Min(e.BytesRecorded, ring.Length);
            int at = (int)(written % ring.Length);
            int first = Math.Min(count, ring.Length - at);
            Buffer.BlockCopy(e.Buffer, 0, ring, at, first);
            Buffer.BlockCopy(e.Buffer, first, ring, 0, count - first);
            written += count;
            lastDataUtc = DateTime.UtcNow;
        }
        // Fuera del candado: el motor de activación no debe bloquear la grabación.
        Data?.Invoke(e.Buffer, e.BytesRecorded);
    }

    /// <summary>Copia lo grabado desde un instante hasta ahora, y cuándo empieza esa copia.</summary>
    private (byte[] Pcm, DateTime StartUtc) Snapshot(DateTime fromUtc)
    {
        lock (gate)
        {
            long available = Math.Min(written, ring.Length);
            long wanted = (long)((lastDataUtc - fromUtc).TotalSeconds * Pcm.BytesPerSecond);
            int count = (int)Math.Clamp(wanted, 0, available) & ~1;   // par: muestras de 2 bytes
            var pcm = new byte[count];
            int at = (int)((written - count) % ring.Length);
            int first = Math.Min(count, ring.Length - at);
            Buffer.BlockCopy(ring, at, pcm, 0, first);
            Buffer.BlockCopy(ring, 0, pcm, first, count - first);
            return (pcm, lastDataUtc.AddSeconds(-count / (double)Pcm.BytesPerSecond));
        }
    }
}
