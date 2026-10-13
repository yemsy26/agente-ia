using NAudio.Wave;

namespace Chispa;

/// <summary>Una petición explícita: graba una sola frase hasta el silencio. No escucha permanentemente.</summary>
internal static class ManualRecorder
{
    public static async Task<byte[]?> Capture(CancellationToken ct)
    {
        using var audio = new WaveInEvent { WaveFormat = new WaveFormat(Pcm.Rate, 16, 1), BufferMilliseconds = Pcm.BlockMs };
        using var stream = new MemoryStream();
        var gate = new object();
        DateTime started = DateTime.UtcNow, lastVoice = started;
        int firstVoice = -1, endVoice = 0, voicedBytes = 0;
        bool finished = false;
        Exception? failure = null;
        audio.DataAvailable += (_, e) =>
        {
            lock (gate)
            {
                if (finished || stream.Length > Pcm.BytesPerSecond * 15) return;
                int at = (int)stream.Length;
                stream.Write(e.Buffer, 0, e.BytesRecorded);
                var levels = Pcm.Levels(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (!levels.Any(level => level >= 180)) return;
                firstVoice = firstVoice < 0 ? Math.Max(0, at - Pcm.BytesPerSecond / 5) : firstVoice;
                endVoice = (int)stream.Length;
                voicedBytes += e.BytesRecorded;
                lastVoice = DateTime.UtcNow;
            }
        };
        audio.RecordingStopped += (_, e) => { lock (gate) if (!finished) failure = e.Exception; };
        audio.StartRecording();
        try
        {
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(50, ct);
                lock (gate)
                {
                    if (failure != null) throw new IOException("El micrófono dejó de grabar.", failure);
                    if (firstVoice < 0 && DateTime.UtcNow - started > TimeSpan.FromSeconds(8)) return null;
                    if (voicedBytes >= Pcm.BytesPerSecond / 4 && DateTime.UtcNow - lastVoice > TimeSpan.FromMilliseconds(800)) break;
                }
            }
        }
        finally { lock (gate) finished = true; audio.StopRecording(); }
        lock (gate)
        {
            if (firstVoice < 0 || voicedBytes < Pcm.BytesPerSecond / 4) return null;
            byte[] pcm = stream.ToArray();
            int end = Math.Min(pcm.Length, endVoice + Pcm.BytesPerSecond / 3);
            return Pcm.ToWav(pcm.AsSpan(firstVoice, (end - firstVoice) & ~1));
        }
    }
}
