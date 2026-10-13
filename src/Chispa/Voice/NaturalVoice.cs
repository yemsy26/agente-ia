using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Chispa;

/// <summary>Una voz que funciona en el propio equipo, sin internet.</summary>
internal interface INaturalVoice
{
    /// <summary>Terminó de decir la frase.</summary>
    event Action? Finished;

    /// <summary>No pudo decirla (se pasa el texto para decirlo con otra voz).</summary>
    event Action<string>? Failed;

    bool Speaking { get; }

    /// <summary>Milisegundos que tardó en empezar a sonar la última frase.</summary>
    long LastStartMs { get; }

    /// <summary>Empieza a decir el texto. Devuelve false si no pudo ni arrancar.</summary>
    bool Say(string text);

    void Stop();

    /// <summary>Se prepara para hablar pronto (el usuario acaba de dirigirse a ella).</summary>
    void Warm();

    /// <summary>Suelta lo que tenga cargado en memoria (al dormir).</summary>
    void Release();
}

/// <param name="Label">Cómo se presenta al hablar: "Esta es la voz {Label}".</param>
/// <param name="Menu">Cómo aparece en el menú.</param>
/// <param name="Kind">supertonic, piper o windows.</param>
/// <param name="Gender">mujer, hombre o vacío si no se sabe.</param>
internal sealed record VoiceOption(string Id, string Label, string Menu, string Kind, string Gender);

/// <summary>Las voces instaladas y cuál toca usar.</summary>
internal static class VoiceCatalog
{
    // Cuando el usuario no ha elegido ninguna: de la que mejor se entiende a la que peor, según la
    // medición de «Chispa.exe --voces» (un reconocedor de voz transcribe frases dichas por cada una).
    private static readonly string[] clearestFirst = ["F5", "F1", "F3", "F2", "M5", "M2", "M1", "M4", "M3", "F4"];

    /// <summary>
    /// Dónde están las voces: en la carpeta "voz" que hay junto al programa. Si no existe, en
    /// %LOCALAPPDATA%\Chispa\voz. La variable CHISPA_VOZ señala otra carpeta (para pruebas).
    /// </summary>
    public static string Folder { get; } = FindFolder();

    private static string FindFolder()
    {
        if (Environment.GetEnvironmentVariable("CHISPA_VOZ") is { Length: > 0 } forced) return forced;
        string beside = Path.Combine(AppContext.BaseDirectory, "voz");
        return Directory.Exists(beside)
            ? beside
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chispa", "voz");
    }

    public static List<VoiceOption> All()
    {
        var all = new List<VoiceOption>();
        foreach (string style in SupertonicVoice.Styles)
        {
            string gender = style.StartsWith('F') ? "mujer" : "hombre";
            all.Add(new(style, $"de {gender} número {style[1..]}", $"{char.ToUpper(gender[0])}{gender[1..]} {style[1..]}", "supertonic", gender));
        }
        foreach (string model in PiperVoice.Models) all.Add(new(model, "de antes", $"La de antes ({model})", "piper", ""));
        all.Add(new("windows", "clásica de Windows", "Clásica de Windows", "windows", ""));
        return all;
    }

    /// <summary>La voz que corresponde a la configuración; sin elección, la más clara de las instaladas.</summary>
    public static VoiceOption Current(Config cfg)
    {
        var all = All();
        string wanted = cfg.Voz.Trim();
        if (wanted.Length > 0)
        {
            return all.FirstOrDefault(v => v.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(v => v.Kind == "piper" && v.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                   ?? all[^1];   // el nombre de una voz de Windows, p. ej. "Helena"
        }
        foreach (string id in clearestFirst)
            if (all.FirstOrDefault(v => v.Id == id) is { } best) return best;
        return all[0];
    }
}

/// <summary>Reproduce sonido (PCM de 16 bits, mono) que va llegando por trozos y avisa cuando ha sonado todo.</summary>
internal sealed class StreamPlayer : IDisposable
{
    private readonly BufferedWaveProvider buffer;
    private readonly WaveOutEvent output = new() { DesiredLatency = 120 };
    private bool closed;

    /// <summary>Sonó hasta el final. No se dispara si se corta con Dispose.</summary>
    public event Action? Finished;

    public bool Started { get; private set; }

    public StreamPlayer(int sampleRate)
    {
        buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(90),
            DiscardOnBufferOverflow = true,
            ReadFully = true,   // mientras se genera, un hueco es silencio y no el final
        };
        output.Init(buffer);
        if (Environment.GetEnvironmentVariable("CHISPA_SIN_ALTAVOZ") == "1") output.Volume = 0;   // pruebas: mismo camino, sin sonar
        output.PlaybackStopped += (_, _) =>
        {
            if (closed) return;
            closed = true;
            output.Dispose();
            Finished?.Invoke();
        };
    }

    public void Add(byte[] pcm, int count)
    {
        if (closed || count == 0) return;
        buffer.AddSamples(pcm, 0, count);
        if (Started) return;
        Started = true;
        output.Play();
    }

    /// <summary>Ya no llega más sonido: al vaciarse lo pendiente, la reproducción termina sola.</summary>
    public void Complete()
    {
        if (Started) buffer.ReadFully = false;
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        try { output.Dispose(); } catch (Exception) { /* dispositivo de audio desconectado */ }
    }
}

/// <summary>
/// Voz neuronal Piper. Cada frase lanza un proceso breve y el sonido empieza a salir mientras se genera;
/// al callar no queda nada cargado. El motor está en voz\piper y las voces (.onnx) en voz\voces.
/// </summary>
internal sealed class PiperVoice : INaturalVoice
{
    private sealed class Session
    {
        public required Process Process;
        public required StreamPlayer Player;
        public required string Text;
    }

