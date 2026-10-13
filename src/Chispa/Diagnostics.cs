using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;

namespace Chispa;

/// <summary>
/// Modos sin interfaz para comprobar cada pieza:
///   --diagnostico [--sin-voz]   pruebas de reflejos, memoria, herramientas y oído
///   --dibujar carpeta           guarda el avatar actual y sus posturas
///   --preguntar "texto"         pasa una frase por la mente y muestra qué diría y haría
/// Con --salida archivo, el informe también se guarda en ese archivo.
/// </summary>
internal static class Diagnostics
{
    /// <summary>Cuerpo de mentira: apunta lo que la mente le pide en vez de hacerlo.</summary>
    internal sealed class FakeBody(TextWriter o) : IBody
    {
        public readonly List<string> Did = [];
        public readonly List<(TimeSpan Delay, Action Fire)> Timers = [];
        public bool Quiet;   // no escribir cada paso en el informe
        public Action<string>? Spoke;   // a quién avisar de cada cosa que dice (la mente, como hace el cuerpo de verdad)

        /// <summary>Lo último que dijo, o vacío.</summary>
        public string LastSaid => Did.LastOrDefault(d => d.StartsWith("dice: "))?[6..] ?? "";

        public void Say(string text)
        {
            Note($"dice: {text}");
            Spoke?.Invoke(text);
        }
        public string Act(string action)
        {
            Note($"hace: {action}");
            return "Hecho.";
        }
        public string ChangeSize(string tamano)
        {
            Note($"tamaño: {tamano}");
            return "Hecho.";
        }
        public void Thinking(bool on) { }
        public void SetMuted(bool muted) => Note($"silencio: {muted}");
        public void Converse(bool on) => Note($"conversación: {on}");
        public void Later(TimeSpan delay, Action action)
        {
            Timers.Add((delay, action));
            Note($"aviso en {delay.TotalSeconds:0} s");
        }
        public void OpenGuide() => Note("abre la guía");
        public void Rename(string name) => Note($"nombre: {name}");
        public void TuneVoice(int pitchSteps, int speedSteps, bool reset = false) => Note($"voz: tono {pitchSteps} velocidad {speedSteps} normal={reset}");
        public string ChangeVoice(string which)
        {
            Note($"cambia de voz: {which}");
            return which == "hombre" ? "de hombre número 1" : "de mujer número 2";
        }

        // Lo que tocaría el equipo: aquí solo se apunta. Las ventanas y la pantalla son de mentira pero mutables:
        // cerrar una la quita de la lista, como en el equipo de verdad; RechazaCierre simula una que Windows no suelta.
        public Task<LaunchResult> Launch(string command, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Note($"abre: {command}"); return Task.FromResult(LaunchOutcome); }
        public LaunchResult LaunchOutcome = new(true, "verificada", true, true);
        public void PressKey(ushort key, int times = 1) => Note($"pulsa: {KeyName(key)}{(times > 1 ? $" x{times}" : "")}");
        public void TypeText(string text, bool enter) => Note($"teclea: {text}{(enter ? " + Intro" : "")}");
        public readonly List<WindowRef> Windows =
            [new(1, "Informe trimestral - Bloc de notas", "notepad"), new(2, "Recetas de arroz - Google Chrome", "chrome"), new(3, "Descargas", "explorer")];
        public readonly HashSet<IntPtr> RechazaCierre = [];
        public void ResetWindows()
        {
            Windows.Clear();
            Windows.AddRange([new(1, "Informe trimestral - Bloc de notas", "notepad"), new(2, "Recetas de arroz - Google Chrome", "chrome"), new(3, "Descargas", "explorer")]);
            RechazaCierre.Clear();
        }
        public IReadOnlyList<WindowRef> OpenWindows() => Windows;
        public void CloseWindow(WindowRef window)
        {
            Note($"cierra: {window.Title}");
            if (!RechazaCierre.Contains(window.Handle)) Windows.RemoveAll(w => w.Handle == window.Handle);
        }
        public byte[]? Screenshot()
        {
            Note("mira la pantalla");
            return FakeScreen();
        }

        private static string KeyName(ushort key) => key switch
        {
            Native.VK_VOLUME_UP => "subir volumen", Native.VK_VOLUME_DOWN => "bajar volumen", Native.VK_VOLUME_MUTE => "silenciar",
            Native.VK_MEDIA_PLAY_PAUSE => "pausa", Native.VK_MEDIA_NEXT => "siguiente pista", Native.VK_MEDIA_PREV => "pista anterior",
            _ => $"tecla {key}",
        };

