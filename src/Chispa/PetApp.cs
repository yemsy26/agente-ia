using System.Diagnostics;

namespace Chispa;

/// <summary>
/// Une cuerpo, voz, oído y mente, y decide cuándo está despierta y cuándo en reposo.
/// Dormida solo quedan el micrófono esperando su nombre y el reloj que mira si sigues delante.
/// El menú de la bandeja vive en <see cref="TrayMenu"/>; lo que oye, en <see cref="PetApp.Oido"/>,
/// y el ciclo de vigilia, en <see cref="PetApp.Vida"/>. Aquí quedan el cableado y el cuerpo (IBody).
/// </summary>
internal sealed partial class PetApp : ApplicationContext, IBody
{
    private readonly Config cfg;
    private readonly PetForm pet;
    private readonly BubbleForm bubble = new();
    private readonly TrayMenu trayMenu;
    private readonly Mouth mouth;
    private readonly Ears ears;
    private readonly Memory memory = new();
    private readonly Mind mind;
    private readonly ConversationSession session = new();
    private DateTime speakingSince;
    private string? speakingRequest;
    private readonly CareClock care;
    private readonly System.Windows.Forms.Timer presenceTimer = new() { Interval = 5000 };
    private CarePromptForm? carePrompt;
    private CompanionSettingsForm? companionSettings;
    private bool wasPresent = true;
    private CancellationTokenSource? manualListening;
    private readonly System.Windows.Forms.Timer life = new();        // gestos espontáneos; parado mientras duerme
    private readonly System.Windows.Forms.Timer attention = new();   // rato "atenta" después de hablar
    private readonly List<System.Windows.Forms.Timer> oneShots = []; // los "dentro de un rato" de After, cancelables al cerrar
    private ChatForm? chat;
    private DateTime lastActivity = DateTime.UtcNow;
    private bool asleep, hidden, sleepy;
    private bool conversing;   // modo conversación: el rato atenta es largo y se renueva con cada frase
    private bool quietNext;    // tras despedirse: al terminar de hablar deja de escuchar frases sueltas
    private bool expectsReply; // acaba de contestar "¿Sí?" o de preguntar algo: lo siguiente que oiga es para ella
    private int unnamedTurns;  // cuántas veces seguidas ha dado por suya una frase sin su nombre

    // ---------- acceso interno para las pruebas de interfaz, sin reflexión sobre campos privados ----------

    internal Mouth Mouth => mouth;
    internal Ears Ears => ears;
    internal PetForm Pet => pet;
    internal BubbleForm Bubble => bubble;
    internal ConversationSession Session => session;
    internal System.Windows.Forms.Timer PresenceTimer => presenceTimer;
    internal ContextMenuStrip Menu => trayMenu.Menu;
    internal ChatForm? Chat => chat;