    private readonly Config cfg;
    private readonly string engine, model;
    private readonly int sampleRate;
    private Session? current;

    public event Action? Finished;
    public event Action<string>? Failed;

    public bool Speaking => current != null;
    public long LastStartMs { get; private set; }
    public int SampleRate => sampleRate;

    private static string VoicesFolder => Path.Combine(VoiceCatalog.Folder, "voces");

    /// <summary>Nombres de las voces instaladas, p. ej. "es_MX-claude-high".</summary>
    public static string[] Models
    {
        get
        {
            if (!File.Exists(Path.Combine(VoiceCatalog.Folder, "piper", "piper.exe")) || !Directory.Exists(VoicesFolder)) return [];
            return Directory.GetFiles(VoicesFolder, "*.onnx").Where(m => File.Exists(m + ".json"))
                .Select(m => Path.GetFileNameWithoutExtension(m)!).Order().ToArray();
        }
    }

    private PiperVoice(Config cfg, string engine, string model, int sampleRate)
    {
        (this.cfg, this.engine, this.model, this.sampleRate) = (cfg, engine, model, sampleRate);
    }

    public static PiperVoice? Open(Config cfg, string name)
    {
        try
        {
            string model = Path.Combine(VoicesFolder, name + ".onnx");
            using var info = JsonDocument.Parse(File.ReadAllText(model + ".json"));
            int rate = info.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32();
            return new PiperVoice(cfg, Path.Combine(VoiceCatalog.Folder, "piper", "piper.exe"), model, rate);
        }
        catch (Exception ex)
        {
            Log.Write($"Voz Piper no disponible: {ex.Message}");
            return null;
        }
    }

    public bool Say(string text)
    {
        Stop();
        try
        {
            double pitch = Math.Clamp(cfg.TonoVoz, 0.7, 1.5);
            var player = new StreamPlayer((int)(sampleRate * pitch));
            Process process;
            try { process = Start(text, pitch); }
            catch
            {
                player.Dispose();
                throw;
            }
            var session = current = new Session { Process = process, Player = player, Text = text };
            var ui = SynchronizationContext.Current;
            void FinishedPlayback()
            {
                if (current != session) return;   // la cortaron para decir otra cosa
                Close(session);
                Finished?.Invoke();
            }
            player.Finished += () => { if (ui == null || SynchronizationContext.Current == ui) FinishedPlayback(); else ui.Post(_ => FinishedPlayback(), null); };
            _ = Pump(session, Stopwatch.StartNew());
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"La voz Piper no pudo hablar: {ex.Message}");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (current is { } session) Close(session);
    }

    public void Warm() { }

    public void Release() { }

    /// <summary>Diagnóstico: genera la frase entera y devuelve el sonido sin reproducirlo.</summary>
    public byte[] Render(string text)
    {
        using var process = Start(text, 1.0);
        using var pcm = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(pcm);
        process.WaitForExit();
        return pcm.ToArray();
    }

    private Process Start(string text, double pitch)
    {
        // El tono sube reproduciendo más deprisa; se compensa alargando los fonemas para no acelerar el habla.
        double length = Math.Clamp((1.04 - 0.04 * cfg.VelocidadVoz) * pitch, 0.5, 2.0);
        var start = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(engine)!,
        };
        foreach (string arg in new[] { "--model", model, "--output_raw", "--quiet", "--sentence_silence", "0.25",
                                       "--length_scale", length.ToString("0.###", CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);

        var process = Process.Start(start) ?? throw new InvalidOperationException("no arrancó el motor de voz");
        process.StandardInput.WriteLine(text.ReplaceLineEndings(" "));
        process.StandardInput.Close();
        return process;
    }

    /// <summary>Pasa el sonido del motor al altavoz según va saliendo.</summary>
    private async Task Pump(Session session, Stopwatch clock)
    {
        try
        {
            var stream = session.Process.StandardOutput.BaseStream;
            byte[] chunk = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(chunk)) > 0)
            {
                if (current != session) return;
                if (!session.Player.Started) LastStartMs = clock.ElapsedMilliseconds;
                session.Player.Add(chunk, count);
            }
            // El motor cerró su salida. Si murió con un error, el audio quedó truncado: no se da por dicho.
            try
            {
                if (!session.Process.WaitForExit(3000) || session.Process.ExitCode != 0)
                {
                    Log.Write($"Voz Piper: el motor terminó con el código {(session.Process.HasExited ? session.Process.ExitCode : 0)}; la frase se cortó.");
                    if (current == session) { Close(session); Failed?.Invoke(session.Text); }
                    return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* el proceso ya no existe */ }
        }
        catch (Exception ex) { Log.Write($"Voz Piper interrumpida: {ex.Message}"); }

        if (current != session) return;
        if (session.Player.Started) session.Player.Complete();
        else
        {
            Close(session);
            Failed?.Invoke(session.Text);
        }
    }

    private void Close(Session session)
    {
        if (current == session) current = null;
        try
        {
            if (!session.Process.HasExited) session.Process.Kill();
        }
        catch (Exception) { /* ya había terminado */ }
        session.Process.Dispose();
        session.Player.Dispose();
    }
}
