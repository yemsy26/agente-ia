using System.Diagnostics;
using System.Speech.Recognition;

namespace Chispa;

internal enum HeardKind { Nothing, Wake, Command, Speech }

/// <param name="Text">Id del reflejo (Command) o la frase dicha (Speech).</param>
/// <param name="Named">La frase empezaba por su nombre: seguro que iba para ella.</param>
internal readonly record struct Heard(HeardKind Kind, string? Text = null, bool Named = false)
{
    public string Value => Text ?? "";
}

/// <summary>
/// El oído. En reposo el reconocedor solo carga gramáticas diminutas (su nombre y las órdenes fijas),
/// que cuestan alrededor de un 1 % de un núcleo. El dictado libre (~45 MB y bastante más CPU) se usa
/// únicamente cuando hace falta:
///  - si alguien dice "Nombre, frase libre", la grabación de esa frase se transcribe en una segunda pasada;
///  - mientras está "atenta" (acaba de hablar contigo) escucha frases sin exigir el nombre.
/// </summary>
internal sealed class Ears : IDisposable
{
    private static readonly string[] greetings = ["oye", "hola", "hey"];

    // Cuánto silencio se tolera tras su nombre antes de contestar "¿Sí?" (el reconocedor ya esperó 0,7 s por su cuenta).
    private const int PauseAfterName = 1100;

    /// <summary>Diagnóstico: hace de micrófono con una grabación que ya se tiene entera.</summary>
    private sealed class WaveSource(byte[] wav) : IUtteranceSource
    {
        public Task<byte[]?> FullUtterance(RecognizedAudio? heard, DateTime speechStartedUtc, int patienceMs = 300)
        {
            double[] levels = Pcm.Levels(wav.AsSpan(Pcm.WavHeader));
            int end = (int)((heard!.AudioPosition + heard.Duration).TotalMilliseconds / Pcm.BlockMs);
            bool keptTalking = Pcm.AnyVoice(levels, end + 1, end + 1 + patienceMs / Pcm.BlockMs);
            return Task.FromResult(keptTalking ? wav : null);
        }
    }

    private readonly Config cfg;
    private readonly RecognizerInfo? recognizer;
    private readonly SynchronizationContext ui;
    private readonly MicBuffer mic = new();
    private readonly Transcriber? fine;   // el oído fino (Whisper), si está instalado
    private readonly object passLock = new();
    private WakeWordEngine? wake;         // el motor de palabra de activación (openWakeWord), si está
    private DateTime wokeUtc = DateTime.MinValue;   // cuándo se dio ya por despierta (cualquier oído), para no repetir

    private SpeechRecognitionEngine? engine;
    private SpeechRecognitionEngine? passEngine;
    private Grammar? bare, dictation, passNamed, passPlain;
    private int revision;
    private bool presenceAllowed = true;
    public bool PresenceAllowed { get => presenceAllowed; set { if (presenceAllowed == value) return; revision++; presenceAllowed = value; Sync(); } }
    private bool listening, paused, attentive;          // lo que se quiere
    private bool running, cancelling, updatePending;    // lo que el motor está haciendo
    private bool dictationLoaded, interpreting;
    private bool reopening, disposed;                   // reintentos de apertura en marcha; y si ya se tiró todo
    private CancellationTokenSource? interpretation;
    private DateTime speechStartedUtc, deafUntilUtc;

    public event Action<Heard>? HeardSomething;
    public event Action? SpeechStarted;
    public event Action<string>? InterpretationFailed;

    /// <summary>Hay reconocedor en español y micrófono.</summary>
    public bool Available => engine != null;
    public bool Processing => interpreting;
    public string Problem { get; private set; } = "";

    /// <summary>Diagnóstico: cómo se interpretó lo último que oyó.</summary>
    public string Trace { get; private set; } = "";