    public PetApp(Config cfg, bool firstRun)
    {
        this.cfg = cfg;
        TraceLog.Configure(cfg);
        pet = new PetForm(cfg.Aspecto, cfg.Atajo, cfg.PosX, cfg.AnimacionReposo);
        _ = pet.Handle;   // crea la ventana ya: registra el atajo antes de montar el menú
        mouth = new Mouth(cfg);
        ears = new Ears(cfg);
        mind = new Mind(cfg, this, memory);
        mind.ProgressChanged = status =>
        {
            if (!hidden && mind.Busy) bubble.ShowText(status, pet.HeadPoint);
        };
        care = new CareClock(cfg.Acompanamiento);
        mind.PersonalRequest = PersonalRequest;
        presenceTimer.Tick += (_, _) => CheckPresence();
        presenceTimer.Start();

        pet.Moved += () => bubble.Follow(pet.HeadPoint);
        pet.Clicked += () =>
        {
            UserActivity();
            pet.Hop();
        };
        pet.DoubleClicked += OpenChat;
        pet.MenuRequested += ShowMenu;
        pet.HotkeyPressed += () =>
        {
            if (cfg.EscuchaContinua) OpenChat();
            else ListenOnce();
        };

        mouth.Started += () =>
        {
            speakingSince = DateTime.UtcNow;
            speakingRequest = TraceLog.CurrentId;
            TraceLog.Event("voz_inicio", data: new { state = "hablando" });
            ears.Paused = true;
            pet.SetListening(false);
            pet.SetTalking(true);
        };
        mouth.Finished += () =>
        {
            if (mouth.Speaking) return; // un fin en cola no pertenece a una reproducción nueva
            TraceLog.EventFor(speakingRequest, "voz_fin", data: new { ms = (DateTime.UtcNow - speakingSince).TotalMilliseconds });
            pet.SetTalking(false);
            bubble.HideAfter(2500);
            // Espera un instante antes de volver a escuchar: si no, oye el final de su propia voz en el altavoz
            // y se pone a contestarse a sí misma.
            After(400, () =>
            {
                if (mouth.Speaking || manualListening != null) return;
                ears.Paused = false;
                StartAttention();
            });
        };

        ears.HeardSomething += OnHeard;
        ears.InterpretationFailed += message => Say(message);
        ears.SpeechStarted += () =>
        {
            if (session.Active(DateTime.UtcNow)) Touch();
            // El usuario empezó a hablar: que el rato de atención no se acabe a media frase.
            if (!attention.Enabled) return;
            attention.Stop();
            attention.Start();
        };

        life.Tick += (_, _) => LifeTick();
        attention.Tick += (_, _) =>
        {
            if (mouth.Speaking || mind.Busy || ears.Processing || manualListening != null) return;
            StopAttention();
        };

        trayMenu = new TrayMenu(cfg, this, pet.ActiveHotkey);

        // Una línea por arranque: si algún día falta una voz, aquí se ve qué encontró y dónde buscó.
        Log.Write($"Voces: {string.Join(", ", VoiceCatalog.All().Select(v => v.Id))} (carpeta {VoiceCatalog.Folder}); en uso: {VoiceCatalog.Current(cfg).Id}. Oído: {ears.HearingName}. Razón: {(Reason.Available ? "motor lógico" : $"sin motor (lo busqué en {Reason.EnginePath})")}.");

        pet.FadeIn();
        WakeUp();
        if (cfg.EscuchaContinua)
        {
            ears.Open();   // si aún no hay micrófono, el oído reintenta solo en segundo plano
            ears.Listening = true;
        }
        After(500, () => { if (wasPresent) Greet(firstRun); });
        After(1200, mind.RestoreReminders);   // los avisos pendientes que sobrevivieron al cierre
        CheckPresence();

        // Para pruebas y demostraciones: frases separadas por ";" que "oye" sola, una cada tres segundos.
        string[] script = (Environment.GetEnvironmentVariable("CHISPA_GUION") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < script.Length; i++)
        {
            string phrase = script[i];
            After(3000 + i * 3000, () =>
            {
                UserActivity();
                _ = mind.Hear(phrase);
            });
        }

        // Con repositorio configurado: mira sola, al rato de arrancar, si salió una versión nueva.
        if (cfg.Actualizaciones.Trim().Length > 0)
            After(25000, async () =>
            {
                if (await Updater.CheckAsync(cfg) is { } found)
                    Say($"Hay una versión nueva de mí: {found.Version.ToString(3)}. En mi menú, «Buscar actualizaciones» te la muestra.");
            });
    }

    // ---------- IBody: lo que la mente le pide al cuerpo ----------

    public void Say(string text)
    {
        Touch();
        expectsReply = text.TrimEnd().EndsWith('?');
        mind.Said(text);
        if (!hidden) bubble.ShowText(text, pet.HeadPoint);
        if (!cfg.Silencio && mouth.Say(text)) return;   // al terminar de hablar se oculta el globo y empieza a atender

        if (hidden) trayMenu.ShowBalloon(5000, cfg.Nombre, text);
        bubble.HideAfter(Math.Clamp(1800 + text.Length * 55, 2500, 15000));
        StartAttention();
    }

