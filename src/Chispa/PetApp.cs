using System.Diagnostics;

namespace Chispa;

/// <summary>
/// Une cuerpo, voz, oído y mente, y decide cuándo está despierta y cuándo en reposo.
/// Dormida solo quedan el micrófono esperando su nombre y el reloj que mira si sigues delante.
/// </summary>
internal sealed class PetApp : ApplicationContext, IBody
{
    private readonly Config cfg;
    private readonly PetForm pet;
    private readonly BubbleForm bubble = new();
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem alwaysListenItem = new("Escuchar siempre mi nombre") { CheckOnClick = true };
    private readonly ToolStripMenuItem voiceItem = new("Hablar en voz alta") { CheckOnClick = true };
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
    internal ContextMenuStrip Menu => menu;
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

        BuildMenu();
        tray.Text = cfg.Nombre;
        tray.Icon = PetRenderer.RenderIcon();
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            UserActivity();
            if (hidden) Appear();
            else HideBody();
        };
        tray.Visible = true;

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
    }

    // ---------- IBody: lo que la mente le pide al cuerpo ----------

    public void Say(string text)
    {
        Touch();
        expectsReply = text.TrimEnd().EndsWith('?');
        mind.Said(text);
        if (!hidden) bubble.ShowText(text, pet.HeadPoint);
        if (!cfg.Silencio && mouth.Say(text)) return;   // al terminar de hablar se oculta el globo y empieza a atender

        if (hidden) tray.ShowBalloonTip(5000, cfg.Nombre, text, ToolTipIcon.None);
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
        voiceItem.Checked = !muted;   // el propio menú guarda el cambio
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
        tray.Text = name;
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
        var oldIcon = tray.Icon;
        tray.Icon = PetRenderer.RenderIcon();
        oldIcon?.Dispose();
        cfg.Save();
    }

    // ---------- lo que oye ----------

    internal void OnHeard(Heard heard)
    {
        if (manualListening != null) return;
        if (!heard.Named && heard.Kind != HeardKind.Wake && !session.Active(DateTime.UtcNow) && !expectsReply)
        { TraceLog.Event("voz_ignorada", data: new { reason = "sin_sesion" }); return; }
        session.UserSpoke(DateTime.UtcNow, cfg.SegundosConversacion);
        conversing = true;
        if (cfg.Depurar) Log.Write($"Oí {heard.Kind} «{heard.Value}» · {ears.Trace}");
        UserActivity();
        mouth.Warm();   // le están hablando: que la voz esté lista para contestar
        if (heard.Kind != HeardKind.Command)
        {
            ears.Warm();    // prepara la frase libre que venga después
        }
        // Una orden fija ya reconocida no necesita cargar Whisper ni el modelo de lenguaje.
        switch (heard.Kind)
        {
            case HeardKind.Wake:
                // Siempre al registro: si un día se queda en "¿Sí?" con la petición ya dicha, aquí se ve qué llegó a oír.
                Log.Write($"Oí solo mi nombre · {ears.Trace}");
                unnamedTurns = 0;
                Appear();
                pet.Hop();
                Say("¿Sí?");
                break;
            case HeardKind.Command:
                expectsReply = false;
                if (heard.Value == "confirmo") mind.ResolveConfirmation(true);
                else if (Reflexes.All.FirstOrDefault(r => r.Id == heard.Value) is { } reflex)
                    _ = mind.Hear(reflex.Voice[0], heard.Named ? HeardFrom.Named : HeardFrom.Conversation);
                break;
            case HeardKind.Speech:
            {
                // Sin su nombre, una frase suelta puede ser ruido o ir para otra persona. Pero si ella acaba de
                // contestar "¿Sí?" o de preguntar algo, lo que viene a continuación es la respuesta: va para ella.
                // Con tope: tres seguidas sin oír su nombre y vuelve a exigirlo, para no acabar charlando con la tele.
                bool expected = !heard.Named && expectsReply && unnamedTurns < 3;
                unnamedTurns = heard.Named ? 0 : expected ? unnamedTurns + 1 : unnamedTurns;
                bool addressed = heard.Named || expected || session.Active(DateTime.UtcNow);
                expectsReply = false;
                _ = mind.Hear(heard.Value, heard.Named ? HeardFrom.Named : addressed ? HeardFrom.Conversation : HeardFrom.Background);
                break;
            }
        }
    }

    /// <summary>
    /// Tras hablar sigue un rato atenta, para poder contestarle sin repetir su nombre. Las ondas verdes
    /// junto a su cabeza lo indican. En modo conversación el rato es largo y se renueva con cada frase.
    /// </summary>
    private void StartAttention()
    {
        if (manualListening != null) return;
        if (session.Opened) { session.ReplyFinished(DateTime.UtcNow, cfg.SegundosConversacion); conversing = true; }
        int seconds = conversing ? cfg.SegundosConversacion : cfg.SegundosAtenta;
        if (quietNext || !ears.Available || !ears.Listening || seconds <= 0)
        {
            quietNext = false;
            StopAttention();
            return;
        }
        ears.Attentive = true;
        pet.SetListening(true);
        attention.Stop();
        attention.Interval = seconds * 1000;
        attention.Start();
    }

    internal void StopAttention()
    {
        attention.Stop();
        conversing = false;
        session.Close();
        expectsReply = false;
        unnamedTurns = 0;
        ears.Attentive = false;
        pet.SetListening(false);
        if (!cfg.EscuchaContinua) ears.Listening = false;
    }

    /// <summary>Con el micrófono apagado: lo abre solo para una petición.</summary>
    private async void ListenOnce()
    {
        UserActivity();
        mind.Cancel();
        ears.PresenceAllowed = true;
        Appear();
        if (ears.CanTranscribeExplicitly)
        {
            using var inputTrace = TraceLog.Begin("entrada_voz", data: new { source = "manual" });
            manualListening?.Cancel();
            using var mine = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            manualListening = mine;
            ears.Paused = true;
            mouth.Stop();
            pet.SetListening(true);
            bubble.ShowText("Te escucho. Habla ahora; al terminar haré tu petición.", pet.HeadPoint);
            try
            {
                string? text = await ears.CaptureOnce(mine.Token);
                mine.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(text)) Say("No capté una frase clara. Puedes intentarlo de nuevo o escribirme.");
                else
                {
                    session.UserSpoke(DateTime.UtcNow, cfg.SegundosConversacion);
                    conversing = true;
                    manualListening = null;
                    ears.Paused = false;
                    pet.SetListening(false);
                    ears.Open();   // si el micrófono se perdió durante la captura, reintenta solo
                    ears.Listening = true;
                    await mind.Hear(text, HeardFrom.Named);
                }
            }
            catch (OperationCanceledException ex) { inputTrace.Fail(ex); }
            catch (Exception ex) { inputTrace.Fail(ex); Log.Write($"Escucha a demanda: {ex.Message}"); Say("No pude usar el micrófono. Comprueba su permiso en Windows o escríbeme."); }
            finally
            {
                if (manualListening == mine) { manualListening = null; ears.Paused = mouth.Speaking; pet.SetListening(false); }
            }
            return;
        }
        if (!ears.Open())
        {
            Say(ears.Problem);
            return;
        }
        ears.Listening = true;
        Say("Te escucho. ¿Qué necesitas?");
    }

    internal void OpenChat()
    {
        UserActivity();
        mouth.Warm();   // mientras escribes, prepara la voz para contestar
        Appear();
        bubble.HideNow();
        if (chat == null)
        {
            chat = new ChatForm(cfg.Nombre);
            chat.Submitted += text =>
            {
                UserActivity();
                manualListening?.Cancel();
                if (cfg.EscuchaContinua) { session.UserSpoke(DateTime.UtcNow, cfg.SegundosConversacion); conversing = true; }
                _ = mind.Hear(text);
            };
        }
        chat.Open(pet.HeadPoint);
    }

    // ---------- despierta / dormida ----------

    private bool Busy => manualListening != null || ears.Processing || mouth.Speaking || mind.Busy;

    private void Greet(bool firstRun)
    {
        pet.WaveHand();
        string hello = firstRun
            ? $"¡Hola! Soy {cfg.Nombre}. Di mi nombre y pídeme algo, o hazme doble clic para escribirme."
            : "¡Hola de nuevo!";
        if (cfg.EscuchaContinua && !ears.Available)
            hello += ears.CanTranscribeExplicitly ? " Windows no puede escuchar mi nombre. Usa Escucharme ahora o escríbeme." : $" No puedo oírte: {ears.Problem} Hazme doble clic para escribirme.";
        Say(hello);
    }

    /// <summary>Hay actividad (suya o del usuario): no es momento de dormirse.</summary>
    private void Touch([System.Runtime.CompilerServices.CallerMemberName] string cause = "")
    {
        lastActivity = DateTime.UtcNow;
        if (!asleep) return;
        if (cfg.Depurar) Log.Write($"Despierta por: {cause}.");
        WakeUp();
    }

    /// <summary>El usuario ha hecho algo: además de despertarla, cancela las ganas de dormir.</summary>
    private void UserActivity([System.Runtime.CompilerServices.CallerMemberName] string cause = "")
    {
        sleepy = false;
        Touch(cause);
    }

    private void WakeUp()
    {
        asleep = false;
        pet.SetAsleep(false);
        life.Interval = 4000;
        life.Start();
    }

    private void GetSleepy()
    {
        sleepy = true;
        life.Stop();
        life.Interval = 1200;
        life.Start();
    }

    private void LifeTick()
    {
        life.Interval = Random.Shared.Next(5000, 12000);
        if (Busy)
        {
            lastActivity = DateTime.UtcNow;
            return;
        }
        if (sleepy || (!attention.Enabled && (DateTime.UtcNow - lastActivity).TotalSeconds >= cfg.DormirTrasSegundos))
        {
            GoToSleep();
            return;
        }
        if (hidden) return;
        switch (Random.Shared.Next(10))
        {
            case < 5: pet.Blink(); break;
            case < 8: pet.Glance(); break;
            case 8: pet.Blink(); break;
            default: pet.Glance(); break;
        }
    }

    /// <summary>Modo cero: para todos los temporizadores y devuelve la memoria que no necesita.</summary>
    private void GoToSleep()
    {
        if (asleep) return;
        if (Busy) return;
        asleep = true;
        sleepy = false;
        life.Stop();
        StopAttention();
        bubble.HideNow();
        pet.SetAsleep(true);
        cfg.PosX = pet.CenterX;   // recuerda dónde se quedó aunque el equipo se apague sin cerrarla
        cfg.Save();
        _ = mind.Sleep(() => asleep);   // apunta lo pendiente en el diario y suelta el modelo
        mouth.Release();
        ears.ReleaseSecondPass();
        After(2000, () =>
        {
            if (!asleep) return;
            Native.TrimMemory();   // un poco después, cuando el oído ya soltó el dictado
            using var me = Process.GetCurrentProcess();
            Log.Write($"Dormida: {me.WorkingSet64 / 1048576.0:F0} MB en uso.");
        });
    }

    private void Appear()
    {
        if (!hidden) return;
        hidden = false;
        pet.FadeIn();
    }

    private void HideBody()
    {
        if (hidden) return;
        hidden = true;
        bubble.HideNow();
        pet.FadeOut();
        GetSleepy();   // escondida no hay nada que animar
    }

    // ---------- menú ----------

    private void BuildMenu()
    {
        if (pet.ActiveHotkey.Length > 0)
        {
            menu.Items.Add(new ToolStripMenuItem($"{pet.ActiveHotkey} me llama desde cualquier ventana") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add("Guía: qué puedo decirle", null, (_, _) => Guide.Open());
        menu.Items.Add("Escribirle…", null, (_, _) => OpenChat());
        menu.Items.Add("Perfil y acompañamiento…", null, (_, _) => OpenCompanionSettings());
        menu.Items.Add("Silenciar acompañamiento por hoy", null, (_, _) =>
        {
            cfg.Acompanamiento.SilencioHastaUtc = DateTime.Today.AddDays(1).ToUniversalTime();
            care.Reset(); CloseCarePrompt(); cfg.Save();
        });
        menu.Items.Add("Escucharme ahora", null, (_, _) => ListenOnce());
        menu.Items.Add("Cancelar petición", null, (_, _) => { manualListening?.Cancel(); mind.Cancel(); mouth.Stop(); StopAttention(); });
        menu.Items.Add("Diagnóstico: generar informe", null, async (_, _) =>
        {
            try { string report = await SupportDiagnostics.WriteReport(cfg); await Device.LaunchAsync(report); }
            catch (Exception ex) { TraceLog.Failure("informe", ex); Say("No pude abrir el informe. El error quedó en el registro."); }
        });
        menu.Items.Add("Conversar (sin repetir su nombre)", null, (_, _) =>
        {
            UserActivity();
            mind.Reflex("conversar");
        });

        alwaysListenItem.Checked = cfg.EscuchaContinua;
        alwaysListenItem.CheckedChanged += (_, _) =>
        {
            cfg.EscuchaContinua = alwaysListenItem.Checked;
            cfg.Save();
            if (!cfg.EscuchaContinua) StopAttention();
            else
            {
                if (!ears.Open()) Say(ears.Problem);   // avisa, pero el oído reintentará solo
                ears.Listening = true;
            }
        };
        menu.Items.Add(alwaysListenItem);

        voiceItem.Checked = !cfg.Silencio;
        voiceItem.CheckedChanged += (_, _) =>
        {
            cfg.Silencio = !voiceItem.Checked;
            cfg.Save();
        };
        menu.Items.Add(voiceItem);

        // Elegir voz: al pulsar una, se presenta con ella para oírla.
        var voices = new ToolStripMenuItem("Voz");
        foreach (var option in VoiceCatalog.All())
            voices.DropDownItems.Add(new ToolStripMenuItem(option.Menu, null, (_, _) =>
            {
                UserActivity();
                mind.Own.SwitchVoice(option.Id);
            }) { Tag = option.Id });
        voices.DropDownOpening += (_, _) =>
        {
            string current = VoiceCatalog.Current(cfg).Id;
            foreach (ToolStripMenuItem item in voices.DropDownItems) item.Checked = (string?)item.Tag == current;
        };
        menu.Items.Add(voices);

        // Con qué piensa: su cerebro propio, o además un modelo de lenguaje local para lo que ese no entienda.
        var brains = new ToolStripMenuItem("Cerebro");
        void Brain(string label, string id, string said) =>
            brains.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                UserActivity();
                cfg.Cerebro = id;
                cfg.Save();
                mind.Reset();   // suelta el modelo que hubiera y empieza la charla de nuevo
                Say(said);
            }) { Tag = id });
        Brain("Con modelo local: entiende lo que le digas (recomendado)", "auto", "Vale. Lo que no entienda yo sola se lo paso al modelo local. Si Ollama no está abierto, sigo con mi cerebro propio.");
        Brain("Solo el propio: instantáneo, pero entiende poco", "propio", "Vale, pienso solo con mi cerebro propio.");
        brains.DropDownOpening += (_, _) =>
        {
            string current = cfg.Cerebro.Trim().ToLowerInvariant() is "propio" ? "propio" : "auto";
            foreach (ToolStripMenuItem item in brains.DropDownItems) item.Checked = (string?)item.Tag == current;
        };
        menu.Items.Add(brains);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Submenu("Tamaño", ["más grande", "más pequeña", "normal"], v => ChangeSize(v)));
        menu.Items.Add(Submenu("Gestos", ["saludar", "saltar", "bailar", "venir"], v => { UserActivity(); Act(v); }));
        var idleAnimation = new ToolStripMenuItem("Animar el avatar en reposo") { Checked = cfg.AnimacionReposo, CheckOnClick = true };
        idleAnimation.CheckedChanged += (_, _) =>
        {
            cfg.AnimacionReposo = idleAnimation.Checked;
            pet.SetIdleAnimation(cfg.AnimacionReposo);
            cfg.Save();
        };
        menu.Items.Add(idleAnimation);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Dormir", null, (_, _) => GetSleepy());
        menu.Items.Add("Esconder o mostrar", null, (_, _) =>
        {
            UserActivity();
            if (hidden) Appear();
            else HideBody();
        });
        menu.Items.Add("Abrir carpeta de datos", null, (_, _) =>
        {
            Directory.CreateDirectory(Config.DataDir);
            Process.Start(new ProcessStartInfo(Config.DataDir) { UseShellExecute = true });
        });
        menu.Items.Add("Salir", null, (_, _) => Quit());
    }

    private static ToolStripMenuItem Submenu(string title, IEnumerable<string> options, Action<string> choose)
    {
        var item = new ToolStripMenuItem(title);
        foreach (string option in options)
            item.DropDownItems.Add(char.ToUpper(option[0]) + Label(option)[1..], null, (_, _) => choose(option));
        return item;
    }

    /// <summary>Los nombres internos van sin tilde; así se le enseñan al usuario.</summary>
    private static string Label(string option) => option == "marron" ? "marrón" : option;

    private void ShowMenu()
    {
        // Sin esto el menú no se cierra al hacer clic fuera: la mascota nunca tiene el foco.
        Native.SetForegroundWindow(menu.Handle);
        menu.Show(Cursor.Position);
    }

    internal void Quit()
    {
        if (cfg.Depurar) Log.Write("Cerrada desde el menú.");
        tray.Visible = false;
        tray.Dispose();
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
        menu.Dispose();
        pet.Dispose();
        ExitThread();
    }

    private void OpenCompanionSettings()
    {
        UserActivity();
        if (companionSettings is { IsDisposed: false }) { companionSettings.Activate(); return; }
        companionSettings = new CompanionSettingsForm(cfg, () => { care.Reset(); CloseCarePrompt(); });
        companionSettings.Show();
    }

    private void CheckPresence()
    {
        double idle = Presence.IdleSeconds();
        bool present = double.IsFinite(idle) && (idle < Math.Clamp(cfg.Acompanamiento.MinutosAusencia, 1, 30) * 60 || session.Active(DateTime.UtcNow) || (session.Opened && Busy));
        ears.PresenceAllowed = present;
        if (!present && wasPresent)
        {
            manualListening?.Cancel();
            StopAttention();
            mind.Cancel();
            mouth.Stop();
            GoToSleep();
        }
        wasPresent = present;
        bool available = present && !Busy && !session.Active(DateTime.UtcNow) && !hidden && !Presence.IsFullScreen()
            && companionSettings is not { IsDisposed: false } && !menu.Visible;
        var offer = care.Tick(DateTime.UtcNow, idle, available);
        if (care.Pending == null || !present || hidden || Presence.IsFullScreen()) CloseCarePrompt();
        if (offer == null) return;
        CloseCarePrompt();
        carePrompt = new CarePromptForm(offer, AnswerCare);
        carePrompt.Show();
        if (cfg.Acompanamiento.HablarAvisos && !cfg.Silencio)
        {
            quietNext = true; // una sugerencia espontánea no abre una conversación con sonidos de fondo
            Say(offer.Question);
            expectsReply = false;
        }
    }

    private void CloseCarePrompt()
    {
        var form = carePrompt;
        carePrompt = null;
        form?.Dispose();
    }

    private void AnswerCare(CareChoice choice)
    {
        string? activity = care.Reply(choice, DateTime.UtcNow);
        CloseCarePrompt();
        cfg.Save();
        if (activity != null) { UserActivity(); quietNext = true; Say(activity); expectsReply = false; }
    }

    private bool PersonalRequest(string text)
    {
        if (text is "configura mi perfil" or "mi perfil" or "activa el acompanamiento" or "activa acompanamiento")
        { OpenCompanionSettings(); return true; }
        if (text is "desactiva el acompanamiento" or "desactiva acompanamiento" or "no me recuerdes pausas")
        {
            cfg.Acompanamiento.Habilitado = false; care.Reset(); CloseCarePrompt(); cfg.Save();
            Say("De acuerdo, desactivé el acompañamiento. Puedes volver a activarlo en mi menú."); return true;
        }
        if (text is "hoy no me avises" or "silencia las pausas por hoy")
        {
            cfg.Acompanamiento.SilencioHastaUtc = DateTime.Today.AddDays(1).ToUniversalTime();
            care.Reset(); CloseCarePrompt(); cfg.Save(); Say("De acuerdo, hoy no habrá más propuestas de pausa."); return true;
        }
        if (care.Pending == null) return false;
        CareChoice? choice = text switch
        {
            "si" or "si una pausa" or "hagamos una pausa" => CareChoice.Accept,
            "no" or "ahora no" or "no gracias" => CareChoice.Decline,
            "en 15 minutos" or "posponlo" => CareChoice.Snooze,
            "hoy no" => CareChoice.Today,
            _ => null,
        };
        if (choice == null) return false;
        AnswerCare(choice.Value); return true;
    }

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
}