        /// <summary>Una ventana dibujada con una factura: lo que «ve» en las pruebas cuando mira la pantalla.</summary>
        public static byte[] FakeScreen()
        {
            using var screen = new Bitmap(1280, 720);
            using (var g = Graphics.FromImage(screen))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.FromArgb(32, 80, 140));
                g.FillRectangle(Brushes.White, 140, 70, 1000, 580);
                using var bar = new SolidBrush(Color.FromArgb(230, 230, 235));
                g.FillRectangle(bar, 140, 70, 1000, 44);
                using var small = new Font("Segoe UI", 13);
                using var normal = new Font("Segoe UI", 20);
                using var big = new Font("Segoe UI", 30, FontStyle.Bold);
                g.DrawString("Factura 2026-104.pdf - Lector de documentos", small, Brushes.Black, 156, 80);
                g.DrawString("Factura número 2026-104", big, Brushes.Black, 190, 150);
                g.DrawString("Cliente: Ana Pérez", normal, Brushes.Black, 190, 240);
                g.DrawString("Concepto: reparación del portátil", normal, Brushes.Black, 190, 300);
                g.DrawString("Fecha de vencimiento: 30 de octubre de 2026", normal, Brushes.Black, 190, 360);
                g.DrawString("Total a pagar: 1.250 pesos", big, Brushes.DarkRed, 190, 450);
            }
            return Device.Jpeg(screen);
        }

        private void Note(string line)
        {
            Did.Add(line);
            if (!Quiet) o.WriteLine($"      [{line}]");
        }
    }

    public static int Run(string[] args)
    {
        string Arg(string flag) => Array.IndexOf(args, flag) is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : "";
        var o = new StringWriter();
        int code;
        try
        {
            code = args[0] switch
            {
                "--diagnostico" => SelfTest(o, voice: !args.Contains("--sin-voz")),
                "--probar-abrir" => TryLaunch(o, Arg("--probar-abrir")),
                "--interfaz" => InterfaceChecks.Run(o, Arg("--interfaz")),
                "--dibujar" => Draw(o, Arg("--dibujar")),
                "--preguntar" => Ask(o, Arg("--preguntar")),
                "--voz" => Speak(o, Arg("--voz")),
                "--oido" => VoiceLab.RunEars(o),
                "--oido-dirigido" => DirectedVoiceChecks.Run(o),
                "--gpu" => GpuDiagnostics.Run(o),
                "--informe" => Report(o),
                "--examen" => Exam.Run(o, Arg("--examen"), Arg("--pensar") switch { "si" or "siempre" => "siempre", "no" or "nunca" => "nunca", _ => "auto" }),
                "--voces" => VoiceLab.Run(o, Arg("--voces") is { Length: > 0 } ids && !ids.StartsWith("--") ? ids : ""),
                _ => Usage(o),
            };
        }
        catch (Exception ex)
        {
            o.WriteLine($"ERROR: {ex}");
            code = 1;
        }
        finally { TraceLog.FlushOnExit(); }
        Console.Out.Write(o.ToString());
        if (Arg("--salida") is { Length: > 0 } file) File.WriteAllText(file, o.ToString());
        return code;
    }

    private static int TryLaunch(TextWriter o, string destination)
    {
        if (ToolBox.ResolveTarget(destination) is not { } target) { o.WriteLine(ToolBox.NotFound); return 1; }
        var result = Device.LaunchAsync(target.Command).GetAwaiter().GetResult();
        o.WriteLine($"Apertura real: {result.Started}. {result.Detail}");
        o.WriteLine($"Ventana visible: {result.WindowVisible}; al frente: {result.Foreground}");
        if (!result.Started || !result.WindowVisible) return 1;
        Thread.Sleep(1800);
        var front = Native.WindowInfo();
        o.WriteLine($"Ventana al frente: {front.Process}");
        bool browser = Device.OpenWindows().Any(w => new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" }.Contains(w.Process.ToLowerInvariant()));
        o.WriteLine($"Hay ventana de navegador reconocida: {browser}");
        return 0;
    }

    private static int Usage(TextWriter o)
    {
        o.WriteLine("Uso: Chispa.exe [--diagnostico [--sin-voz] | --interfaz carpeta | --oido-dirigido | --gpu | --informe | --probar-abrir destino | --dibujar carpeta | --preguntar \"texto\" | --voz \"texto\"] [--salida archivo]");
        return 2;
    }

    private static int Report(TextWriter o)
    {
        string path = SupportDiagnostics.WriteReport(Config.Load()).GetAwaiter().GetResult();
        o.WriteLine(File.ReadAllText(path));
        o.WriteLine($"Informe guardado: {path}");
        return 0;
    }

    /// <summary>Dice unas frases con la voz configurada y mide cuánto tarda en empezar a sonar.</summary>
    private static int Speak(TextWriter o, string text)
    {
        using var mouth = new Mouth(Config.Load());
        o.WriteLine($"Voz: {mouth.VoiceName}");
        string[] phrases = text.Length > 0 && !text.StartsWith("--")
            ? [text]
            : ["¿Sí?", "¡Hola! Soy Chispa. ¿En qué te ayudo hoy?", "Juan Pablo Duarte fue uno de los padres de la patria de la República Dominicana. Nació en Santo Domingo en 1813."];
        int failed = 0;
        foreach (string phrase in phrases)
        {
            using var done = new ManualResetEventSlim();
            void OnFinished() => done.Set();
            mouth.Finished += OnFinished;
            var clock = Stopwatch.StartNew();
            bool finished = mouth.Say(phrase) && done.Wait(TimeSpan.FromSeconds(45));
            mouth.Finished -= OnFinished;
            if (!finished) failed++;
            o.WriteLine($"{(finished ? "  ok   " : "  FALLA")} empezó a sonar a los {mouth.LastStartMs} ms y terminó a los {clock.ElapsedMilliseconds} ms   «{phrase}»");
        }
        o.WriteLine($"Voz usada al final: {mouth.VoiceName}");
        return failed == 0 ? 0 : 1;
    }

    private static int Ask(TextWriter o, string question)
    {
        var body = new FakeBody(o);
        var mind = new Mind(Config.Load(), body, new Memory()) { ConfirmTimeout = TimeSpan.FromSeconds(1) };
        var clock = Stopwatch.StartNew();
        o.WriteLine($"> {question}");
        mind.Hear(question).GetAwaiter().GetResult();
        o.WriteLine($"cerebro: {mind.ProviderName}   tiempo: {clock.Elapsed.TotalSeconds:F1} s");
        return 0;
    }

    private static int Draw(TextWriter o, string dir)
    {
        if (dir.Length == 0) dir = ".";
        Directory.CreateDirectory(dir);
        DrawCharacter(o, dir);
        return 0;
    }

    /// <summary>El personaje ilustrado en cada gesto, al tamaño al que se ve en pantalla, y cuánto cuesta pintarlo.</summary>
    private static void DrawCharacter(TextWriter o, string dir)
    {
        var look = new Look();
        double scale = look.Tamano * PetRenderer.Zoom;
        string previewPath = Path.Combine(dir, "avatar.png");
        using (var preview = PetRenderer.Render(new Pose(), scale))
            preview.Save(previewPath, ImageFormat.Png);
        o.WriteLine($"Avatar transparente guardado en {Path.GetFullPath(previewPath)}");
        Pose[] poses =
        [
            new() { Breath = 0.2 }, new() { Breath = 1, EyeOpen = 0.6 }, new() { EyeOpen = 0.3 }, new() { EyeOpen = 0 },
            new() { MouthOpen = 0.3 }, new() { MouthOpen = 0.6, Nod = 0.15 }, new() { MouthOpen = 1 },
            new() { Listening = true, Head = 4.5, Nod = -0.1 }, new() { Thinking = true, ThinkPhase = 0.3, Head = -3.5, Nod = -0.7 },
            new() { Asleep = true, EyeOpen = 0 }, new() { Wave = 0.5 }, new() { Wave = 1 }, new() { Wave = 0.8, MouthOpen = 0.6, Head = 3 },
            new() { LegPhase = 1.2, Gaze = 1, Tilt = 4, Sway = -0.9 }, new() { LegPhase = 4.4, Gaze = -1, Tilt = -4, Sway = 0.9 },
            new() { Squash = -0.13, Airborne = true, Sway = 0.4 }, new() { Squash = 0.16 }, new() { Tilt = 9, Wave = 0.8, LegPhase = 1.2, Sway = -1 },
            new() { Gaze = -1 }, new() { Gaze = 1 }, new() { Head = 6 }, new() { Nod = 1 },
        ];
        int cw = (int)Math.Ceiling(PetRenderer.W * scale), ch = (int)Math.Ceiling(PetRenderer.H * scale), cols = 8, rows = (poses.Length + cols - 1) / cols;
        using var sheet = new Bitmap(cols * cw, rows * ch, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(228, 232, 238));
            using var night = new SolidBrush(Color.FromArgb(32, 34, 44));
            g.FillRectangle(night, 0, ch, sheet.Width, ch);   // la fila de en medio, sobre fondo oscuro
            for (int i = 0; i < poses.Length; i++)
            {
                using var bmp = PetRenderer.Render(poses[i], scale);
                g.DrawImageUnscaled(bmp, i % cols * cw, i / cols * ch);
            }
        }
        string path = Path.Combine(dir, "personaje.png");
        sheet.Save(path, ImageFormat.Png);
        o.WriteLine($"Personaje guardado en {Path.GetFullPath(path)}");

        // 300 fotogramas (diez segundos de animación), sin pausas: separar tiempo de reloj de CPU real del proceso.
        using var process = Process.GetCurrentProcess();
        foreach (double dpi in new[] { 1.0, 1.5 })
        {
            var pose = new Pose();
            PetRenderer.Render(pose, scale * dpi).Dispose();   // la primera vez prepara las piezas a ese tamaño
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            const int frames = 300;
            for (int f = 0; f < frames; f++)
            {
                double t = f / 30.0;
                pose.Clock = t;
                pose.Breath = 0.5 - 0.5 * Math.Cos(t * 1.65);
                pose.Sway = 0.6 * Math.Sin(t * 2.1);
                pose.Head = 3 * Math.Sin(t * 0.9);
                pose.MouthOpen = 0.2 + 0.8 * Math.Abs(Math.Sin(t * 9.5));
                pose.EyeOpen = f % 90 < 5 ? Math.Abs(1 - f % 90 / 2.5) : 1;
                pose.Wave = f > 200 ? 0.9 : 0;
                PetRenderer.Render(pose, scale * dpi).Dispose();
            }
            clock.Stop();
            process.Refresh();
            double ms = clock.Elapsed.TotalMilliseconds / frames;
            double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / frames;
            o.WriteLine($"Pintar un fotograma de {(int)Math.Ceiling(PetRenderer.W * scale * dpi)} × {(int)Math.Ceiling(PetRenderer.H * scale * dpi)} px (pantalla al {dpi * 100:F0} %): {ms:F2} ms de reloj, {cpuMs:F2} ms de CPU   →   estimación con este coste: reposo a 12/s {cpuMs * 12 / 10:F1} %, gestos a 30/s {cpuMs * 30 / 10:F1} % de un núcleo");
        }
    }

    private static int SelfTest(TextWriter o, bool voice)
    {
        // Datos en una carpeta temporal: el diagnóstico nunca toca la memoria real del usuario.
        string temp = Path.Combine(Path.GetTempPath(), "chispa-diagnostico-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("CHISPA_DATOS", temp);
        int failed = 0;
        void Check(string what, bool ok, string detail = "")
        {
            o.WriteLine($"{(ok ? "  ok   " : "  FALLA")} {what}{(detail.Length > 0 ? "   →   " + detail : "")}");
            if (!ok) failed++;
        }

        var cfg = new Config { Cerebro = "propio" };   // estas pruebas son de su cerebro propio: nada de llamar al modelo
        try
        {
            AvatarTest(o, temp, Check);
            o.WriteLine("REFLEJOS (órdenes sin IA)");
            (string Text, string? Expected)[] phrases =
            [
                ("Chispa, ¿qué hora es?", "hora"), ("oye chispa salta", "saltar"), ("Escóndete, por favor", "esconder"),
                ("más pequeña", "pequena"), ("más grande", "grande"), ("¿Qué sabes de mí?", "memoria"), ("ven aquí", "venir"),
                ("cuéntame un chiste", "chiste"), ("hablemos", "conversar"), ("eso es todo", "fin"), ("ayuda", "guia"),
                ("tamaño normal", "normal"), ("no saltes encima de la mesa", null), ("¿qué hora es en Tokio?", null),
            ];
            foreach (var (text, expected) in phrases)
            {
                string? got = Reflexes.Match(Reflexes.Normalize(text, cfg.Nombre));
                Check($"«{text}» → {expected ?? "(pensar)"}", got == expected, got == expected ? "" : $"salió {got ?? "(pensar)"}");
            }
            Check("entiende variantes parecidas («salta ya», «mas grande»)", Reflexes.Near("salta ya") == "saltar" && Reflexes.Near("mas grande") == "grande");
            Check("una negación o una frase más larga no cuentan como orden",
                Reflexes.Near("no saltes") == null && Reflexes.Near("que hora es en tokio") == null && Reflexes.Near("saltamontes verdes del campo") == null);
            Check("«hola Chispa» se queda en saludo", Reflexes.Normalize("¡Hola, Chispa!", cfg.Nombre).Length == 0);
            Check("«recuerda que…» extrae el dato",
                Reflexes.RememberRequest("Chispa, recuerda que mi color favorito es el azul.", cfg.Nombre) == "mi color favorito es el azul");

            o.WriteLine("MEMORIA");
            var memory = new Memory();
            Check("guarda un dato nuevo", memory.Add("Se llama Yemsy"));
            Check("no duplica", !memory.Add("se llama yemsy"));
            memory.Add("Su color favorito es el azul");
            memory.Add("Trabaja programando por las noches");
            for (int i = 0; i < 12; i++) memory.Add($"Dato de relleno número {i} sobre plantas");
            var relevant = memory.Relevant("¿cuál era mi color favorito?", 5);
            Check("trae lo que viene al caso", relevant.Count == 5 && relevant[0].Contains("azul"), string.Join(" | ", relevant.Take(2)));
            Check("persiste en disco", new Memory().All.Count == 15);
            Check("olvida por tema", memory.Forget("color favorito") == 1 && new Memory().All.Count == 14);

            o.WriteLine("MENTE Y HERRAMIENTAS");
            var body = new FakeBody(o);
            var mind = new Mind(cfg, body, memory) { ConfirmTimeout = TimeSpan.FromMilliseconds(300) };
            JsonElement Args(object a) => JsonSerializer.SerializeToElement(a);
            string Tool(string name, object a) => ToolBox.Run(name, Args(a), mind).GetAwaiter().GetResult();

            mind.Hear("Chispa, salta").GetAwaiter().GetResult();
            Check("una orden hablada mueve el cuerpo sin modelo grande", body.Did.Contains("hace: saltar") && mind.ProviderName == "propio");
            mind.Hear("recuerda que tengo un perro llamado Bruno").GetAwaiter().GetResult();
            Check("«recuerda que…» va a la memoria", memory.All.Any(f => f.Texto.Contains("Bruno")));
            Check("herramienta cuerpo", Tool("cuerpo", new { accion = "bailar" }) == "Hecho." && body.Did.Contains("hace: bailar"));
            Check("herramienta tamaño", Tool("tamano", new { tamano = "normal" }) == "Hecho." && body.Did.Any(d => d.Contains("tamaño: normal")));
            Check("herramienta recordar", Tool("recordar", new { dato = "Le gusta el café sin azúcar" }).StartsWith("Guardado"));
            string context = Tool("contexto_equipo", new { });
            Check("herramienta contexto_equipo", context.Contains("Fecha y hora") && context.Contains("Ventana activa"), context.Split('\n')[0].Trim());

            string denied = Tool("abrir", new { destino = "calculadora" });
            Check("si no lo pidió el usuario, sin confirmación hablada NO abre nada",
                denied.Contains("no lo confirmó") && body.Did.Any(d => d.Contains("Chispa, confirmo")) && !body.Did.Any(d => d.StartsWith("abre:")), denied);
            Check("no abre destinos desconocidos ni ejecuta comandos", Tool("abrir", new { destino = "cmd /c del archivo" }) == ToolBox.NotFound && !body.Did.Any(d => d.StartsWith("abre:")));
            var pending = mind.Confirm("hacer una prueba");
            mind.Reflex("confirmo");
            Check("«Chispa, confirmo» autoriza", pending.GetAwaiter().GetResult());
            pending = mind.Confirm("hacer otra prueba");
            mind.Reflex("cancela");
            Check("«Chispa, cancela» lo impide", !pending.GetAwaiter().GetResult());

            OwnBrainTest(o, cfg, Check);
            ReasonTest(o, cfg, Check);
            LongMemoryTest(o, cfg, Check);
            RegressionChecks.Run(o, Check).GetAwaiter().GetResult();
            SupportDiagnostics.RunChecks(Check).GetAwaiter().GetResult();
            StreamingChecks.Run(Check).GetAwaiter().GetResult();
            LaunchChecks.Run(Check).GetAwaiter().GetResult();
            if (voice)
            {
                DirectedVoiceChecks.RunCancellationChecks(Check).GetAwaiter().GetResult();
                VoiceTest(o, cfg, Check);
                VadTest(o, cfg, Check);
                WakeTest(o, cfg, Check);
                DevicesTest(o);
            }

            using var me = Process.GetCurrentProcess();
            o.WriteLine($"Memoria privada del proceso al terminar las pruebas: {me.PrivateMemorySize64 / 1048576.0:F0} MB");
        }
        finally
        {
            TraceLog.FlushOnExit();
            try { Directory.Delete(temp, recursive: true); } catch (Exception) { /* carpeta temporal en uso */ }
        }
        o.WriteLine(failed == 0 ? "TODO BIEN" : $"{failed} PRUEBAS FALLARON");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>El avatar real, sus gestos y la recuperación ante piezas externas rotas; sin ventanas ni modelos.</summary>
    private static void AvatarTest(TextWriter o, string temp, Action<string, bool, string> check)
    {
        o.WriteLine("AVATAR (recursos, gestos, DPI y recuperación)");
        var assembly = typeof(Diagnostics).Assembly;
        string[] pieces = ["figura.json", "cuerpo.png", "sin_brazo.png", "brazo.png", "pelo.png",
            "ojos_1.png", "ojos_2.png", "ojos_3.png", "boca_1.png", "boca_2.png", "boca_3.png"];
        var resources = assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        bool embedded = pieces.All(name => resources.Contains("Avatar/" + name));
        check("todas las piezas del avatar viajan dentro del programa", embedded,
            embedded ? $"{pieces.Length} recursos" : string.Join(", ", pieces.Where(name => !resources.Contains("Avatar/" + name))));

        void TryCheck(string title, Action run)
        {
            try { run(); }
            catch (Exception ex) { check(title, false, $"{ex.GetType().Name}: {ex.Message}"); }
        }
        (int Visible, int Opaque, int Clear, ulong Hash) Pixels(Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int visible = 0, opaque = 0, clear = 0;
                ulong hash = 14695981039346656037UL;
                int[] row = new int[bitmap.Width];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    foreach (int pixel in row)
                    {
                        uint color = unchecked((uint)pixel);
                        uint alpha = color >> 24;
                        if (alpha > 0) visible++; else clear++;
                        if (alpha >= 240) opaque++;
                        hash = unchecked((hash ^ color) * 1099511628211UL);
                    }
                }
                return (visible, opaque, clear, hash);
            }
            finally { bitmap.UnlockBits(data); }
        }
        void CheckPortrait(Character avatar, string title)
        {
            using var icon = avatar.Portrait();
            using var bitmap = icon.ToBitmap();
            var pixels = Pixels(bitmap);
            check(title, icon.Width == 64 && icon.Height == 64 && pixels.Opaque > bitmap.Width * bitmap.Height / 100,
                $"{icon.Width} × {icon.Height} px; {pixels.Visible} píxeles visibles");
        }
        double scale = new Look().Tamano * PetRenderer.Zoom;
        (string Name, Pose Pose)[] states =
        [
            ("dormir", new() { Asleep = true, EyeOpen = 0 }),
            ("escuchar", new() { Listening = true }),
            ("hablar", new() { MouthOpen = 0.9 }),
            ("saludar", new() { Wave = 1 }),
        ];
        TryCheck("el avatar embebido se carga y dibuja", () =>
        {
            var avatar = Character.Load(Path.Combine(temp, "avatar-ausente"));
            check("carga el avatar embebido sin carpeta externa", avatar != null, "");
            if (avatar == null) return;
            CheckPortrait(avatar, "el retrato embebido se convierte en icono");
            foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
            {
                double actualScale = scale * dpi;
                using var idle = avatar.Render(new Pose(), actualScale);
                ulong idleHash = Pixels(idle).Hash;
                foreach (var (name, pose) in states)
                {
                    string title = $"{name} al {dpi * 100:F0} % conserva el gesto y el fondo transparente";
                    TryCheck(title, () =>
                    {
                        using var bitmap = avatar.Render(pose, actualScale);
                        var pixels = Pixels(bitmap);
                        int area = bitmap.Width * bitmap.Height;
                        bool valid = bitmap.Width == (int)Math.Ceiling(PetRenderer.W * actualScale)
                            && bitmap.Height == (int)Math.Ceiling(PetRenderer.H * actualScale)
                            && pixels.Opaque > area / 100 && pixels.Clear > area / 100 && pixels.Hash != idleHash;
                        check(title, valid, $"{bitmap.Width} × {bitmap.Height} px; {pixels.Visible} visibles; {pixels.Clear} transparentes");
                    });
                }
            }
        });

        if (!embedded) return;
        void CopyPiece(string folder, string name)
        {
            using var input = assembly.GetManifestResourceStream("Avatar/" + name)!;
            using var output = File.Create(Path.Combine(folder, name));
            input.CopyTo(output);
        }
        void CheckFallback(string folder, string title)
        {
            TryCheck(title, () =>
            {
                var avatar = Character.Load(folder);
                check(title, avatar != null, "");
                if (avatar == null) return;
                foreach (var (name, pose) in states)
                {
                    using var bitmap = avatar.Render(pose, scale);
                    var pixels = Pixels(bitmap);
                    check($"tras recuperar: {name}", pixels.Opaque > bitmap.Width * bitmap.Height / 100 && pixels.Clear > 0, "");
                }
                CheckPortrait(avatar, "tras recuperar: icono de la bandeja");
            });
        }
        string incomplete = Path.Combine(temp, "avatar-incompleto");
        Directory.CreateDirectory(incomplete);
        CopyPiece(incomplete, "figura.json");
        CheckFallback(incomplete, "una carpeta externa incompleta recupera el avatar embebido");

        string corrupt = Path.Combine(temp, "avatar-corrupto");
        Directory.CreateDirectory(corrupt);
        foreach (string name in pieces) CopyPiece(corrupt, name);
        File.WriteAllText(Path.Combine(corrupt, "cuerpo.png"), "esta pieza no es una imagen PNG");
        CheckFallback(corrupt, "un cuerpo externo corrupto recupera el avatar antes de dibujar o crear el icono");

        string invalidRig = Path.Combine(temp, "avatar-medidas-invalidas");
        Directory.CreateDirectory(invalidRig);
        foreach (string name in pieces) CopyPiece(invalidRig, name);
        string rigPath = Path.Combine(invalidRig, "figura.json");
        using (var rig = JsonDocument.Parse(File.ReadAllText(rigPath)))
        {
            var properties = rig.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            properties["suelo"] = properties["coronilla"];
            File.WriteAllText(rigPath, JsonSerializer.Serialize(properties));
        }
        CheckFallback(invalidRig, "unas medidas externas inválidas recuperan el avatar embebido");

        using (var rig = JsonDocument.Parse(assembly.GetManifestResourceStream("Avatar/figura.json")!))
        {
            var properties = rig.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            properties["coronilla"] = JsonSerializer.SerializeToElement(properties["suelo"].GetDouble() - 1);
            File.WriteAllText(rigPath, JsonSerializer.Serialize(properties));
        }
        CheckFallback(invalidRig, "un rango de altura externo diminuto recupera el avatar sin crear imágenes gigantes");

        TryCheck("hablar o pensar estando oculta no activa el reloj del avatar", () =>
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                using var hiddenPet = new PetForm(new Look(), "", null);
                var timer = hiddenPet.Clock;
                hiddenPet.SetTalking(true);
                hiddenPet.SetThinking(true);
                check("hablar o pensar estando oculta no activa el reloj del avatar", !timer.Enabled, "");
                hiddenPet.SetAsleep(true);
                check("dormir detiene el reloj y cancela el habla y pensamiento del avatar",
                    !timer.Enabled && hiddenPet.Pose.Asleep && !hiddenPet.Pose.Thinking && hiddenPet.Pose.MouthOpen == 0, "");
            }
            finally
            {
                // El diagnóstico no tiene bucle de mensajes; las pruebas async posteriores no deben capturar
                // el contexto de WinForms que se instala al construir una ventana.
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    /// <summary>Un modelo de lenguaje de mentira: contesta lo que se le diga, tras pedir (o no) una herramienta.</summary>
    private sealed class ScriptedBrain : IBrainProvider
    {
        public string Says = "";
        public string? SaysIfAskedAgain;   // lo que contesta si le piden la respuesta por segunda vez
        public (string Tool, object Args)? Asks;
        public (string Tool, object Args)? AsksCorrection;   // lo que pide solo en la ronda de corrección
        public string? SaysOnCorrection;   // lo que contesta en la ronda de corrección
        public int Replies;                // cuántas veces se le pidió una respuesta
        public string Name => "de prueba";
        public async Task<string> Reply(string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolSpec> tools,
                                        Func<string, JsonElement, Task<string>> runTool, CancellationToken ct)
        {
            Replies++;
            if (system.Contains(Guard.CorrectionMark) && SaysOnCorrection != null)
            {
                if (AsksCorrection is { } corrected) await runTool(corrected.Tool, JsonSerializer.SerializeToElement(corrected.Args));
                return SaysOnCorrection;
            }
            if (Asks is { } asks) await runTool(asks.Tool, JsonSerializer.SerializeToElement(asks.Args));
            return system.EndsWith(Guard.DoItForReal) && SaysIfAskedAgain != null ? SaysIfAskedAgain : Says;
        }
        public void Dispose() { }
    }

    /// <summary>La memoria larga: el diario, las fechas dichas de palabra y los datos que se corrigen sin borrarse.</summary>
    private static void LongMemoryTest(TextWriter o, Config cfg, Action<string, bool, string> check)
    {
        o.WriteLine("MEMORIA LARGA (diario)");
        var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        var journal = new Journal();
        journal.Clear();
        DateTime today = DateTime.Today;
        journal.Add("necesito enviar el informe de ventas a Laura el viernes", "Vale, lo tengo en cuenta.", today.AddDays(-7).AddHours(10));
        journal.Add("tengo que cambiar las ruedas del coche antes del viaje", "Te lo recuerdo cuando quieras.", today.AddDays(-60).AddHours(18));
        journal.Add("estoy leyendo Cien años de soledad", "Buena elección.", today.AddDays(-95).AddHours(21));
        journal.Add("qué hora es", "Son las tres.", today.AddDays(-1).AddHours(15));

        check("guarda entradas de meses distintos y las relee en orden", journal.Read() is { Count: 4 } all && all[0].Dijo.Contains("Cien años") && all[3].Dijo == "qué hora es", $"{journal.Read().Count} entradas");
        var hit = journal.Search("qué te dije del informe de ventas");
        check("encuentra por tema lo dicho hace una semana", hit.Count > 0 && hit[^1].Dijo.Contains("Laura"), string.Join(" | ", hit.Select(e => e.Dijo)));
        check("entiende «ayer»", Journal.When("qué hablamos ayer") is { } y && y.From == today.AddDays(-1) && y.To == today, "");
        string month = today.AddDays(-95).ToString("MMMM", es);
        check("entiende «la semana pasada», «hace dos meses» y un mes por su nombre",
            Journal.When("la semana pasada") is { } week && week.From <= today.AddDays(-7) && today.AddDays(-7) < week.To.AddDays(7)
            && Journal.When("hace dos meses") is { } two && two.From <= today.AddDays(-60) && today.AddDays(-60) < two.To
            && Journal.When($"en {month}") is { } named && named.From <= today.AddDays(-95) && today.AddDays(-95) < named.To, month);
        check("busca dentro de una fecha", journal.Search("", today.AddDays(-1), today) is { Count: 1 } one && one[0].Dijo == "qué hora es", "");
        check("lo que no se habló no aparece", journal.Search("vacaciones en la playa de Cancún", minMatches: 2).Count == 0, "");
        journal.SaveSummary(today.AddDays(-1), "Preguntó la hora.");
        check("guarda el resumen de un día", journal.RecentSummaries() is { Count: 1 } days && days[0].Text == "Preguntó la hora." && !journal.DaysToSummarize().Contains(today.AddDays(-1)), "");

        var memory = new Memory();
        memory.Clear();
        var body = new FakeBody(o) { Quiet = true };
        var mind = new Mind(cfg, body, memory);
        body.Spoke = mind.Said;
        mind.Hear("mi color favorito es el verde").GetAwaiter().GetResult();
        mind.Hear("mi color favorito es el azul").GetAwaiter().GetResult();
        mind.Flush();
        var written = mind.Journal.Read(today);
        check("cada intercambio queda en el diario con lo que contestó", written.Count == 2 && written[1].Respondio.Contains("Actualizado"), string.Join(" | ", written.Select(e => $"{e.Dijo} → {e.Respondio}")));
        check("un dato corregido no se borra: queda cerrado con su fecha",
            memory.All.Any(f => f.Texto.Contains("azul")) && !memory.All.Any(f => f.Texto.Contains("verde")) && memory.Past.Any(f => f.Texto.Contains("verde") && f.Hasta != null), "");
        int before = body.Did.Count;
        mind.Hear("¿qué te dije la semana pasada del informe?").GetAwaiter().GetResult();
        string told = string.Join(" | ", body.Did.Skip(before));
        check("«¿qué te dije la semana pasada del informe?» lo saca del diario", told.Contains("Laura"), told);
        before = body.Did.Count;
        mind.Hear("¿qué hablamos anteayer?").GetAwaiter().GetResult();
        told = string.Join(" | ", body.Did.Skip(before));
        check("de un día sin conversación dice que no tiene nada", told.Contains("No tengo nada apuntado de anteayer"), told);

        // El índice incremental: una instancia nueva (como tras reiniciar) sigue buscando igual, y se rehace si se estropea.
        var fresh = new Journal();
        var again = fresh.Search("qué te dije del informe de ventas");
        check("una instancia nueva del diario sigue encontrando lo mismo (índice en disco)",
            again.Any(e => $"{e.Dijo} {e.Respondio}".Contains("Laura")), string.Join(" | ", again.Select(e => e.Dijo)));
        check("saca los temas de un tramo de meses", fresh.Topics(today.AddDays(-100), today.AddDays(-40), max: 20).Any(t => t.Tema.Contains("ruedas")),
            string.Join(", ", fresh.Topics(today.AddDays(-100), today.AddDays(-40)).Select(t => t.Tema)));
        check("cuenta cuántas entradas tocan un tema", fresh.Count("ruedas") == 1, $"{fresh.Count("ruedas")}");
        before = body.Did.Count;
        mind.Hear("¿cuántas veces te he hablado de las ruedas del coche?").GetAwaiter().GetResult();
        told = string.Join(" | ", body.Did.Skip(before));
        check("«¿cuántas veces te he hablado de las ruedas?» cuenta las entradas", told.Contains("1 entrada") && told.Contains("ruedas"), told);
        File.WriteAllText(Path.Combine(Config.DataDir, "diario", "indice.json"), "esto no es json");
        check("con el índice estropeado la búsqueda sigue funcionando", new Journal().Search("ruedas del coche").Any(e => e.Dijo.Contains("ruedas")), "");

        mind.Flush();
        journal.Clear();
    }

    /// <summary>El motor lógico, los repartos con condiciones y el guardián que revisa lo que diría un modelo.</summary>
    private static void ReasonTest(TextWriter o, Config cfg, Action<string, bool, string> check)
    {
        o.WriteLine("RAZÓN (motor lógico y guardián)");
        check("el motor lógico está junto al programa", Reason.Available, Reason.EnginePath);
        if (!Reason.Available) return;

        int[][] facts = [[1], [-1, -2]];   // 1 es cierto; 1 y 2 no caben a la vez
        check("decide si algo se deduce, se contradice o no se sabe",
            Reason.Judge(facts, 1) == Verdict.Deduced && Reason.Judge(facts, 2) == Verdict.Contradicted && Reason.Judge(facts, 3) == Verdict.Unknown, "");

        string[] things = ["dentista", "banco", "taller"], days = ["lunes", "martes", "miércoles"];
        string plan = Allocation.Answer(things, days, ["el dentista no puede el lunes", "el banco no puede el miércoles", "el taller solo el miércoles"]);
        check("reparte tres citas con condiciones y sabe que es la única forma", plan.Contains("lunes, banco; martes, dentista; miércoles, taller") && plan.Contains("única"), plan);
        string order = Allocation.Answer(things, days, ["taller antes del banco", "dentista justo después del banco"]);
        check("entiende «antes» y «justo después»", order.Contains("lunes, taller; martes, banco; miércoles, dentista"), order);
        string open = Allocation.Answer(things, days, ["el dentista no puede el lunes"]);
        check("si hay varias formas, lo dice", open.StartsWith("Una forma") && open.Contains("Hay otras"), open);
        string together = Allocation.Answer(things, days, ["banco y taller el mismo día", "dentista solo martes", "banco no puede el lunes"]);
        check("«el mismo día» junta dos cosas", together.Contains("miércoles, banco y taller"), together);
        string clash = Allocation.Answer(["dentista", "banco"], ["lunes", "martes"], ["dentista solo lunes", "banco solo lunes"]);
        check("si es imposible lo dice, y señala qué condición sobra", clash.Contains("imposible") && clash.Contains("«dentista solo lunes» o «banco solo lunes»"), clash);
        string crowded = Allocation.Answer(things, ["lunes", "martes"], ["máximo uno por día"]);
        check("si no caben, lo dice", crowded.Contains("no caben 3 cosas en 2 huecos"), crowded);
        string onePer = Allocation.Answer(things, ["lunes", "martes"], ["una cosa por hueco"]);
        string noTwo = Allocation.Answer(things, ["lunes", "martes"], ["no quiero dos el mismo día"]);
        check("entiende «una por hueco» y «no quiero dos el mismo día»", onePer.Contains("no caben") && noTwo.Contains("no caben"), $"{onePer} · {noTwo}");
        string unclear = Allocation.Answer(things, days, ["el dentista ya veremos"]);
        check("una condición que no entiende detiene el problema", unclear.StartsWith("No entendí la condición"), unclear);
        string vague = Allocation.Answer(things, days, ["el dentista quizá el lunes"]);
        check("tampoco adivina si una palabra rara obliga o prohíbe", vague.StartsWith("No entendí la condición"), vague);
        string closed = Allocation.Answer(["gimnasio", "farmacia", "mercado"], ["jueves", "viernes", "sábado"], ["el gimnasio cierra el jueves", "la farmacia solo abre el sábado"]);
        check("«cierra» prohíbe y «solo abre» obliga", closed.Contains("jueves, mercado; viernes, gimnasio; sábado, farmacia"), closed);

        // Llamadas a herramientas que el modelo dejó escritas en el texto, en las tres formas que se le han visto.
        string Rescued(string written, out string left)
        {
            var calls = new List<(string Name, JsonElement Args)>();
            left = OllamaBrain.Salvage(written, ToolBox.Specs, calls);
            return string.Join(" + ", calls.Select(c => $"{c.Name} {c.Args.GetRawText()}"));
        }
        string gemma = Rescued("investigar{consulta:<|\"|>capital de Francia<|\"|>}", out string restGemma);
        check("rescata una llamada escrita al estilo de Gemma", gemma == "investigar {\"consulta\":\"capital de Francia\"}" && restGemma.Length == 0, $"{gemma} · «{restGemma}»");
        string xml = Rescued("¡Claro! <cuerpo accion=\"bailar\"/>", out string restXml);
        check("rescata una llamada escrita como etiqueta y deja el resto", xml == "cuerpo {\"accion\":\"bailar\"}" && restXml == "¡Claro!", $"{xml} · «{restXml}»");
        string json = Rescued("<tool_call>{\"name\": \"razonar\", \"arguments\": {\"cosas\": [\"a\", \"b\"], \"huecos\": [\"x\", \"y\"]}}</tool_call>", out string restJson);
        check("rescata una llamada escrita en JSON, con listas", json.StartsWith("razonar {\"cosas\": [\"a\", \"b\"]") && restJson.Length == 0, $"{json} · «{restJson}»");
        string none = Rescued("Puedo investigar eso si quieres.", out string restNone);
        check("no confunde una frase normal con una llamada", none.Length == 0 && restNone == "Puedo investigar eso si quieres.", $"{none} · «{restNone}»");

        var memory = new Memory();
        memory.Clear();
        var body = new FakeBody(o) { Quiet = true };
        var model = new ScriptedBrain();
        var mind = new Mind(cfg, body, memory) { ConfirmTimeout = TimeSpan.FromMilliseconds(200), ProviderOverride = model };
        string Tell(string phrase)
        {
            int before = body.Did.Count;
            mind.Hear(phrase).GetAwaiter().GetResult();
            return string.Join(" | ", body.Did.Skip(before));
        }

        string spoken = Tell("organiza dentista, banco y taller en lunes, martes y miércoles; el dentista no puede el lunes; el banco no puede el miércoles; el taller solo el miércoles");
        check("«organiza … en …; condiciones» lo resuelve sin modelo", spoken.Contains("martes, dentista") && mind.ProviderName == "propio", spoken);
        string tool = ToolBox.Run("razonar", JsonSerializer.SerializeToElement(new { cosas = things, huecos = days, reglas = new[] { "taller solo lunes", "banco antes de dentista" } }), mind).GetAwaiter().GetResult();
        check("herramienta razonar", tool.Contains("lunes, taller; martes, banco; miércoles, dentista"), tool);

        Tell("mi hermana se llama Ana y vive en Santiago");
        Tell("mi color favorito es el azul");
        void Guarded(string title, string says, string expected, (string, object)? asks = null, string? again = null)
        {
            (model.Says, model.Asks, model.SaysIfAskedAgain) = (says, asks, again);
            string heard = Tell($"prueba del guardián: {title}");
            check($"guardián: {title}", heard.Contains(expected, StringComparison.OrdinalIgnoreCase), heard);
        }
        Guarded("deja pasar lo que se deduce de la memoria", "Tu hermana se llama Ana, qué nombre tan bonito.", "qué nombre tan bonito");
        Guarded("deja pasar una deducción", "Tu hermana vive en Santiago.", "vive en Santiago");
        Guarded("frena lo que contradice la memoria", "Tu hermana se llama Marta.", "Me dijiste que tu hermana se llama Ana");
        Guarded("frena otro dato contradicho", "Claro, tu color favorito es el verde.", "tu color favorito es el azul");
        Guarded("frena un dato inventado", "Tu perro se llama Max y es muy juguetón.", "no me lo has contado");
        Guarded("no confunde una pregunta con una afirmación", "¿Tu perro se llama Max?", "¿Tu perro se llama Max?");
        Guarded("frena un «ya está hecho» sin confirmación", "Aquí tienes la calculadora, ya está abierta.", "No lo hice", ("abrir", new { destino = "calculadora" }));
        Guarded("no toca una respuesta normal", "Los pulpos tienen tres corazones.", "tres corazones");
        Guarded("si cuenta que usa una herramienta en vez de usarla, se lo pide otra vez",
            "*Llamando a la herramienta investigar con consulta: pulpos*", "dice: Tienen tres corazones.", again: "Tienen tres corazones.");
        Guarded("si contesta solo con símbolos, se lo pide otra vez", "[]", "dice: La capital de Francia es París.", again: "La capital de Francia es París.");
        Guarded("si finge un gesto sin hacerlo y insiste, no lo dice", "*Me escondo detrás de la ventana* Ya no me ves.", "no me salió");
        Guarded("si dice que lo apuntó o que avisará sin haberlo hecho, se lo pide otra vez",
            "Te lo acabo de guardar, así que me acordaré de avisarte a las cinco.", "dice: No he podido dejarlo programado.", again: "No he podido dejarlo programado.");
        Guarded("si dice que cambió de aspecto sin hacerlo, se lo pide otra vez",
            "Ahora soy más grande.", "dice: Voy a ello.", again: "Voy a ello.");
        Guarded("un cambio de aspecto hecho de verdad sí puede contarlo",
            "Ahora soy más grande.", "dice: Ahora soy más grande.", ("tamano", new { tamano = "mas_grande" }));
        Guarded("un gesto hecho de verdad sí puede contarlo", "(Me pongo a bailar) ¿Qué tal?", "¿Qué tal?", ("cuerpo", new { accion = "bailar" }));

        // Plan → ejecutar → verificar → corregir: si una herramienta falla y el modelo lo oculta, hay una ronda
        // más para arreglarlo; lo que no se arregla se dice como fallo, nunca como hecho.
        body.RechazaCierre.Add(2);   // la ventana del navegador se resiste, como si Windows no la soltara
        model.Asks = ("cerrar", new { ventana = "chrome" });
        model.SaysOnCorrection = null;
        model.Says = "No pude cerrar el navegador, lo siento.";
        model.Replies = 0;
        string admitido = Tell("¿podrías cerrar el navegador, por favor?");
        check("si el modelo ya admite el fallo, no hay ronda extra", model.Replies == 1 && admitido.Contains("No pude cerrar «Recetas"), admitido);
        model.Says = "¡Listo! Ya cerré el navegador.";
        model.Replies = 0;
        string ocultado = Tell("¿podrías cerrar el navegador, por favor?");
        check("una herramienta que falla y se oculta provoca una ronda de corrección", model.Replies == 2, $"{model.Replies} respuestas");
        check("lo que no se pudo arreglar se dice tal cual, no como hecho",
            ocultado.Contains("No pude cerrar «Recetas de arroz") && !ocultado.Contains("¡Listo!"), ocultado);
        model.Says = "¡Listo! Ya cerré el navegador.";
        model.AsksCorrection = ("cerrar", new { ventana = "notepad" });
        model.SaysOnCorrection = "El navegador no se dejó, pero cerré el bloc de notas.";
        model.Replies = 0;
        string corregido = Tell("¿podrías cerrar el navegador, por favor?");
        check("la ronda de corrección arregla lo que puede y lo cuenta",
            model.Replies == 2 && corregido.Contains("cerré el bloc de notas") && corregido.Contains("cierra: Informe trimestral - Bloc de notas")
            && !corregido.Contains("sigue abierta tras dos intentos"), corregido);
        body.RechazaCierre.Clear();
    }

    /// <summary>Frases escritas contra su cerebro propio: sin modelo de lenguaje y, salvo Wikipedia, sin internet.</summary>
    private static void OwnBrainTest(TextWriter o, Config cfg, Action<string, bool, string> check)
    {
        o.WriteLine("CEREBRO PROPIO (sin modelo de lenguaje)");
        var memory = new Memory();
        memory.Clear();
        var body = new FakeBody(o) { Quiet = true };
        var mind = new Mind(cfg, body, memory) { ConfirmTimeout = TimeSpan.FromMilliseconds(200) };

        // Dice una frase y devuelve todo lo que pasó a raíz de ella.
        List<string> Tell(string phrase)
        {
            int before = body.Did.Count;
            mind.Hear(phrase).GetAwaiter().GetResult();
            return body.Did.Skip(before).ToList();
        }
        void Expect(string phrase, string fragment, string? not = null)
        {
            var did = Tell(phrase);
            bool ok = did.Any(d => d.Contains(fragment, StringComparison.OrdinalIgnoreCase)) && (not == null || !did.Any(d => d.Contains(not, StringComparison.OrdinalIgnoreCase)));
            check($"«{phrase}» → {fragment}", ok, ok ? "" : string.Join(" | ", did.DefaultIfEmpty("(nada)")));
        }

        Expect("¿Cuánto es doce por ocho?", "Da 96");
        Expect("cuánto es 3,5 más 1,5", "Da 5.");
        Expect("el 25 por ciento de 80", "Da 20");
        Expect("raíz cuadrada de 144", "Da 12");
        Expect("cuánto es dos mil quinientos entre cuatro", "Da 625");
        Expect("cuánto es 10 entre 0", "no se divide");

        Expect("Me llamo Yemsy", "Anotado: te llamas Yemsy");
        Expect("¿Cómo me llamo?", "te llamas Yemsy");
        Expect("¿cuál es mi nombre?", "te llamas Yemsy");
        Expect("mi color favorito es el azul", "Anotado: tu color favorito es el azul");
        Expect("mi color favorito es el verde", "Actualizado");
        Expect("¿Cuál es mi color favorito?", "verde", not: "azul");
        Expect("me gusta el café sin azúcar", "Anotado: te gusta el café");
        Expect("trabajo de programador por las noches", "trabajas de programador");
        Expect("¿dónde vivo?", "no me lo has contado");
        Expect("bueno, ya no: ahora mi color favorito es el rojo", "Actualizado: ahora tu color favorito es el rojo");
        Expect("¿Cuál es mi color favorito?", "rojo", not: "verde");
        Expect("mi hermana se llama Ana y vive en Santiago", "Anotado: tu hermana se llama Ana y vive en Santiago");
        Expect("¿cómo se llama mi hermana?", "tu hermana se llama Ana");
        Expect("¿cómo se llama mi perro?", "no me lo has contado");

        Expect("cuando diga buenos días jefa responde Buenos días, jefe", "Aprendido");
        Expect("buenos días jefa", "dice: Buenos días, jefe");
        Expect("cuando diga modo fiesta haz baila y luego salta", "Aprendido");
        Expect("modo fiesta", "hace: bailar");
        Expect("cuando diga hola responde qué lo que", "Aprendido");
        Expect("hola", "dice: qué lo que");
        Expect("enciende la lámpara del salón", "todavía no lo sé hacer");
        Expect("significa salta", "Aprendido");
        Expect("enciende la lámpara del salón", "hace: saltar");
        Expect("olvida modo fiesta", "ya no la sé");
        Expect("modo fiesta", "no lo sé hacer", not: "hace: bailar");

        // Cuando no entiende, propone lo más parecido; con un «sí» lo hace y se lo aprende.
        Expect("tira dados", "¿Quisiste decir «tira un dado»?");
        Expect("sí", "Salió un");
        Expect("tira dados", "Salió un", not: "Quisiste");

        // Lo que oye de fondo, sin su nombre: solo reacciona si es una orden clara, y no aprende de ello.
        int quiet = body.Did.Count;
        mind.Hear("pues ayer fuimos al mercado con mi tía", HeardFrom.Background).GetAwaiter().GetResult();
        mind.Hear("me gusta el fútbol", HeardFrom.Background).GetAwaiter().GetResult();
        mind.Hear("vale", HeardFrom.Background).GetAwaiter().GetResult();
        check("lo que oye de fondo y no entiende lo deja pasar en silencio", body.Did.Count == quiet, string.Join(" | ", body.Did.Skip(quiet)));
        check("y de lo oído de fondo no apunta datos", !memory.All.Any(f => f.Texto.Contains("fútbol")), "");
        mind.Hear("salta", HeardFrom.Background).GetAwaiter().GetResult();
        check("una orden de fondo no mueve el cuerpo", !body.Did.Skip(quiet).Contains("hace: saltar"), "");
        int before = body.Did.Count;
        mind.Hear("zarandea el trasto ese", HeardFrom.Named).GetAwaiter().GetResult();
        check("de voz, si no entiende, dice lo que oyó sin preguntar qué significa",
            body.Did.Skip(before).Any(d => d.Contains("No te entendí bien. Oí «zarandea el trasto ese»")), string.Join(" | ", body.Did.Skip(before)));

        Expect("más grande", "tamaño: mas_grande");
        Expect("tamaño normal", "tamaño: normal");
        Expect("puedes abrir el navegador", "abre: https://www.google.com");
        Expect("podrías abrirme el navegador por favor", "abre: https://www.google.com");

        Expect("avísame en 10 minutos", "aviso en 600 s");
        Expect("recuérdame en media hora que saque la basura", "aviso en 1800 s");
        body.Timers[^1].Fire();
        check("el aviso dice de qué era", body.LastSaid.Contains("saque la basura"), body.LastSaid);
        Expect("recuérdame que llame a mi mamá en 5 minutos", "aviso en 300 s");
        body.Timers[^1].Fire();
        check("y habla de «tu mamá», no de «mi mamá»", body.LastSaid.Contains("llame a tu mama"), body.LastSaid);

        // Lo que el usuario pide con sus palabras se hace en el acto; escribir en una ventana sigue pidiendo confirmación.
        Expect("abre la calculadora", "abre: calc.exe");
        check("…y lo dice", body.LastSaid == "Abrí la calculadora y está al frente.", body.LastSaid);
        Expect("abre la carpeta de descargas", "Downloads");
        Expect("sube el volumen", "pulsa: subir volumen");
        Expect("salta a la siguiente canción", "pulsa: siguiente pista");
        Expect("busca recetas de arroz en Google", "abre: https://www.google.com/search?q=recetas");
        Expect("cierra el bloc de notas", "cierra: Informe trimestral - Bloc de notas");
        Expect("cierra el navegador", "cierra: Recetas de arroz - Google Chrome");
        Expect("cierra esta ventana", "cierra: Descargas");   // las otras dos ya se cerraron: la de delante es esta
        body.ResetWindows();   // vuelve a haber ventanas: se comprueba que no encuentra una que no existe
        Expect("cierra la ventana de las fotos", "No encuentro ninguna ventana así");
        Expect("escribe voy en camino", "Voy a escribir «voy en camino»", not: "teclea");
        Expect("¿qué dice en la pantalla?", "necesito el modelo local");
        check("por su ruta no abre programas, solo carpetas y documentos",
            ToolBox.ResolveTarget(@"C:\Windows\System32\cmd.exe") == null && ToolBox.ResolveTarget(@"C:\Windows")?.Label == "la carpeta Windows", "");
        Expect("recuérdame a las cinco de la tarde que llame al banco", "a las 17:00");
        check("…y deja el aviso puesto", body.Timers.Count > 0 && body.Timers[^1].Delay > TimeSpan.Zero && body.Timers[^1].Delay <= TimeSpan.FromHours(24), $"{body.Timers.LastOrDefault().Delay}");
        bool Clock(string said, int hour, int minute, bool tomorrow = false) =>
            OwnBrain.TryClock(Reflexes.Normalize(said), out DateTime at, out _) && at.Hour == hour && at.Minute == minute && (!tomorrow || at.Date == DateTime.Today.AddDays(1));
        check("entiende las horas dichas de palabra",
            Clock("a las cinco y media de la tarde", 17, 30) && Clock("mañana a las 8", 8, 0, tomorrow: true) && Clock("a las 17:30", 17, 30)
            && Clock("a las seis menos cuarto de la mañana", 5, 45) && Clock("a la una de la tarde", 13, 0), "");
        string alarm = ToolBox.Run("avisar", JsonSerializer.SerializeToElement(new { cuando = "en 20 minutos", mensaje = "sacar la comida del horno" }), mind).GetAwaiter().GetResult();
        check("herramienta avisar", alarm.StartsWith("Hecho: te aviso") && body.Timers[^1].Delay is { TotalMinutes: > 19 and < 21 }, alarm);

        // Los avisos quedan guardados y sobreviven al cierre: al arrancar se reprograman o se cuentan como pendientes.
        var saved = new Recordatorios();
        var futuro = saved.Add(DateTime.Now.AddHours(2), "¡Aviso! Ya pasaron 2 horas: regar las plantas.", "regar las plantas");
        saved.Add(DateTime.Now.AddHours(-1), "¡Aviso! de hace una hora.", "apagar el horno");
        var viejo = saved.Add(DateTime.Now.AddDays(-3), "¡Aviso! de hace días.", "");
        check("un aviso de hace días se descarta al recoger los pendientes", saved.Pending(DateTime.Now).All(a => a.Id != viejo.Id), "");
        int saidBefore = body.Did.Count, timersBefore = body.Timers.Count;
        var revived = new Mind(cfg, body, memory);
        revived.RestoreReminders();
        check("al arrancar devuelve a la vida el aviso futuro guardado",
            body.Timers.Count > timersBefore && Math.Abs(body.Timers[^1].Delay.TotalHours - 2) < 0.1,
            string.Join(" | ", body.Timers.Skip(timersBefore).Select(t => $"{(int)t.Delay.TotalMinutes}m")));
        check("y cuenta el que venció mientras estaba cerrada",
            body.Did.Skip(saidBefore).Any(d => d.Contains("apagar el horno")), string.Join(" | ", body.Did.Skip(saidBefore)));
        body.Timers[^1].Fire();
        check("al cumplirse dice su mensaje y se borra del disco",
            body.LastSaid.Contains("regar las plantas") && new Recordatorios().Pending(DateTime.Now).All(a => a.Id != futuro.Id), body.LastSaid);
        body.ResetWindows();   // las tres ventanas se cerraron de verdad en las pruebas de cierre
        Expect("¿qué programas tengo abiertos?", "3 ventanas abiertas");

        // Lecciones con versiones y deshacer: reenseñar guarda la anterior y «deshaz» la devuelve o la borra.
        Expect("cuando diga prende el ventilador, haz salta", "Aprendido");
        Expect("cuando diga prende el ventilador, haz baila", "Aprendido");
        Expect("prende el ventilador", "hace: bailar");
        Expect("deshaz la lección de prende el ventilador", "versión anterior");
        Expect("prende el ventilador", "hace: saltar");
        Expect("deshaz la última lección", "Ya no sé qué hacer");
        Expect("prende el ventilador", "Eso todavía no lo sé hacer", not: "hace: saltar");
        Expect("deshaz la lección de algo que no existe", "No tengo ninguna lección");

        // Una orden que sale mal propone una lección corregida; solo se guarda si el usuario dice «sí».
        Expect("abre el bloc de nota", "¿Quieres que cuando digas «abre el bloc de nota» abra el bloc de notas?");
        Expect("no", "no aprendo nada");
        Expect("abre el bloc de nota", "¿Quieres que cuando digas «abre el bloc de nota» abra el bloc de notas?", not: "abre: notepad");
        Expect("abre el bloc de nota", "¿Quieres que cuando digas «abre el bloc de nota» abra el bloc de notas?");
        Expect("sí", "abre: notepad.exe");
        Expect("abre el bloc de nota", "abre: notepad.exe", not: "¿Quieres que");
        Expect("cuando diga abre el bloc de nota, responde eso no existe", "Aprendido");
        Expect("abre el bloc de nota", "dice: eso no existe", not: "¿Quieres que");
        Expect("olvida abre el bloc de nota", "ya no la sé");
        Expect("¿cuánto espacio libre me queda en el disco?", "GB libres");
        check("encuentra los programas del menú Inicio", Device.Programs().Count > 0, $"{Device.Programs().Count} programas");
        check("si pide una carpeta que no existe, no abre un programa de nombre parecido",
            ToolBox.ResolveTarget("la carpeta de proyectos secretos de marte") == null, ToolBox.ResolveTarget("la carpeta de proyectos secretos de marte")?.Label ?? "");
        // Lo pedido hace un momento sigue valiendo: la frase puede llegar partida en dos al hablar.
        Tell("abre la carpeta");
        int openedBefore = body.Did.Count(d => d.StartsWith("abre:"));
        string second = ToolBox.Run("abrir", JsonSerializer.SerializeToElement(new { destino = "descargas" }), mind).GetAwaiter().GetResult();
        check("un destino aún ambiguo requiere aclaración o confirmación", second == ToolBox.Denied && body.Did.Count(d => d.StartsWith("abre:")) == openedBefore, second);
        // De verdad, sin doble: la foto se hace, se comprueba que es una imagen y se tira; no se guarda ni se enseña.
        byte[]? shot = Device.Screenshot();
        check("puede fotografiar la pantalla", shot is { Length: > 5000 } && shot[0] == 0xFF && shot[1] == 0xD8, $"{(shot?.Length ?? 0) / 1024} KB");
        check("ve qué ventanas hay abiertas", Device.OpenWindows().Count > 0, $"{Device.OpenWindows().Count} ventanas");
        Expect("abre el programa secreto", "No encuentro eso para abrirlo", not: "abre:");

        Expect("hablemos", "conversación: True");
        Expect("¿cómo estás?", "dice: ");
        Expect("cuéntame un chiste", "hace: saltar", not: "no sé");   // cuenta uno al azar y da un salto
        Expect("lanza una moneda", "Salió c");
        Expect("habla más despacio", "voz: tono 0 velocidad -1");
        Expect("otra voz", "cambia de voz: siguiente");
        Expect("Chispa, voz de hombre", "Esta es la voz de hombre número 1");
        Expect("me gusta esta voz", "Me quedo con esta voz");
        Expect("ahora te llamas Luna", "nombre: Luna");
        Expect("eso es todo", "conversación: False");
        Expect("adiós", "conversación: False");
        Expect("ayuda", "abre la guía");

        // Investiga por su cuenta (necesita internet) y se queda con lo aprendido.
        var wiki = Tell("quién fue Juan Pablo Duarte");
        check("«quién fue…» lo busca en Wikipedia", wiki.Any(d => d.Contains("Wikipedia") || d.Contains("internet")), string.Join(" | ", wiki));
        var capital = Tell("¿Cuál es la capital de Francia?");
        check("una pregunta que no conoce la investiga sola", capital.Any(d => d.Contains("París") || d.Contains("internet")), string.Join(" | ", capital));
        if (capital.Any(d => d.Contains("París")))
        {
            var again = Tell("dime cuál es la capital de Francia");
            check("y la segunda vez ya lo sabe, sin buscar", again.Any(d => d.Contains("París")) && !again.Any(d => d.Contains("Lo busqué")), string.Join(" | ", again));
        }
        var sky = Tell("¿Por qué el cielo es azul?");
        var weatherReply = Tell("¿Qué tiempo hace en Santo Domingo?");
        check("mira el tiempo de una ciudad", weatherReply.Any(d => d.Contains("grados") || d.Contains("internet")), string.Join(" | ", weatherReply));
        Expect("ponme música de Juan Luis Guerra", "abre: https://www.youtube.com/results?search_query=juan");
        body.Quiet = false;
        check("ejemplo de investigación", true, string.Join(" | ", sky));

        using var guide = typeof(Diagnostics).Assembly.GetManifestResourceStream("GUIA.md");
        string html = guide == null ? "" : Guide.ToHtml(new StreamReader(guide).ReadToEnd());
        check("la guía va dentro del programa y se convierte en página", html.Contains("<table>") && html.Contains("<h2>") && html.Contains("<em>"), $"{html.Length} caracteres");
    }

    /// <summary>Genera frases con la voz del sistema y comprueba qué entiende el oído con ellas.</summary>
    private static void VoiceTest(TextWriter o, Config cfg, Action<string, bool, string> check)
    {
        o.WriteLine("OÍDO (frases sintetizadas, sin micrófono)");
        using var ears = new Ears(cfg);
        if (ears.Problem.Length > 0)
        {
            check("reconocedor en español instalado", false, ears.Problem);
            return;
        }
        var speaker = VoiceCatalog.Current(cfg);
        var render = VoiceLab.Renderer(speaker);
        o.WriteLine($"Voz de prueba: {speaker.Menu}");
        byte[] Speak(string text)
        {
            var (samples, rate) = render(text);
            var mono = Pcm.Resample(samples, rate, Pcm.Rate);
            var padded = new float[mono.Length + Pcm.Rate];
            mono.CopyTo(padded, Pcm.Rate / 3);
            return Pcm.ToWav(VoiceLab.ToPcm16(padded));
        }
        (string Said, bool Attentive, HeardKind Kind, string Contains)[] cases =
        [
            ("Chispa", false, HeardKind.Wake, ""),
            ("Chispa, salta", false, HeardKind.Command, "saltar"),
            ("oye Chispa, ¿qué hora es?", false, HeardKind.Command, "hora"),
            ("Chispa, más grande", false, HeardKind.Command, "grande"),
            ("Chispa, confirmo", false, HeardKind.Command, "confirmo"),
            ("Chispa, cuéntame un chiste corto", false, HeardKind.Speech, "chiste"),
            ("Chispa, recuerda que mi color favorito es el azul", false, HeardKind.Speech, "azul"),
            ("Chispa, salta a la siguiente canción", false, HeardKind.Speech, "canción"),   // empieza como una orden, pero no lo es
            ("Chispa, abre el navegador y busca recetas de arroz con pollo", false, HeardKind.Speech, "navegador"),
            ("la chica pasa por la calle", false, HeardKind.Nothing, ""),
            ("hoy vamos a comer pizza con los amigos", false, HeardKind.Nothing, ""),
            ("dispara la cámara ahora", false, HeardKind.Nothing, ""),
            ("salta", false, HeardKind.Nothing, ""),
            ("confirmo", false, HeardKind.Nothing, ""),
            ("confirmo", true, HeardKind.Command, "confirmo"),
            ("salta", true, HeardKind.Command, "saltar"),
            ("cuánto es dos más dos", true, HeardKind.Speech, "cuánto"),
        ];
        foreach (var (said, attentive, kind, contains) in cases)
        {
            var clock = Stopwatch.StartNew();
            Heard heard = ears.TestWave(Speak(said), attentive);
            string interpreted = Reflexes.Normalize(heard.Value, cfg.Nombre);
            string? command = interpreted == Reflexes.Normalize(cfg.FraseConfirmacion) ? "confirmo" : Reflexes.Match(interpreted);
            bool ok = heard.Kind == kind && heard.Value.Contains(contains, StringComparison.OrdinalIgnoreCase)
                || kind == HeardKind.Command && heard.Kind == HeardKind.Speech && command == contains;
            check($"{(attentive ? "[atenta] " : "")}«{said}» → {kind} {contains}", ok,
                ok ? $"{clock.ElapsedMilliseconds} ms" : $"oyó {heard.Kind} «{heard.Value}» en {clock.ElapsedMilliseconds} ms · {ears.Trace}");
        }

        // Una pausa tras el nombre («Chispa… ¿cuál es la capital de Francia?»): la petición tiene que llegar entera,
        // no quedarse en un «¿Sí?» dicho encima de ella.
        // La voz sintética trae silencio propio al principio y al final: se recorta para que la pausa sea la que se dice.
        byte[] Samples(string said)
        {
            byte[] pcm = Speak(said)[Pcm.WavHeader..];
            double[] levels = Pcm.Levels(pcm);
            double floor = levels.Max() * 0.1;
            int first = Array.FindIndex(levels, l => l > floor), last = Array.FindLastIndex(levels, l => l > floor);
            return pcm[(first * Pcm.BlockBytes)..((last + 1) * Pcm.BlockBytes)];
        }
        Heard AfterPause(int pauseMs) => ears.TestWave(Pcm.ToWav(
            [.. new byte[Pcm.BytesPerSecond / 4], .. Samples("Chispa"), .. new byte[Pcm.BytesPerSecond * pauseMs / 1000 & ~1],
             .. Samples("cuál es la capital de Francia"), .. new byte[Pcm.BytesPerSecond]]));

        Heard late = AfterPause(1000);
        check("«Chispa… (pausa de 1 s) cuál es la capital de Francia» → la petición entera, sin «¿Sí?» por medio",
            late.Kind == HeardKind.Speech && late.Named && late.Value.Contains("capital", StringComparison.OrdinalIgnoreCase),
            $"oyó {late.Kind} «{late.Value}» · {ears.Trace}");
        Heard alone = AfterPause(3000);
        check("con tres segundos de silencio tras el nombre, contesta «¿Sí?»", alone.Kind == HeardKind.Wake, $"oyó {alone.Kind} «{alone.Value}» · {ears.Trace}");
    }

    /// <summary>
    /// Los dispositivos de grabación que ve el equipo ahora mismo. Solo informa (no es una comprobación):
    /// el número de dispositivos depende de lo que esté enchufado, y si la asistente se perdió el
    /// micrófono, aquí se ve si Windows lo sigue teniendo.
    /// </summary>
    private static void DevicesTest(TextWriter o)
    {
        o.WriteLine("DISPOSITIVOS DE GRABACIÓN (los que ve NAudio)");
        int count = WaveInEvent.DeviceCount;
        var names = new string[Math.Min(count, 8)];
        for (int i = 0; i < names.Length; i++)
        {
            try { names[i] = WaveInEvent.GetCapabilities(i).ProductName; }
            catch (Exception) { names[i] = "?"; }
        }
        o.WriteLine(count == 0 ? "  ninguno" : "  " + string.Join(" · ", names));
    }

    /// <summary>El VAD de Silero distingue voz de silencio, ruido y tonos, sin micrófono ni altavoz.</summary>
    private static void VadTest(TextWriter o, Config cfg, Action<string, bool, string> check)
    {
        o.WriteLine("VAD (Silero, sin micrófono)");
        if (SileroVad.ModelPath == null)
        {
            check("el modelo silero_vad.onnx está en la carpeta oido", false, "no está");
            return;
        }
        using var vad = new SileroVad();
        check("el modelo Silero abre y queda disponible", vad.Available, SileroVad.ModelPath);
        if (!vad.Available) return;

        vad.Reset();
        bool[] silence = vad.Mark(new float[Pcm.Rate * 2]) ?? [];
        check("el silencio no es voz", silence.Length > 0 && silence.All(v => !v), $"{silence.Count(v => v)} tramas con voz");

        // Ruido de fondo (habitación, ventilador) a un volumen bajo, y un tono sostenido (música): no son voz.
        var rnd = new Random(7);
        float[] NoiseAndTone(int seconds, double noiseAmp, double toneAmp, double toneHz)
        {
            var f = new float[Pcm.Rate * seconds];
            for (int i = 0; i < f.Length; i++)
                f[i] = (float)(noiseAmp * (rnd.NextDouble() * 2 - 1) + toneAmp * Math.Sin(2 * Math.PI * toneHz * i / Pcm.Rate));
            return f;
        }
        vad.Reset();
        bool[] noisy = vad.Mark(NoiseAndTone(2, 0.02, 0, 0)) ?? [];
        check("el ruido de fondo no es voz", noisy.Length > 0 && noisy.All(v => !v), $"{noisy.Count(v => v)} tramas con voz");
        vad.Reset();
        bool[] tono = vad.Mark(NoiseAndTone(2, 0, 0.02, 440)) ?? [];
        check("un tono sostenido no es voz", tono.Length > 0 && tono.All(v => !v), $"{tono.Count(v => v)} tramas con voz");

        // Voz de verdad: la del sistema diciendo una frase, con silencio alrededor.
        try
        {
            var speaker = VoiceCatalog.Current(cfg);
            var (samples, rate) = VoiceLab.Renderer(speaker)("¿Qué hora es?");
            var mono = Pcm.Resample(samples, rate, Pcm.Rate);
            var padded = new float[mono.Length + Pcm.Rate];   // medio segundo de silencio delante y detrás
            mono.CopyTo(padded, Pcm.Rate / 2);
            vad.Reset();
            bool[] spoken = vad.Mark(padded) ?? [];
            int first = spoken.Length / 10, middle = spoken.Length / 3, last = spoken.Length - spoken.Length / 10;
            bool edgesQuiet = spoken[..first].All(v => !v) && spoken[last..].All(v => !v);
            bool voiceInside = spoken[middle..(middle + spoken.Length / 3)].Any(v => v);
            check("la voz se detecta y el silencio de alrededor no",
                spoken.Length > 0 && edgesQuiet && voiceInside,
                $"tramas={spoken.Length}, voz={spoken.Count(v => v)}");
        }
        catch (Exception ex)
        {
            check("la voz sintética para el VAD se puede generar", false, ex.Message);
        }
    }

    /// <summary>
    /// El motor de palabra de activación (openWakeWord) oye «chispa» en las voces de la propia Chispa y
    /// calla ante palabras parecidas. Sin micrófono: las voces se generan y se pasan por el motor como
    /// si llegaran del micro. Los tonos y la música no son voz y los descarta la confirmación del VAD
    /// en el oído de verdad; aquí solo se mira si el motor puntúa palabras y silencio como se espera.
    /// </summary>
    private static void WakeTest(TextWriter o, Config cfg, Action<string, bool, string> check)
    {
        o.WriteLine("Palabra de activación (openWakeWord, sin micrófono)");
        if (!WakeWordEngine.ModelsInstalled)
        {
            check("los modelos del oído moderno (chispa_wake, melspectrogram y embedding) están en la carpeta oido",
                false, "falta alguno de los tres");
            return;
        }
        using var wake = new WakeWordEngine();
        check("el motor de palabra de activación abre sus tres modelos", wake.Available, WakeWordEngine.ModelPath!);
        if (!wake.Available) return;

        static byte[] WithSilence(float[] at16, float beforeS, float afterS)
        {
            var padded = new float[(int)((beforeS + afterS) * Pcm.Rate) + at16.Length];
            at16.CopyTo(padded, (int)(beforeS * Pcm.Rate));
            return Pcm.ToWav(VoiceLab.ToPcm16(padded));
        }
        static float ScoreOf(WakeWordEngine wake, Func<string, (float[] Wav, int Rate)> render, string text)
        {
            var (samples, rate) = render(text);
            var (max, _) = wake.TestWave(WithSilence(Pcm.Resample(samples, rate, Pcm.Rate), 1.0f, 1.2f));
            return max;
        }

        // Positivos: cada voz dice las tres formas de llamarla; basta con que una despierte.
        var detail = new List<string>();
        bool AnyWakes(WakeWordEngine wake, Func<string, (float[] Wav, int Rate)> render, string[] phrases, string label)
        {
            var scores = phrases.Select(p => ScoreOf(wake, render, p)).ToArray();
            detail.Add($"{label}: {string.Join(" · ", phrases.Zip(scores).Select(z => $"{z.First} {z.Second:F2}"))}");
            return scores.Any(s => s >= WakeWordEngine.Threshold);
        }
        string[] calls = ["Chispa", "Oye Chispa", "Hola Chispa"];

        bool piperWakes = false, windowsWakes = false, supertonicWakes = false;
        try
        {
            var piper = VoiceCatalog.All().FirstOrDefault(v => v.Kind == "piper");
            if (piper != null) piperWakes = AnyWakes(wake, VoiceLab.Renderer(piper), calls, piper.Id);
            SupertonicVoice.Unload();
            var windows = VoiceCatalog.All().FirstOrDefault(v => v.Kind == "windows");
            if (windows != null) windowsWakes = AnyWakes(wake, VoiceLab.Renderer(windows), calls, windows.Id);
            SupertonicVoice.Unload();
            var super = VoiceCatalog.All().FirstOrDefault(v => v.Id == "F5") ?? VoiceCatalog.All().FirstOrDefault(v => v.Kind == "supertonic");
            if (super != null) supertonicWakes = AnyWakes(wake, VoiceLab.Renderer(super), calls, super.Id);
            SupertonicVoice.Unload();
        }
        catch (Exception ex)
        {
            check("las voces para la prueba de activación se pueden generar", false, ex.Message);
            return;
        }
        o.WriteLine("  " + string.Join("\n  ", detail));
        check("«chispa» dicha por las voces de la propia Chispa despierta al motor",
            piperWakes && windowsWakes && supertonicWakes,
            $"piper {(piperWakes ? "sí" : "no")} · windows {(windowsWakes ? "sí" : "no")} · supertonic {(supertonicWakes ? "sí" : "no")}");

        // Negativos: palabras parecidas, con la voz con la que el modelo mejor las conoce.
        string[] notHer = ["Pista", "El cristal de la mesa", "Qué chistoso", "La vista es bonita", "Me da risa"];
        try
        {
            var speaker = VoiceCatalog.All().FirstOrDefault(v => v.Kind == "piper") ?? VoiceCatalog.Current(cfg);
            var render = VoiceLab.Renderer(speaker);
            int falsos = 0;
            foreach (string frase in notHer)
                if (ScoreOf(wake, render, frase) >= WakeWordEngine.Threshold) falsos++;
            SupertonicVoice.Unload();
            check("palabras parecidas a «chispa» no despiertan al motor", falsos == 0, $"{falsos} de {notHer.Length} dispararon");
        }
        catch (Exception ex)
        {
            check("las frases de control se pueden generar", false, ex.Message);
        }

        // Silencio: no debe despertar nunca.
        var (quietMax, _) = wake.TestWave(Pcm.ToWav(new byte[Pcm.BytesPerSecond * 3]));
        check("el silencio no despierta al motor", quietMax < WakeWordEngine.Threshold, $"puntúa {quietMax:F2}");

        // Paridad con el entrenamiento: si hay un wav de referencia en la carpeta oido, se puntúa y la
        // cifra se puede comparar con la que da openWakeWord en Python sobre el mismo archivo.
        if (Oido.Model("prueba_paridad.wav") is { } probe)
        {
            var (probeMax, _) = wake.TestWave(File.ReadAllBytes(probe));
            o.WriteLine($"  (paridad: {Path.GetFileName(probe)} puntúa {probeMax:F2} con este motor)");
        }
    }
}

