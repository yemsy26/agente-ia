using System.Drawing.Imaging;
using System.Diagnostics;
using System.Speech.Recognition;
using System.Text;

namespace Chispa;

/// <summary>Vista y prueba de los controles reales, con datos aislados y audio apagado.</summary>
internal static class InterfaceChecks
{
    public static int Run(TextWriter o, string folder)
    {
        folder = Path.GetFullPath(string.IsNullOrEmpty(folder) ? ".build/interfaz" : folder);
        Directory.CreateDirectory(folder);
        using var incremental = new StreamWriter(Path.Combine(folder, "avance.txt"), append: false, Encoding.UTF8) { AutoFlush = true };
        var report = TextWriter.Synchronized(new TeeWriter(o, incremental));
        var previousProgress = progress;
        var previousFailure = uiFailure;
        progress = report;
        uiFailure = null;
        ThreadExceptionEventHandler threadError = (_, e) =>
        {
            uiFailure = e.Exception;
            report.WriteLine($"{DateTimeOffset.UtcNow:O} EXCEPCIÓN UI: {e.Exception}");
        };
        string? originalData = Environment.GetEnvironmentVariable("CHISPA_DATOS");
        var originalContext = SynchronizationContext.Current;
        bool originalThreadChecks = Control.CheckForIllegalCrossThreadCalls;
        Environment.SetEnvironmentVariable("CHISPA_DATOS", Path.Combine(folder, "datos"));
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += threadError;
            Progress("Inicio de verificación de interfaz");
            Control.CheckForIllegalCrossThreadCalls = true;
            var cfg = new Config { Cerebro = "propio", Silencio = true, EscuchaContinua = false, AnimacionReposo = false, Atajo = "" };
            bool saved = false;
            using (var settings = new CompanionSettingsForm(cfg, () => saved = true))
            {
                settings.Show(); Application.DoEvents();
                Snapshot(settings, Path.Combine(folder, "perfil.png"));
                var controls = settings.Controls[0].Controls;
                ((TextBox)controls.OfType<TextBox>().Single()).Text = "Yemsy";
                controls.OfType<CheckBox>().First(c => c.Text.StartsWith("Activar")).Checked = true;
                controls.OfType<Button>().Single().PerformClick();
            }
            if (!saved || !cfg.Acompanamiento.Habilitado || cfg.Perfil.NombrePreferido != "Yemsy") throw new Exception("Los ajustes no se guardaron.");
            cfg.Acompanamiento.Habilitado = false; // la prueba no habilita las pausas en la aplicación real
            CareChoice? selected = null;
            int answers = 0;
            using (var prompt = new CarePromptForm(new("Llevas un rato trabajando. ¿Te apetece una pausa breve?", "actividad"), choice => { selected = choice; answers++; }))
            {
                prompt.Show(); Application.DoEvents();
                Snapshot(prompt, Path.Combine(folder, "pausa.png"));
                prompt.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().First(b => b.Text == "Ahora no").PerformClick();
                prompt.Close();
            }
            if (selected != CareChoice.Decline || answers != 1) throw new Exception("Rechazar la propuesta no funcionó correctamente.");
            using var app = new PetApp(cfg, false);
            Application.DoEvents();
            var menu = app.Menu;
            string[] labels = menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text ?? "").ToArray();
            if (labels.Any(s => s is "Ropa" or "Color" or "Forma" or "Volver a la normalidad")) throw new Exception("Menú obsoleto.");
            if (!labels.Contains("Perfil y acompañamiento…") || !labels.Contains("Gestos")) throw new Exception("Falta el nuevo menú.");
            menu.Show(new Point(30, 30)); Application.DoEvents();
            Snapshot(menu, Path.Combine(folder, "menu.png"));
            menu.Close();
            try { CommunicationChecks(report, app, cfg); }
            finally { app.Quit(); }
            report.WriteLine("Interfaz correcta: perfil guardado, rechazo único, menú actual y tres vistas PNG.");
            report.WriteLine($"Presencia detectada: {double.IsFinite(Presence.IdleSeconds())}");
            report.WriteLine(Presence.LastStatus);
            return 0;
        }
        catch (Exception ex) { Progress($"Fallo de interfaz: {ex}"); throw; }
        finally
        {
            Progress("Inicio flush final de diagnóstico");
            TraceLog.FlushOnExit();
            Progress("Fin flush final de diagnóstico");
            Application.ThreadException -= threadError;
            Environment.SetEnvironmentVariable("CHISPA_DATOS", originalData);
            SynchronizationContext.SetSynchronizationContext(originalContext);
            Control.CheckForIllegalCrossThreadCalls = originalThreadChecks;
            progress = previousProgress;
            uiFailure = previousFailure;
        }
    }
    private static void Snapshot(Control control, string path)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, ImageFormat.Png);
    }

    // La voz de prueba no abre altavoces, procesos ni modelos. Los eventos entran por Signal,
    // igual que un callback de audio que acaba de llegar desde un hilo de trabajo.
    private sealed class SilentVoice : INaturalVoice
    {
        public event Action? Finished { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public bool Speaking { get; private set; }
        public long LastStartMs => 0;
        public bool Say(string text) { Speaking = true; return true; }
        public void Stop() => Speaking = false;
        public void Warm() { }
        public void Release() => Stop();
    }

    private static void CommunicationChecks(TextWriter output, PetApp app, Config cfg)
    {
        int verified = 0;
        void Check(string title, bool ok)
        {
            if (!ok) throw new InvalidOperationException("Comunicación UI: " + title);
            verified++;
            output.WriteLine("  ok   " + title);
        }
        // Consume el saludo programado mientras la configuración sigue en silencio.
        PumpFor(600);
        app.PresenceTimer.Stop();
        int uiThread = Environment.CurrentManagedThreadId;
        using (var target = new Label())
        using (var isolatedMouth = new Mouth(new Config { Silencio = true }))
        {
            _ = target.Handle;
            int workerThread = 0, deliveredThread = 0;
            bool delivered = false;
            var dispatch = Task.Run(() =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                isolatedMouth.Signal(() =>
                {
                    deliveredThread = Environment.CurrentManagedThreadId;
                    target.Text = "callback en la interfaz";
                    delivered = true;
                });
            });
            PumpUntil(() => dispatch.IsCompleted && delivered, 2000, "callback de voz al hilo UI");
            dispatch.GetAwaiter().GetResult();
            Check("un callback de voz desde worker llega al hilo de la interfaz", workerThread != uiThread && deliveredThread == uiThread && target.Text == "callback en la interfaz");
        }

        var mouth = app.Mouth;
        var ears = app.Ears;
        var pet = app.Pet;
        var bubble = app.Bubble;
        var session = app.Session;
        var voice = new SilentVoice();
        mouth.TestVoice = voice;
        cfg.Silencio = false; // Say usa exclusivamente la voz simulada que se acaba de inyectar.
        Action finish = mouth.FinishedHandlers ?? throw new InvalidOperationException("Nadie escucha el fin de la voz.");
        var hide = bubble.HideTimer;

        app.OnHeard(new Heard(HeardKind.Speech, "qué hora es", Named: true));
        PumpUntil(() => voice.Speaking && pet.Pose.MouthOpen > .1, 1500, "respuesta hablada simulada");
        Check("una petición con nombre abre sesión y pausa el oído durante la voz", session.Active(DateTime.UtcNow) && ears.Paused && bubble.Visible);
        bool staleDelivered = false;
        var oldFinish = Task.Run(() => mouth.Signal(() => { finish(); staleDelivered = true; }));
        PumpUntil(() => oldFinish.IsCompleted && staleDelivered, 1500, "fin de voz anterior en cola");
        oldFinish.GetAwaiter().GetResult();
        PumpFor(100);
        Check("un fin antiguo no oculta la boca, el globo ni reanuda el oído de una voz nueva", voice.Speaking && pet.Pose.MouthOpen > .1 && bubble.Visible && !hide.Enabled && ears.Paused);

        var clockSession = new ConversationSession();
        DateTime simulated = DateTime.UtcNow;
        clockSession.UserSpoke(simulated, 45);
        clockSession.ReplyFinished(simulated.AddSeconds(100), 45);
        Check("una respuesta larga deja un nuevo plazo completo para contestar", clockSession.Active(simulated.AddSeconds(144)) && !clockSession.Active(simulated.AddSeconds(146)));
        output.WriteLine("  nota: la inactividad de Windows no se modifica; esta prueba usa tiempos de sesión sintéticos.");

        // Un motor SIN entrada de audio da acceso al estado de atención de PetApp.
        // Se representa una cancelación en curso para no cargar ni descargar gramáticas;
        // esta prueba es de sesión/UI, las gramáticas se verifican en --oido-dirigido.
        var installed = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault();
        if (installed == null)
        {
            voice.Stop();
            mouth.Signal(finish);
            PumpUntil(() => !ears.Paused, 1500, "fin de voz sin reconocedor");
            Check("terminar la voz devuelve el oído a su estado sin reconocedor", pet.Pose.MouthOpen == 0 && !ears.Paused);
            output.WriteLine("  nota: seguimiento sin nombre no probado: falta un motor Windows que pueda usarse sin audio.");
        }
        else
        {
            var engine = new SpeechRecognitionEngine(installed);
            try
            {
                engine.SetInputToNull();
                var grammar = new Grammar(new GrammarBuilder("prueba") { Culture = installed.Culture });
                engine.LoadGrammar(grammar);
                // Además del motor sin entrada, Sync tiene una puerta cerrada que impide
                // iniciar MicBuffer incluso si SAPI aceptase reconocer sin entrada.
                ears.InstallNullInputEngine(engine, grammar);
            }
            catch { engine.Dispose(); throw; }
            voice.Stop();
            var realFinish = Task.Run(() => mouth.Signal(finish));
            PumpUntil(() => realFinish.IsCompleted && !ears.Paused && ears.Attentive, 2000, "volver a escuchar después de responder");
            realFinish.GetAwaiter().GetResult();
            Check("al terminar la voz conserva conversación y vuelve a atender sin usar micrófono", session.Active(DateTime.UtcNow) && ears.Attentive && ears.Listening && pet.Pose.Listening && pet.Pose.MouthOpen == 0);
            app.OnHeard(new Heard(HeardKind.Speech, "qué fecha es"));
            PumpUntil(() => voice.Speaking && bubble.BubbleText.Contains("Hoy es"), 1500, "seguimiento hablado sin nombre");
            Check("la siguiente frase sin repetir el nombre recibe respuesta", session.Active(DateTime.UtcNow) && bubble.BubbleText.Contains("Hoy es"));
            app.OnHeard(new Heard(HeardKind.Speech, "eso es todo"));
            voice.Stop(); mouth.Signal(finish);
            PumpUntil(() => !ears.Paused && !ears.Attentive, 2000, "fin de conversación explícito");
            Check("eso es todo cierra sesión y escucha en modo a demanda", !session.Opened && !ears.Listening && !pet.Pose.Listening);

            // Enviar texto en modo a demanda no debe encender el micrófono.
            app.OpenChat();
            var chat = app.Chat ?? throw new InvalidOperationException("OpenChat no creó la ventana de texto.");
            var box = chat.Box;
            box.Text = "qué hora es";
            chat.SimulateKeyDown(new KeyEventArgs(Keys.Enter));
            Check("escribir en modo a demanda conserva el micrófono apagado", voice.Speaking && !ears.Listening);
            voice.Stop(); mouth.Signal(finish);
            PumpUntil(() => !ears.Paused, 1500, "respuesta escrita a demanda");

            cfg.EscuchaContinua = true;
            ears.Listening = true; // estado deseado; el motor sigue sin entrada de audio.
            app.OpenChat();
            box.Text = "qué hora es";
            chat.SimulateKeyDown(new KeyEventArgs(Keys.Enter));
            voice.Stop(); mouth.Signal(finish);
            PumpUntil(() => !ears.Paused && ears.Attentive, 2000, "continuidad entre texto y voz");
            Check("escribir con escucha continua mantiene sesión para seguir hablando", session.Active(DateTime.UtcNow) && ears.Attentive && ears.Listening);
            cfg.EscuchaContinua = false;
            app.StopAttention();
            // Ears.Dispose, llamado por Quit, es dueño del motor inyectado y lo libera.
        }
        cfg.Silencio = true;
        voice.Stop();
        output.WriteLine($"Comunicación UI: {verified} comprobaciones; voz simulada, callbacks reales entre hilos, sin audio ni captura.");
    }

    private static TextWriter? progress;
    private static Exception? uiFailure;
    private static void Progress(string stage) => progress?.WriteLine($"{DateTimeOffset.UtcNow:O} {stage}");
    private sealed class TeeWriter(TextWriter buffered, TextWriter incremental) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { buffered.Write(value); incremental.Write(value); }
        public override void Write(string? value) { buffered.Write(value); incremental.Write(value); }
        public override void WriteLine(string? value) { buffered.WriteLine(value); incremental.WriteLine(value); }
    }
    private static void PumpUntil(Func<bool> condition, int limitMs, string reason)
    {
        var clock = Stopwatch.StartNew();
        Progress($"Espera {reason}: inicio (límite {limitMs} ms)");
        while (!condition() && clock.ElapsedMilliseconds < limitMs) { PumpMessages(); Thread.Sleep(5); }
        PumpMessages();
        if (!condition()) throw new TimeoutException("La prueba de interfaz no completó: " + reason);
        Progress($"Espera {reason}: fin ({clock.ElapsedMilliseconds} ms)");
    }
    private static void PumpFor(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        Progress($"Bombeo de mensajes: inicio ({milliseconds} ms)");
        while (clock.ElapsedMilliseconds < milliseconds) { PumpMessages(); Thread.Sleep(5); }
        Progress("Bombeo de mensajes: fin");
    }
    private static void PumpMessages()
    {
        Application.DoEvents();
        if (uiFailure != null) throw new InvalidOperationException("El callback de interfaz falló.", uiFailure);
    }
}