    public Ears(Config cfg)
    {
        this.cfg = cfg;
        fine = Transcriber.ModelPath != null ? new Transcriber(cfg) : null;
        ui = SynchronizationContext.Current ?? new SynchronizationContext();
        mic.MicLost += () => ui.Post(_ => RearmAfterMicLoss(), null);
        mic.Data += OnMicData;   // el motor de palabra de activación escucha el audio en directo
        wake = BuildWake();
        recognizer = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "es");
        if (recognizer == null)
            Problem = "Windows no tiene instalado el reconocimiento de voz en español (Configuración > Hora e idioma > Voz).";
    }

    /// <summary>Abre el micrófono. Devuelve false (y explica en Problem) si no se puede.</summary>
    public bool Open()
    {
        if (engine != null) return true;
        if (recognizer == null) return false;
        SpeechRecognitionEngine? e = null;
        try
        {
            e = new SpeechRecognitionEngine(recognizer);
            bare = LoadLight(e);
            e.SetInputToDefaultAudioDevice();
            // Cada aviso comprueba que sigue siendo el motor en uso: tras Reload llegan todavía avisos del anterior.
            e.SpeechRecognized += (_, a) => ui.Post(_ => { if (engine == e) OnResult(a.Result, rejected: false); }, null);
            e.SpeechRecognitionRejected += (_, a) => ui.Post(_ => { if (engine == e) OnResult(a.Result, rejected: true); }, null);
            e.SpeechDetected += (_, _) => ui.Post(_ =>
            {
                if (engine != e) return;
                if (!interpreting) speechStartedUtc = DateTime.UtcNow;
                SpeechStarted?.Invoke();
            }, null);
            e.RecognizeCompleted += (_, _) => ui.Post(_ =>
            {
                if (engine != e) return;
                running = cancelling = updatePending = false;
                Sync();
            }, null);
            e.RecognizerUpdateReached += (_, _) => ui.Post(_ =>
            {
                if (engine != e) return;
                updatePending = false;
                ApplyGrammars();
            }, null);
            engine = e;
            return true;
        }
        catch (Exception ex)
        {
            e?.Dispose();
            Problem = "No encuentro un micrófono que pueda usar.";
            Log.Write($"Oído no disponible: {ex.Message}");
            return false;
        }
    }

    /// <summary>Encender o apagar el micrófono.</summary>
    public bool Listening
    {
        get => listening;
        set { if (listening == value) return; revision++; listening = value; Sync(); }
    }

    /// <summary>En pausa mientras ella habla, para no oírse a sí misma.</summary>
    public bool Paused
    {
        get => paused;
        set { if (paused == value) return; revision++; paused = value; Sync(); }
    }

    /// <summary>Atenta: acepta frases libres sin exigir el nombre (carga el dictado).</summary>
    public bool Attentive
    {
        get => attentive;
        set { if (attentive == value) return; revision++; attentive = value; Sync(); }
    }

    /// <summary>Vuelve a montar el oído desde cero; hace falta cuando cambia su nombre, que está en las gramáticas.</summary>
    public void Reload()
    {
        interpretation?.Cancel();
        revision++;
        bool wasOpen = engine != null;
        mic.Stop();
        ReleaseSecondPass();
        engine?.Dispose();
        engine = null;
        dictation = null;
        running = cancelling = updatePending = dictationLoaded = interpreting = false;
        wake?.Dispose();
        wake = BuildWake();
        if (wasOpen)
        {
            if (Open()) Sync();
            else EnsureReopened();   // el micrófono falló al cambiar el nombre: se reintenta hasta que vuelva
        }
    }

    // ---------- palabra de activación ----------

    /// <summary>
    /// El motor openWakeWord solo existe si el modelo está instalado y el nombre de la asistente
    /// sigue siendo «chispa»: el modelo no sabe decir otro nombre.
    /// </summary>
    private WakeWordEngine? BuildWake()
    {
        if (!Reflexes.Normalize(cfg.Nombre).Equals("chispa", StringComparison.Ordinal) || !WakeWordEngine.ModelsInstalled) return null;
        var w = new WakeWordEngine();
        if (!w.Available) { w.Dispose(); return null; }
        w.Woke += OnWoke;
        if (cfg.Depurar) Log.Write("Oído: palabra de activación «chispa» lista.");
        return w;
    }

    /// <summary>El audio del micrófono alimenta al motor de activación en directo (hilo del micrófono).</summary>
    private void OnMicData(byte[] pcm, int count) => wake?.Feed(pcm.AsSpan(0, count));

    /// <summary>El motor oyó «chispa». Llega desde un hilo del fondo: se pasa al hilo de la interfaz.</summary>
    private void OnWoke()
    {
        ui.Post(_ => _ = ConsiderWoke(), null);
    }

    /// <summary>
    /// Qué hacer cuando el motor de activación oye su nombre. Igual que el camino del reconocedor de
    /// Windows: se espera un poco a ver si la persona sigue hablando ("Chispa, ¿qué hora es?") antes de
    /// contestar "¿Sí?", para no hablarle encima. Si siguió hablando, se transcribe la frase entera con
    /// el oído fino; si el nombre iba solo, se despierta.
    /// </summary>
    private async Task ConsiderWoke()
    {
        if (!listening || paused || !presenceAllowed || interpreting || attentive) { wake?.Reset(); return; }
        if (DateTime.UtcNow - wokeUtc < TimeSpan.FromSeconds(2)) return;   // ya se dio por despierta hace nada
        DateTime wordAt = DateTime.UtcNow - TimeSpan.FromMilliseconds(1100);   // la palabra ya se dijo
        int began = revision;
        using var request = TraceLog.Begin("entrada_voz", data: new { source = "wakeword" });
        interpreting = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var eagerStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        interpretation = timeout;
        var clock = Stopwatch.StartNew();

        // Primera pasada del oído fino, en directo: transcribe en cuanto la persona parece haber
        // terminado (2,5 s de audio y un momento sin voz), sin esperar al silencio que cierra la
        // frase. Si lo que oyó cubría todo lo dicho, se ahorra la segunda pasada.
        Task<(string? Text, int Covered)> eager = fine == null
            ? Task.FromResult<(string? Text, int Covered)>((null, 0))
            : Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        eagerStop.Token.ThrowIfCancellationRequested();
                        byte[]? soFar = mic.AudioSince(wordAt);
                        int pcm = soFar == null ? 0 : soFar.Length - Pcm.WavHeader;
                        bool longEnough = pcm >= Pcm.BytesPerSecond * 5 / 2;                        // 2,5 s de audio
                        bool quietTail = longEnough && !mic.VoiceSince(DateTime.UtcNow.AddMilliseconds(-400));
                        if (longEnough && (quietTail || pcm >= Pcm.BytesPerSecond * 6))             // o 6 s sin más
                        {
                            string? text = fine!.Hear(soFar!, eagerStop.Token);
                            return (text, pcm);
                        }
                        await Task.Delay(120, eagerStop.Token);
                    }
                }
                catch (OperationCanceledException) { return ((string?)null, 0); }
                catch (Exception ex) { Log.Write($"Primera pasada del oído fino: {ex.Message}"); return ((string?)null, 0); }
            });
        try
        {
            byte[]? rest = await mic.FullUtterance(null, wordAt, PauseAfterName).WaitAsync(timeout.Token);
            if (rest == null)
            {
                // El motor oyó «chispa» donde no había voz humana (música, tonos): disparo falso, no despertar.
                if (!mic.VoiceSince(wordAt.AddMilliseconds(-300))) { request.Mark("descartada"); return; }
                // Nombre a secas: «¿Sí?».
                if (began != revision || paused || !listening || !presenceAllowed || wokeUtc > wordAt) return;
                wokeUtc = DateTime.UtcNow;
                TraceLog.Event("voz_interpretada", "chispa", new { named = true, kind = (int)HeardKind.Wake });
                HeardSomething?.Invoke(new(HeardKind.Wake, Named: true));
                return;
            }
            // Siguió hablando: la frase entera, sin exigir el nombre (el motor ya lo oyó).
            // Si la primera pasada cubría todo lo dicho, esa transcripción vale y no hay segunda.
            (string? Text, int Covered) eagerDone;
            try { eagerDone = await eager.WaitAsync(timeout.Token); }
            catch { eagerDone = ((string?)null, 0); }
            Heard heard;
            int speechEnd = (rest.Length - Pcm.WavHeader) - 1400 * Pcm.BytesPerSecond / 1000;   // la frase acabó ~1,4 s antes del final de la grabación
            if (eagerDone.Text != null && eagerDone.Covered >= speechEnd)
            {
                Trace += $" · fino «{eagerDone.Text}»";
                heard = eagerDone.Text.Length == 0 ? default : new(HeardKind.Speech, eagerDone.Text);
            }
            else
            {
                heard = await Task.Run(() => Transcribe(rest, requireName: false, nameHeard: true, timeout.Token), timeout.Token);
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (heard.Kind == HeardKind.Nothing) heard = new(HeardKind.Wake, Named: true);   // se oyó algo, aunque no se pudo transcribir
            TraceLog.Event("voz_interpretada", heard.Value, new { named = heard.Named, kind = (int)heard.Kind, revision_valida = began == revision });
            if (began == revision && !paused && listening && presenceAllowed)
            {
                if (wokeUtc > wordAt) request.Mark("descartada");   // el reconocedor de Windows llegó antes
                else { wokeUtc = DateTime.UtcNow; HeardSomething?.Invoke(heard); }
            }
            else request.Mark("descartada");
        }
        catch (OperationCanceledException ex)
        {
            request.Fail(ex);
            if (began == revision && listening && presenceAllowed && !paused)
                InterpretationFailed?.Invoke("La interpretación de la voz tardó demasiado. Puedes repetir la frase; el detalle quedó en el diagnóstico.");
        }
        catch (Exception ex)
        {
            request.Fail(ex); Log.Write($"Error interpretando lo oído por el motor de activación: {ex.Message}");
            if (began == revision && listening && presenceAllowed && !paused)
                InterpretationFailed?.Invoke("No pude interpretar esa frase. El detalle quedó en el diagnóstico.");
        }
        finally
        {
            eagerStop.Cancel();   // si quedaba una primera pasada en marcha (nombre a secas), se detiene
            if (interpretation == timeout) { interpretation = null; interpreting = false; }
            if (began == revision && clock.ElapsedMilliseconds > 700) deafUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        }
    }

    /// <summary>Qué transcribe las frases libres: "Whisper" o "dictado de Windows".</summary>
    public string HearingName => fine != null ? $"Whisper ({Path.GetFileName(Transcriber.ModelPath)})" : "dictado de Windows";    public bool CanTranscribeExplicitly => fine != null;
    public async Task<string?> CaptureOnce(CancellationToken ct)
    {
        if (fine == null) return null;
        var wav = await ManualRecorder.Capture(ct);
        if (wav == null) return "";
        return await Task.Run(() => fine.Hear(wav, ct), ct);
    }
    internal string? TestExplicitWave(byte[] wav) => fine?.Hear(wav);

    /// <summary>Diagnóstico: la interpretación en curso, para comprobar que cada cierre la cancela (sin reflexión).</summary>
    internal CancellationTokenSource? Interpretation { get => interpretation; set => interpretation = value; }

    /// <summary>Diagnóstico: interpreta un resultado oído con una fuente propia (sin reflexión sobre el método privado).</summary>
    internal Task<Heard> TestInterpret(RecognitionResult result, bool rejected, IUtteranceSource source, DateTime startedUtc, CancellationToken ct = default)
        => Interpret(result, rejected, source, startedUtc, ct);

    /// <summary>
    /// Pruebas de interfaz: monta un motor de reconocimiento sin entrada de audio como si estuviera a
    /// media escucha, sin pasar por Sync. Representa una cancelación en curso, para probar la sesión y
    /// la interfaz sin cargar ni descargar gramáticas y sin tocar el micrófono (las gramáticas se
    /// verifican aparte, en --oido-dirigido).
    /// </summary>
    internal void InstallNullInputEngine(SpeechRecognitionEngine e, Grammar lightGrammar)
    {
        presenceAllowed = false;   // Sync tiene esta puerta cerrada: aunque SAPI aceptase reconocer, no abre el micrófono
        engine = e;
        bare = lightGrammar;
        running = true;
        cancelling = true;
        listening = true;
    }

    /// <summary>Alguien empezó a hablarle: prepara el oído fino para que la siguiente frase no espere.</summary>
    public void Warm() => fine?.Warm();

    /// <summary>Suelta lo que la segunda pasada tenga en memoria (el dictado y el oído fino).</summary>
    public void ReleaseSecondPass()
    {
        if (interpreting) { _ = Task.Run(ReleaseIdlePass); return; }
        ReleaseIdlePass();
    }

    private void ReleaseIdlePass()
    {
        lock (passLock)
        {
            passEngine?.Dispose();
            passEngine = null;
        }
        fine?.Release();
    }

    public void Dispose()
    {
        disposed = true;   // el bucle de reintentos, si seguía, se detiene en su próxima vuelta
        interpretation?.Cancel();
        mic.Dispose();
        ReleaseSecondPass();
        wake?.Dispose();
        wake = null;
        engine?.Dispose();
        engine = null;
    }

    /// <summary>Diagnóstico: pasa una grabación (WAV de 16 kHz) por las mismas gramáticas y reglas que el micrófono.</summary>
    public Heard TestWave(byte[] wav, bool attentiveMode = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (recognizer == null) return default;
        using var e = new SpeechRecognitionEngine(recognizer);
        var bareGrammar = LoadLight(e);
        if (attentiveMode)
        {
            e.LoadGrammar(new DictationGrammar { Name = "dictado" });
            bareGrammar.Enabled = true;
        }
        RecognitionResult? rejected = null;
        e.SpeechRecognitionRejected += (_, a) => rejected ??= a.Result;
        e.SetInputToWaveStream(new MemoryStream(wav));
        var result = e.Recognize();
        Trace = "";
        var source = new WaveSource(wav);
        if (result != null) return Interpret(result, false, source, default, ct).GetAwaiter().GetResult();
        return rejected != null ? Interpret(rejected, true, source, default, ct).GetAwaiter().GetResult() : default;
    }

    // ---------- gramáticas ----------

    /// <summary>Carga las gramáticas ligeras y devuelve la de órdenes sin nombre, que solo se activa al estar atenta.</summary>
    private Grammar LoadLight(SpeechRecognitionEngine e)
    {
        var culture = e.RecognizerInfo.Culture;
        // Tolera la pausa natural tras el nombre ("Chispa… ¿qué hora es?") sin dar la frase por terminada.
        e.EndSilenceTimeout = TimeSpan.FromMilliseconds(450);
        e.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(700);

        var named = new Choices();
        var unnamed = new Choices();
        foreach (var reflex in Reflexes.All)
            foreach (string phrase in reflex.Voice)
            {
                named.Add(new SemanticResultValue(phrase, reflex.Id));
                unnamed.Add(new SemanticResultValue(phrase, reflex.Id));
            }
        named.Add(new SemanticResultValue(cfg.FraseConfirmacion, "confirmo"));
        unnamed.Add(new SemanticResultValue(cfg.FraseConfirmacion, "confirmo"));

        var commands = Named(culture);
        commands.Append(new SemanticResultKey("cmd", named));
        e.LoadGrammar(new Grammar(commands) { Name = "cmd" });
        e.LoadGrammar(new Grammar(Named(culture)) { Name = "wake" });

        var bareBuilder = new GrammarBuilder { Culture = culture };
        bareBuilder.Append(new SemanticResultKey("cmd", unnamed));
        var bareGrammar = new Grammar(bareBuilder) { Name = "cmd_libre", Enabled = false };
        e.LoadGrammar(bareGrammar);
        return bareGrammar;
    }

    /// <summary>"[oye|hola|hey] Nombre"</summary>
    private GrammarBuilder Named(System.Globalization.CultureInfo culture)
    {
        var builder = new GrammarBuilder { Culture = culture };
        builder.Append(new GrammarBuilder(new Choices(greetings)), 0, 1);
        builder.Append(cfg.Nombre);
        return builder;
    }

    private SpeechRecognitionEngine CreatePassEngine()
    {
        var e = new SpeechRecognitionEngine(recognizer!);
        var free = Named(e.RecognizerInfo.Culture);
        free.AppendDictation();
        passNamed = new Grammar(free) { Name = "libre" };
        passPlain = new DictationGrammar { Name = "dictado" };
        e.LoadGrammar(passNamed);
        e.LoadGrammar(passPlain);
        return e;
    }

    // ---------- estado del motor ----------

    /// <summary>Lleva el motor al estado deseado sin cortar audio a medias cuando se puede evitar.</summary>
    private void Sync()
    {
        if (!listening || paused || !presenceAllowed) interpretation?.Cancel();
        if (engine == null)
        {
            // Sin motor (aún no abierto, o el dispositivo se perdió): si se quiere escuchar, se reintenta
            // en segundo plano hasta que el micrófono vuelva. Así no hay que reiniciarla.
            if (listening && !paused && presenceAllowed) EnsureReopened();
            return;
        }
        bool want = listening && !paused && presenceAllowed;
        if (running)
        {
            if (!want)
            {
                if (!cancelling)
                {
                    cancelling = true;
                    mic.Stop();
                    wake?.Reset();
                    engine.RecognizeAsyncCancel();
                    if (cfg.Depurar) Log.Write("Oído: en pausa.");
                }
            }
            else if (attentive != dictationLoaded && !updatePending && !cancelling)
            {
                updatePending = true;
                engine.RequestRecognizerUpdate();   // el motor avisa en un punto seguro y guarda el audio mientras tanto
            }
            return;
        }
        ApplyGrammars();
        if (!want) return;
        try
        {
            wake?.Reset();   // el motor empieza la escucha limpio
            mic.Start();   // graba ya, para no perder el principio de la frase
            engine.RecognizeAsync(RecognizeMode.Multiple);
            running = true;
            if (cfg.Depurar) Log.Write("Oído: escuchando.");
        }
        catch (Exception ex) { Log.Write($"No pude empezar a escuchar: {ex.Message}"); }
    }

    /// <summary>El micro cayó a media escucha: se desmonta el motor y se vuelve a montar, sin soltar el oído fino.</summary>
    private void RearmAfterMicLoss()
    {
        if (engine == null || !listening || paused || !presenceAllowed) return;
        Log.Write("Oído: el micrófono falló; rearmando el reconocedor.");
        interpretation?.Cancel();
        mic.Stop();
        wake?.Reset();
        var old = engine;
        engine = null;
        dictation = null;
        running = cancelling = updatePending = dictationLoaded = false;
        try { old.Dispose(); } catch (Exception ex) { Log.Write($"Oído: al desmontar el motor viejo: {ex.Message}"); }
        if (Open()) Sync();
        else EnsureReopened();   // el dispositivo no está ahora: se reintenta hasta que vuelva
    }

    /// <summary>
    /// El micrófono no está disponible (desconectado, o aún sin enchufar): se reintenta en segundo
    /// plano cada dos segundos, durante un par de minutos, y la escucha se reanuda sola en cuanto
    /// vuelve. Solo hay un bucle a la vez.
    /// </summary>
    private async void EnsureReopened()
    {
        if (reopening || recognizer == null) return;   // sin reconocedor en español no hay nada que reintentar
        reopening = true;
        try
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(2000);
                if (disposed || !listening || paused || !presenceAllowed || engine != null) break;
                if (!Open()) continue;
                Log.Write("Oído: el micrófono volvió; reanudando la escucha.");
                Sync();
                break;
            }
        }
        catch (Exception ex) { Log.Write($"Reintentos del oído detenidos: {ex.Message}"); }
        finally { reopening = false; }
    }

    private void ApplyGrammars()
    {
        if (engine == null || attentive == dictationLoaded) return;
        try
        {
            if (attentive)
            {
                dictation = new DictationGrammar { Name = "dictado" };
                engine.LoadGrammar(dictation);
            }
            else if (dictation != null)
            {
                engine.UnloadGrammar(dictation);
                dictation = null;
            }
            bare!.Enabled = attentive;
            dictationLoaded = attentive;
            if (cfg.Depurar) Log.Write(attentive ? "Oído: atenta, dictado cargado." : "Oído: en reposo, dictado soltado.");
        }
        catch (Exception ex) { Log.Write($"No pude cambiar las gramáticas: {ex.Message}"); }
    }

    // ---------- interpretación ----------

    private async void OnResult(RecognitionResult result, bool rejected)
    {
        // Mientras espera a que acabes una frase larga, el motor sigue soltando trozos de esa misma frase: se ignoran.
        if (!listening || !presenceAllowed || paused || interpreting || DateTime.UtcNow < deafUntilUtc) return;
        // El motor de activación ya se encargó de ese «[oye|hola|hey] Nombre»: no hace falta despertar dos veces.
        if (result.Grammar?.Name == "wake" && DateTime.UtcNow - wokeUtc < TimeSpan.FromMilliseconds(2500)) return;
        int began = revision;
        using var request = TraceLog.Begin("entrada_voz", data: new { source = "windows", rechazada = rejected });
        interpreting = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        interpretation = timeout;
        int nameAt = NameIndex(result);
        bool mayReply = attentive || nameAt >= 0 && result.Words[nameAt].Confidence >= cfg.UmbralNombre;
        Task<Heard>? work = null;
        var clock = Stopwatch.StartNew();
        try
        {
            work = Interpret(result, rejected, mic, speechStartedUtc, timeout.Token);
            Heard heard = await work.WaitAsync(timeout.Token);
            TraceLog.Event("voz_interpretada", heard.Value, new { named = heard.Named, kind = (int)heard.Kind, revision_valida = began == revision });
            if (heard.Kind != HeardKind.Nothing && !paused && listening && presenceAllowed && began == revision)
            {
                // Si el motor de activación ya atendió esta misma frase, no se repite.
                if (heard.Named && DateTime.UtcNow - wokeUtc < TimeSpan.FromMilliseconds(2500)) request.Mark("descartada");
                else { wokeUtc = DateTime.UtcNow; HeardSomething?.Invoke(heard); }
            }
            else request.Mark("descartada");
        }
        catch (OperationCanceledException ex)
        {
            request.Fail(ex);
            if (began == revision && listening && presenceAllowed && !paused)
            {
                request.Mark("timeout");
                if (mayReply) InterpretationFailed?.Invoke("La interpretación de la voz tardó demasiado. Puedes repetir la frase; el detalle quedó en el diagnóstico.");
            }
        }
        catch (Exception ex)
        {
            request.Fail(ex); Log.Write($"Error interpretando lo oído: {ex.Message}");
            if (mayReply && began == revision && listening && presenceAllowed && !paused)
                InterpretationFailed?.Invoke("No pude interpretar esa frase. El detalle quedó en el diagnóstico.");
        }
        finally
        {
            if (work is { IsCompleted: false })
                _ = work.ContinueWith(t => { if (t.Exception != null) TraceLog.Failure("voz_tardia", t.Exception.GetBaseException()); }, TaskContinuationOptions.OnlyOnFaulted);
            if (interpretation == timeout) { interpretation = null; interpreting = false; }
            if (began == revision && clock.ElapsedMilliseconds > 700) deafUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        }
    }

    private async Task<Heard> Interpret(RecognitionResult result, bool rejected, IUtteranceSource source, DateTime startedUtc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string grammar = result.Grammar?.Name ?? "";
        Trace = $"1ª [{(rejected ? "rechazado" : grammar)}] conf={result.Confidence:F2} audio={result.Audio?.Duration.TotalSeconds:F1}s «{result.Text}»";

        if (grammar == "dictado")
        {
            // Atenta: el dictado de Windows solo marca dónde empieza y acaba la frase; qué se dijo lo decide el oído fino.
            if (fine == null && (rejected || result.Confidence < cfg.UmbralDictado || result.Text.Trim().Length <= 1)) return default;
            string said = result.Text;
            if (fine != null && WavOf(result) is { } recording && await Task.Run(() => fine.Hear(recording, ct), ct) is { } better)
            {
                Trace += $" · fino «{better}»";
                if (better.Length == 0) return default;
                said = better;
            }
            return new(HeardKind.Speech, said, Named: AfterName(said) != null);
        }

        bool bareCommand = grammar == "cmd_libre";
        if (bareCommand)
        {
            if (fine == null && (rejected || result.Confidence < cfg.UmbralComando)) return default;
        }
        else
        {
            // Todo lo demás empieza por su nombre: si el nombre no se oyó claro, no era para ella.
            int at = NameIndex(result);
            if (at < 0 || result.Words[at].Confidence < cfg.UmbralNombre) return default;
        }

        Heard direct = rejected || result.Confidence < cfg.UmbralComando ? default
            : grammar == "wake" ? new(HeardKind.Wake, Named: true)
            : new(HeardKind.Command, (string)result.Semantics["cmd"].Value, Named: !bareCommand);

        // El reconocedor da la frase por terminada en cuanto encaja una orden. Si la persona siguió hablando
        // ("Chispa, salta… a la siguiente canción"), no era esa orden: se espera a la frase entera.
        // Tras su nombre a secas se espera más: es normal hacer una pausa antes de pedir ("Chispa… ¿qué hora es?"),
        // y si contestara "¿Sí?" enseguida hablaría encima de la petición, que es justo cuando no escucha.
        int patience = grammar == "wake" ? PauseAfterName : 300;
        byte[]? full = result.Audio != null ? await source.FullUtterance(result.Audio, startedUtc, patience).WaitAsync(ct) : null;
        ct.ThrowIfCancellationRequested();
        // Lo mismo si la grabación dura mucho más de lo que se tarda en decir la orden reconocida:
        // "Chispa, abre el navegador y busca recetas…" no es "Chispa, abre la guía".
        bool tooLong = result.Audio != null && result.Audio.Duration.TotalSeconds > 0.4 + result.Text.Length * 0.11;
        if (full == null && !tooLong && direct.Kind != HeardKind.Nothing && result.Confidence >= .8f) return direct;

        // Frase libre, o una orden dicha poco clara: se transcribe la grabación con el dictado.
        byte[]? wav = full ?? WavOf(result);
        if (wav == null) return default;
        int nameAt = NameIndex(result);
        bool nameHeard = !bareCommand && nameAt >= 0 && result.Words[nameAt].Confidence >= Math.Max(.75, cfg.UmbralNombre);   // el oído ligero ya oyó su nombre con claridad
        Heard transcribed = await Task.Run(() => Transcribe(wav, requireName: !bareCommand, nameHeard, ct), ct);
        return transcribed; // Una segunda pasada que rechaza ruido no debe ejecutar la primera suposición.
    }

    /// <summary>
    /// La frase sin su nombre cuando el oído fino lo escribió de otra manera ("Chiva, ¿qué hora es?" → "¿qué hora es?").
    /// Si la primera palabra no se le parece, se deja la frase entera.
    /// </summary>
    private string WithoutMisheardName(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        string first = Reflexes.Normalize(words[0]), name = Reflexes.Normalize(cfg.Nombre);
        bool misheard = first.Length > 0 && Distance(first, name) <= Math.Max(2, name.Length / 3);
        return (misheard ? string.Join(' ', words.Skip(1)) : text).Trim();
    }

    /// <summary>Cuántas letras hay que cambiar, quitar o poner para pasar de una palabra a otra.</summary>
    private static int Distance(string a, string b)
    {
        int[] row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    private int NameIndex(RecognitionResult result)
    {
        for (int i = 0; i < result.Words.Count && i < 2; i++)
            if (string.Equals(result.Words[i].Text, cfg.Nombre, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static byte[]? WavOf(RecognitionResult result)
    {
        var audio = result.Audio;
        if (audio == null || audio.Duration < TimeSpan.FromMilliseconds(350)) return null;
        using var wav = new MemoryStream();
        audio.WriteToWaveStream(wav);
        return wav.ToArray();
    }

    /// <summary>Lo que queda de una frase tras su nombre ("Chispa, salta" → "salta"), o null si no empieza por él.</summary>
    private string? AfterName(string text)
    {
        string name = Reflexes.Normalize(cfg.Nombre);
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(2, words.Length); i++)   // el nombre puede ir tras un "oye" o un "hola"
            if (Reflexes.Similarity(Reflexes.Normalize(words[i]), name) >= 0.85) return string.Join(' ', words.Skip(i + 1)).Trim();
        return null;
    }

    /// <summary>
    /// Segunda pasada: transcribe una grabación entera. Usa el oído fino (Whisper) si está instalado
    /// y, si no, el dictado de Windows.
    /// </summary>
    /// <param name="nameHeard">El oído ligero ya oyó su nombre con claridad al principio de la frase.</param>
    private Heard Transcribe(byte[] wav, bool requireName, bool nameHeard = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (fine?.Hear(wav, ct) is { } text)
        {
            Trace += $" · fino «{text}»";
            if (text.Length == 0) return default;
            if (!requireName) return new(HeardKind.Speech, text);
            if (AfterName(text) is not { } rest)
            {
                // El oído fino no escribió su nombre. Si el ligero solo creyó oírlo, no era para ella.
                // Si lo oyó claro, sí lo era: se queda con la petición en vez de tirarla.
                if (!nameHeard) return default;
                rest = WithoutMisheardName(text);
            }
            return rest.Length == 0 ? new(HeardKind.Wake) : new(HeardKind.Speech, rest, Named: true);
        }

        lock (passLock)
        {
            ct.ThrowIfCancellationRequested();
            passEngine ??= CreatePassEngine();
            passNamed!.Enabled = requireName;
            passPlain!.Enabled = !requireName;
            RecognitionResult? second;
            try
            {
                passEngine.SetInputToWaveStream(new MemoryStream(wav));
                second = passEngine.Recognize(TimeSpan.FromSeconds(10));
            }
            finally { passEngine.SetInputToNull(); }
            ct.ThrowIfCancellationRequested();
            Trace += second == null ? " · 2ª (nada)" : $" · 2ª conf={second.Confidence:F2} audio={second.Audio?.Duration.TotalSeconds:F1}s «{second.Text}»";
            if (second == null) return default;

            if (!requireName)
                return second.Confidence >= cfg.UmbralDictado && second.Text.Trim().Length > 1 ? new(HeardKind.Speech, second.Text) : default;

            int at = NameIndex(second);
            if (at < 0 || second.Words[at].Confidence < cfg.UmbralNombre) return default;
            string rest = string.Join(' ', second.Words.Skip(at + 1).Select(w => w.Text)).Trim();
            return rest.Length == 0 ? new(HeardKind.Wake) : new(HeardKind.Speech, rest, Named: true);
        }
    }
}
