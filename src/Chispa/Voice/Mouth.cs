using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;

namespace Chispa;

/// <summary>
/// La voz. Usa la voz elegida entre las instaladas (ver <see cref="VoiceCatalog"/>) y, si falla, la clásica
/// de Windows. Nada queda cargado mientras duerme: todo se suelta en <see cref="Release"/>.
/// </summary>
internal sealed class Mouth(Config cfg) : IDisposable
{
    private SpeechSynthesizer? synth;
    private Prompt? current;
    private INaturalVoice? natural;
    private bool naturalChecked, naturalBroken;
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    /// <summary>
    /// Ejecuta el aviso en el hilo de la interfaz: en línea si ya estamos en él, si no lo encola.
    /// Compara el hilo, no la identidad del SynchronizationContext: al bombear mensajes a mano
    /// (Application.DoEvents) Windows la sustituye por otra instancia y la comparación fallaba.
    /// </summary>
    internal void Signal(Action? action)
    {
        if (action == null) return;
        if (ui == null || Environment.CurrentManagedThreadId == uiThread) action();
        else ui.Post(_ => action(), null);
    }

    /// <summary>Pruebas de interfaz: voz simulada en lugar de la natural, sin abrir altavoces ni modelos.</summary>
    internal INaturalVoice? TestVoice { set { natural = value; naturalChecked = true; } }

    /// <summary>Pruebas de interfaz: los suscriptores de «Finished», para simular un aviso viejo en cola.</summary>
    internal Action? FinishedHandlers => Finished;

    public event Action? Started, Finished;

    public bool Speaking => current != null || natural?.Speaking == true;

    /// <summary>Qué voz sonará en la próxima frase.</summary>
    public string VoiceName => naturalBroken ? "clásica de Windows" : VoiceCatalog.Current(cfg).Label;

    /// <summary>Milisegundos que tardó en empezar a sonar la última frase (solo voz natural).</summary>
    public long LastStartMs => natural?.LastStartMs ?? 0;

    /// <summary>Empieza a decir el texto. Devuelve false si no hay voz disponible.</summary>
    public bool Say(string text)
    {
        string clean = Speakable(text);
        if (clean.Length == 0) return false;
        bool wasSpeaking = Speaking;

        if (Natural() is { } voice)
        {
            CancelWindows();
            if (voice.Say(clean))
            {
                if (!wasSpeaking) Signal(Started);
                return true;
            }
            naturalBroken = true;   // por esta sesión sigue con la voz de Windows
        }
        return SayWindows(clean, wasSpeaking);
    }

    public void Stop()
    {
        if (!Speaking) return;
        natural?.Stop();
        CancelWindows();
        Signal(Finished);
    }

    /// <summary>El usuario acaba de dirigirse a ella: que la voz esté lista para cuando toque contestar.</summary>
    public void Warm()
    {
        if (!cfg.Silencio) Natural()?.Warm();
    }

    /// <summary>Cambió la voz, la velocidad o el tono: la próxima frase se monta de nuevo con esos ajustes.</summary>
    public void Reconfigure()
    {
        Stop();
        synth?.Dispose();
        synth = null;
        natural = null;   // los modelos siguen cargados: al probar voces seguidas no hay que esperar cada vez
        naturalChecked = naturalBroken = false;
    }

    /// <summary>Suelta todo lo que la voz tenga en memoria, para no ocupar nada mientras duerme.</summary>
    public void Release()
    {
        if (Speaking) return;
        synth?.Dispose();
        synth = null;
        SupertonicVoice.Unload();
    }

    /// <summary>Diagnóstico: genera el audio de una frase (WAV de 16 kHz) con la voz de Windows, sin reproducirlo.</summary>
    public byte[] SynthesizeWav(string text)
    {
        using var temp = Create();
        using var pcm = new MemoryStream();
        temp.SetOutputToAudioStream(pcm, new SpeechAudioFormatInfo(Pcm.Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        temp.Speak(text);
        temp.SetOutputToNull();
        return Pcm.ToWav(pcm.ToArray());
    }

    public void Dispose()
    {
        natural?.Stop();
        synth?.Dispose();
        SupertonicVoice.Unload();
    }

    // ---------- voz natural ----------

    private INaturalVoice? Natural()
    {
        if (naturalBroken) return null;
        if (naturalChecked) return natural;
        naturalChecked = true;
        var chosen = VoiceCatalog.Current(cfg);
        natural = chosen.Kind switch
        {
            "supertonic" => new SupertonicVoice(cfg, chosen.Id),
            "piper" => PiperVoice.Open(cfg, chosen.Id),
            _ => null,
        };
        if (natural == null) return null;
        natural.Finished += () => Signal(Finished);
        natural.Failed += text =>
        {
            // No llegó a sonar: se dice con la voz de Windows para no dejar al usuario sin respuesta.
            naturalBroken = true;
            if (!SayWindows(text, wasSpeaking: true)) Signal(Finished);
        };
        return natural;
    }

    // ---------- voz de Windows ----------

    private bool SayWindows(string clean, bool wasSpeaking)
    {
        try
        {
            synth ??= Create();
            synth.SpeakAsyncCancelAll();

            // Una pausa corta entre frases suena menos a robot que leerlo todo de corrido.
            var prompt = new PromptBuilder(synth.Voice.Culture);
            foreach (string sentence in Regex.Split(clean, @"(?<=[.!?…])\s+"))
            {
                if (sentence.Length == 0) continue;
                prompt.AppendText(sentence);
                prompt.AppendBreak(TimeSpan.FromMilliseconds(220));
            }

            current = synth.SpeakAsync(prompt);
            if (!wasSpeaking) Signal(Started);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"No pude hablar: {ex.Message}");
            current = null;
            return false;
        }
    }

    private void CancelWindows()
    {
        if (current == null) return;
        current = null;
        try { synth?.SpeakAsyncCancelAll(); } catch (Exception) { /* ya estaba parado */ }
    }

    private SpeechSynthesizer Create()
    {
        var s = new SpeechSynthesizer { Rate = Math.Clamp(cfg.VelocidadVoz, -10, 10) };
        var voices = s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
        var voice = voices.FirstOrDefault(v => cfg.Voz.Length > 0 && v.Name.Contains(cfg.Voz, StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "es");
        if (voice != null) s.SelectVoice(voice.Name);
        // Para pruebas: recorre todo el camino de la voz sin que suene nada.
        if (Environment.GetEnvironmentVariable("CHISPA_SIN_ALTAVOZ") == "1") s.SetOutputToNull();
        else s.SetOutputToDefaultAudioDevice();
        s.SpeakCompleted += (_, e) => Signal(() =>
        {
            if (e.Prompt != current) return;   // un aviso anterior que se canceló
            current = null;
            Signal(Finished);
        });
        return s;
    }

    /// <summary>Quita lo que no se pronuncia: markdown, emojis y símbolos sueltos.</summary>
    private static string Speakable(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '*' or '#' or '`' or '_' or '~' or '|' or '«' or '»') continue;
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.Format) continue;
            sb.Append(rune.ToString());
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
