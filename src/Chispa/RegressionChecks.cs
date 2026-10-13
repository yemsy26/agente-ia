using System.Text.Json;

namespace Chispa;

internal static class RegressionChecks
{
    private sealed class LateBrain : IBrainProvider
    {
        public string Name => "prueba diferida";
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> Reply(string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolSpec> tools, Func<string, JsonElement, Task<string>> run, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task; // simula un proveedor que ignora la cancelación
            await run("abrir", JsonSerializer.SerializeToElement(new { destino = "navegador" }));
            return "Ya abrí el navegador.";
        }
        public void Dispose() { }
    }

    public static async Task Run(TextWriter output, Action<string, bool, string> check)
    {
        output.WriteLine("REGRESIONES: ejecución, actividad, consentimiento y audio");
        void Check(string title, bool ok) => check(title, ok, "");
        var cfg = new Config { Cerebro = "propio" };
        var body = new Diagnostics.FakeBody(output) { Quiet = true, LaunchOutcome = new(false, "fallo simulado de Windows") };
        var mind = new Mind(cfg, body, new Memory()) { ConfirmTimeout = TimeSpan.FromMilliseconds(25) };
        await mind.Hear("puedes abrir el navegador");
        Check("un fallo al abrir se comunica como fallo", body.LastSaid.Contains("No pude abrir") && body.LastSaid.Contains("fallo simulado"));
        body.LaunchOutcome = new(true, "verificada", true, true);
        await mind.Hear("abre https://example.com/a?x=1&y=2");
        Check("la orden conserva la URL y sus parámetros", body.Did.Contains("abre: https://example.com/a?x=1&y=2"));
        Check("un sí sin herramienta no satisface una petición de abrir", Guard.MissingRequestedAction("puedes abrir el navegador", []));
        Check("abrir exige la herramienta aunque se haya hecho otro gesto", Guard.MissingRequestedAction("abre el navegador", ["cuerpo"]));
        Check("detecta una promesa de apertura sin ejecución", Guard.FakesAction("Claro, te abro el navegador.", []));
        Check("no trata una negación como petición de abrir", !Guard.MissingRequestedAction("no abras el navegador", []));
        int opened = body.Did.Count(d => d.StartsWith("abre:"));
        string mismatch = await ToolBox.Run("abrir", JsonSerializer.SerializeToElement(new { destino = "calculadora" }), mind);
        Check("el modelo no reutiliza permiso para otro destino", mismatch == ToolBox.Denied && body.Did.Count(d => d.StartsWith("abre:")) == opened);
        await mind.Hear("qué hora es");
        string unrelated = await ToolBox.Run("abrir", JsonSerializer.SerializeToElement(new { destino = "navegador" }), mind);
        Check("una orden anterior no autoriza otro turno", unrelated == ToolBox.Denied);
        foreach (string phrase in new[] { "quiero que me abras el navegador", "necesito abrir el navegador", "¿me abres el navegador?", "por favor, abre el navegador", "muéstrame el navegador", "puedes abrirme el navegador por favor" })
        {
            int beforePhrase = body.Did.Count(d => d.StartsWith("abre:"));
            await mind.Hear(phrase);
            Check($"petición natural ejecutada sin esperar al modelo: {phrase}", body.Did.Count(d => d.StartsWith("abre:")) == beforePhrase + 1 && body.LastSaid.Contains("está al frente"));
        }
        Check("preguntar cómo abrir o negar una apertura no ejecuta", new[] { "cómo abrir el navegador", "quiero saber cómo abrir el navegador", "no abras el navegador", "necesito que no abras el navegador" }.All(p => OwnBrain.OpenDestination(p) == null));
        var delayed = new LateBrain();
        var lateBody = new Diagnostics.FakeBody(output) { Quiet = true };
        var late = new Mind(cfg, lateBody, new Memory()) { ProviderOverride = delayed };
        Task previous = late.Hear("necesito que organices una explicación detallada del protocolo xyz");
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await late.Hear("qué hora es");
        delayed.Release.TrySetResult();
        await previous.WaitAsync(TimeSpan.FromSeconds(3));
        Check("un proveedor tardío cancelado no ejecuta herramientas ni habla", !lateBody.Did.Any(d => d.StartsWith("abre:") || d.Contains("Ya abrí")));
        var frozen = new LateBrain();
        var frozenBody = new Diagnostics.FakeBody(output) { Quiet = true };
        var bounded = new Mind(new Config { Cerebro = "propio", LimiteRespuestaSegundos = 5 }, frozenBody, new Memory()) { ProviderOverride = frozen };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Task frozenRequest = bounded.Hear("explica un protocolo inventado que necesita un modelo");
        await frozen.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await frozenRequest.WaitAsync(TimeSpan.FromSeconds(7));
        Check("un proveedor que ignora cancelación no deja pensando para siempre", elapsed.Elapsed.TotalSeconds < 7 && !bounded.Busy && frozenBody.LastSaid.Contains("tardó demasiado"));
        frozen.Release.TrySetResult();
        await Task.Delay(50);
        Check("la consulta vencida no abre nada al llegar tarde", !frozenBody.Did.Any(d => d.StartsWith("abre:")));
        var conversation = new ConversationSession();
        DateTime sessionNow = DateTime.UtcNow;
        conversation.UserSpoke(sessionNow, 45);
        Check("dirigirse al agente abre una conversación sin repetir el nombre", conversation.Active(sessionNow.AddSeconds(40)));
        conversation.UserSpoke(sessionNow.AddSeconds(40), 45);
        Check("seguir hablando renueva la conversación", conversation.Active(sessionNow.AddSeconds(80)));
        conversation.ReplyFinished(sessionNow.AddSeconds(110), 45);
        Check("una respuesta tardía concede tiempo para seguir hablando", conversation.Active(sessionNow.AddSeconds(120)));
        Check("el silencio permite cerrar la sesión", !conversation.Active(sessionNow.AddSeconds(160)));
        conversation.Close();
        Check("eso es todo cierra de inmediato la conversación", !conversation.Opened);
        var lessons = new Lessons();
        lessons.Teach("abre el navegador", "decir", "Voy a abrir el navegador.");
        int before = body.Did.Count;
        await mind.Hear("abre el navegador");
        Check("una lección de texto no puede fingir la apertura solicitada", body.Did.Skip(before).Any(d => d.StartsWith("abre:")) && body.LastSaid.Contains("está al frente"));
        lessons.Forget("abre el navegador");
        lessons.Teach("inicia mi trabajo", "hacer", "abre la calculadora");
        Check("las acciones aprendidas requieren la frase exacta", lessons.Find("inicia mi trabajo")?.Tipo == "hacer" && lessons.Find("no inicia mi trabajo") == null);
        before = body.Did.Count;
        await mind.Hear("abre el navegador", HeardFrom.Background);
        Check("no ejecuta órdenes del sonido de fondo", body.Did.Count == before);

        var prefs = new CompanionPreferences();
        var care = new CareClock(prefs);
        DateTime now = DateTime.UtcNow;
        CareOffer? Advance(int seconds, double idle = 0, bool available = true)
        {
            CareOffer? result = null;
            for (int i = 0; i < seconds; i += 5) { now = now.AddSeconds(5); result = care.Tick(now, idle, available) ?? result; }
            return result;
        }
        Check("el acompañamiento empieza desactivado", Advance(3600) == null && care.ActiveSeconds == 0);
        prefs.Habilitado = true; care.Reset(); care.Tick(now, 0, true);
        Check("no propone antes de 45 minutos activos", Advance(2695) == null);
        Check("propone a los 45 minutos sin iniciar una actividad", Advance(5) != null && care.Pending != null);
        Check("rechazar no inicia instrucciones", care.Reply(CareChoice.Decline, now) == null && care.Pending == null);
        Check("no insiste después de rechazar", Advance(60) == null);
        Advance(2640);
        Check("aceptar devuelve una actividad breve", !string.IsNullOrEmpty(care.Reply(CareChoice.Accept, now)));
        Advance(2700);
        care.Reply(CareChoice.Snooze, now);
        Check("posponer silencia durante 15 minutos", Advance(895) == null);
        Check("posponer vuelve solo después del plazo", Advance(5) != null);
        care.Reply(CareChoice.Today, now);
        Check("hoy no respeta el silencio guardado", Advance(3600) == null && prefs.SilencioHastaUtc > now);
        prefs.SilencioHastaUtc = default; care.Reset(); care.Tick(now, 0, true);
        Check("dejar la PC trabajando no acumula actividad", Advance(3600, 600) == null && care.ActiveSeconds == 0);
        Advance(2700, available: false);
        Check("no interrumpe una tarea ocupada o pantalla completa", care.Pending == null);
        Check("puede ofrecer cuando vuelve a estar disponible", care.Tick(now.AddSeconds(5), 0, true) != null);
        now = now.AddSeconds(5);
        care.Reply(CareChoice.Ignore, now); Advance(2700); care.Reply(CareChoice.Ignore, now);
        Check("dos avisos ignorados detienen las propuestas del día", Advance(5400) == null);
        care.Reset(); care.Tick(now, 0, true); Advance(2700);
        now = now.AddMinutes(1);
        Check("aceptación vencida no empieza una actividad", care.Reply(CareChoice.Accept, now) == null);
        care.Reset(); care.Tick(now, 0, true); Advance(1200); care.Tick(now.AddMinutes(30), 0, true);
        Check("suspender el equipo no suma media hora de trabajo", care.ActiveSeconds <= 1210);
        Check("silencio se descarta antes de cargar Whisper", !Transcriber.HasSignal(new float[16000]));
        var click = new float[16000]; click[100] = .9f;
        Check("un clic aislado no cuenta como voz", !Transcriber.HasSignal(click));
        using (var transcriber = new Transcriber(cfg))
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var signal = Enumerable.Repeat(.02f, Pcm.Rate).ToArray();
            byte[] wav = Pcm.ToWav(VoiceLab.ToPcm16(signal));
            bool stopped = false;
            try { transcriber.Hear(wav, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check("cancelar una transcripción evita cargar el modelo", stopped);
        }
        Check("se filtran subtítulos de ruido", Transcriber.Clean("[Música] gracias por ver") == "");
        Check("las herramientas solo ofrecen el avatar actual", !ToolBox.Specs.Any(t => t.Name is "absorber" or "aspecto") && ToolBox.Specs.Any(t => t.Name == "tamano"));
        Check("perfil dulce por defecto e instrucciones breves", cfg.Perfil.TonoDulce && cfg.Perfil.Instructions.Contains("sin exageraciones"));
        // Los archivos viejos no vuelven a introducir opciones retiradas al guardar la configuración.
        var migrated = JsonSerializer.Deserialize<Config>("{\"version\":4,\"aspecto\":{\"forma\":\"gota\",\"ropa\":\"sombrero\",\"tamano\":1.1}}", Config.Json)!;
        string saved = JsonSerializer.Serialize(migrated, Config.Json);
        Check("la configuración conserva tamaño y descarta campos retirados", migrated.Aspecto.Tamano == 1.1 && !saved.Contains("sombrero") && !saved.Contains("gota"));
    }
}