    public string Act(string action)
    {
        Touch();
        switch (action)
        {
            case "saltar": Appear(); pet.Jump(); break;
            case "bailar": Appear(); pet.Dance(); break;
            case "saludar": Appear(); pet.WaveHand(); break;
            case "venir": Appear(); pet.WalkTo(Cursor.Position); break;
            case "aparecer": Appear(); pet.Hop(); break;
            case "esconderse": HideBody(); break;
            case "dormir": GetSleepy(); break;
            case "callar": mouth.Stop(); break;
            default: return $"No sé hacer «{action}».";
        }
        return "Hecho.";
    }

    public string ChangeSize(string tamano)
    {
        Touch();
        double before = cfg.Aspecto.Tamano;
        cfg.Aspecto.Tamano = Reflexes.Normalize(tamano) switch
        {
            "mas grande" => Math.Min(2.2, before * 1.25),
            "mas pequeno" or "mas pequena" => Math.Max(.35, before / 1.25),
            "normal" => .8,
            _ => before,
        };
        Appear();
        LookChanged();
        return cfg.Aspecto.Tamano > before ? "Ahora soy más grande." : cfg.Aspecto.Tamano < before ? "Ahora soy más pequeña." : "Ya tengo ese tamaño o llegué al límite.";
    }

    public void Thinking(bool on) => pet.SetThinking(on);

    public void SetMuted(bool muted)
    {
        if (muted) mouth.Stop();
        trayMenu.SetMuted(muted);   // el propio menú guarda el cambio
    }

    public void Converse(bool on)
    {
        conversing = on;
        if (!on) { quietNext = true; session.Close(); }
        else
        {
            session.UserSpoke(DateTime.UtcNow, cfg.SegundosConversacion);
            if (!ears.Listening)
            {
                ears.Open();   // si el micrófono se perdió, reintenta solo
                ears.Listening = true;
            }
        }
    }

    public void Later(TimeSpan delay, Action action) => After((int)Math.Clamp(delay.TotalMilliseconds, 1, int.MaxValue), action);

    public void OpenGuide() => Guide.Open();

    public void Rename(string name)
    {
        cfg.Nombre = name;
        cfg.Save();
        trayMenu.Text = name;
        chat?.Dispose();
        chat = null;
        ears.Reload();   // su nombre es la palabra que la despierta
    }

    public void TuneVoice(int pitchSteps, int speedSteps, bool reset = false)
    {
        cfg.TonoVoz = reset ? 1.0 : Math.Clamp(cfg.TonoVoz + 0.06 * pitchSteps, 0.8, 1.4);
        cfg.VelocidadVoz = reset ? 1 : Math.Clamp(cfg.VelocidadVoz + 2 * speedSteps, -6, 8);
        cfg.Save();
        mouth.Reconfigure();
    }

    public Task<LaunchResult> Launch(string command, CancellationToken ct = default) => Device.LaunchAsync(command, ct);

    public void PressKey(ushort key, int times = 1) => Native.PressKey(key, times);

    public void TypeText(string text, bool enter)
    {
        Native.TypeText(text);
        if (enter) Native.PressKey(Native.VK_RETURN);
    }

    public IReadOnlyList<WindowRef> OpenWindows() => Device.OpenWindows();

    public void CloseWindow(WindowRef window) => Native.RequestClose(window.Handle);

    public byte[]? Screenshot() => Device.Screenshot();

    public string ChangeVoice(string which)
    {
        var all = VoiceCatalog.All();
        int at = all.FindIndex(v => v.Id == VoiceCatalog.Current(cfg).Id);
        VoiceOption? next = which switch
        {
            "siguiente" => all[(at + 1) % all.Count],
            "anterior" => all[(at - 1 + all.Count) % all.Count],
            // la siguiente de ese tipo, dando la vuelta a la lista
            "hombre" or "mujer" => Enumerable.Range(1, all.Count).Select(i => all[(at + i) % all.Count]).FirstOrDefault(v => v.Gender == which),
            _ => all.FirstOrDefault(v => v.Id.Equals(which, StringComparison.OrdinalIgnoreCase)),
        };
        if (next == null) return "";
        cfg.Voz = next.Id;
        cfg.Save();
        mouth.Reconfigure();
        return next.Label;
    }

    private void LookChanged()
    {
        pet.Refit();
        trayMenu.Icon = PetRenderer.RenderIcon();
        cfg.Save();
    }

    // ---------- comportamientos que pide el menú de la bandeja ----------

    internal void SetSilence(bool silenced)
    {
        cfg.Silencio = silenced;
        cfg.Save();
    }

    internal void PickVoice(string id)
    {
        UserActivity();
        mind.Own.SwitchVoice(id);
    }

    internal void PickBrain(string id, string said)
    {
        UserActivity();
        cfg.Cerebro = id;
        cfg.Save();
        mind.Reset();   // suelta el modelo que hubiera y empieza la charla de nuevo
        Say(said);
    }

    internal void DoGesture(string gesture)
    {
        UserActivity();
        Act(gesture);
    }

    internal void SetIdleAnimation(bool on)
    {
        cfg.AnimacionReposo = on;
        pet.SetIdleAnimation(on);
        cfg.Save();
    }

    internal async Task OpenReport()
    {
        try
        {
            string report = await SupportDiagnostics.WriteReport(cfg);
            await Device.LaunchAsync(report);
        }
        catch (Exception ex)
        {
            TraceLog.Failure("informe", ex);
            Say("No pude abrir el informe. El error quedó en el registro.");
        }
    }

    // ---------- actualizaciones ----------

    /// <summary>
    /// Mira en GitHub si hay una versión más nueva y, si la hay, la anuncia y ofrece abrir la página
    /// de descarga. Abrirla espera la confirmación hablada de siempre; nada se descarga ni se instala solo.
    /// </summary>
    internal async void CheckUpdates()
    {
        if (cfg.Actualizaciones.Trim().Length == 0)
        {
            Say("Aún no tengo repositorio de actualizaciones. Cuando me publiques en GitHub, apunta «dueño/repositorio» en el campo actualizaciones de mi configuración.");
            return;
        }
        Say("Déjame mirar si hay una versión nueva…");
        try
        {
            ReleaseInfo? found = await Updater.CheckAsync(cfg);
            if (found == null)
            {
                Say($"Vas al día: la versión {Updater.ThisVersion} es la más nueva.");
                return;
            }
            Say($"Hay una versión nueva: {found.Value.Version.ToString(3)}.");
            if (await mind.Confirm($"abrir la página de descarga de la versión {found.Value.Version.ToString(3)}"))
            {
                _ = Device.LaunchAsync(found.Value.Url);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"No pude mirar actualizaciones: {ex.Message}");
            Say("No pude mirar las actualizaciones. Puede que no haya red o que el repositorio no exista todavía.");
        }
    }

    private void ShowMenu() => trayMenu.ShowAtCursor();

    // ---------- utilidades ----------

    /// <summary>Ejecuta algo una sola vez dentro de un rato. Los temporizadores pendientes se cancelan al cerrar.</summary>
    private void After(int ms, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = ms };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            lock (oneShots) oneShots.Remove(timer);
            action();
        };
        lock (oneShots) oneShots.Add(timer);
        timer.Start();
    }

    internal void Quit()
    {
        if (cfg.Depurar) Log.Write("Cerrada desde el menú.");
        trayMenu.Dispose();
        cfg.PosX = pet.CenterX;
        cfg.Save();
        presenceTimer.Dispose();
        life.Dispose(); attention.Dispose();
        lock (oneShots)
        {
            foreach (var timer in oneShots) { timer.Stop(); timer.Dispose(); }
            oneShots.Clear();
        }
        companionSettings?.Dispose();
        CloseCarePrompt();
        manualListening?.Cancel();
        mind.Cancel();
        mind.Flush();   // que lo último que se habló quede en el diario
        ears.Dispose();
        mouth.Dispose();
        chat?.Dispose();
        bubble.Dispose();
        pet.Dispose();
        ExitThread();
    }
}
