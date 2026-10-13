using System.Diagnostics;

namespace Chispa;

/// <summary>
/// El icono de la bandeja y su menú: aquí vive toda la presentación del menú.
/// Cada clic pide un comportamiento con nombre a la mascota dueña y no toca su estado.
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem alwaysListenItem = new("Escuchar siempre mi nombre") { CheckOnClick = true };
    private readonly ToolStripMenuItem voiceItem = new("Hablar en voz alta") { CheckOnClick = true };

    /// <summary>El menú en sí, para las pruebas de interfaz.</summary>
    internal ContextMenuStrip Menu => menu;

    internal TrayMenu(Config cfg, PetApp owner, string? activeHotkey)
    {
        Build(cfg, owner, activeHotkey);
        tray.Text = cfg.Nombre;
        tray.Icon = PetRenderer.RenderIcon();
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            owner.ToggleHidden();
        };
        tray.Visible = true;
    }

    internal string Text { set => tray.Text = value; }

    internal Icon Icon
    {
        set
        {
            var old = tray.Icon;
            tray.Icon = value;
            old?.Dispose();
        }
    }

    internal void ShowBalloon(int ms, string title, string text) => tray.ShowBalloonTip(ms, title, text, ToolTipIcon.None);

    /// <summary>La mascota se silenció por voz: el menú refleja el estado.</summary>
    internal void SetMuted(bool muted) => voiceItem.Checked = !muted;

    internal void ShowAtCursor()
    {
        // Sin esto el menú no se cierra al hacer clic fuera: la mascota nunca tiene el foco.
        Native.SetForegroundWindow(menu.Handle);
        menu.Show(Cursor.Position);
    }

    public void Dispose()
    {
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
    }

    private void Build(Config cfg, PetApp owner, string? activeHotkey)
    {
        if (!string.IsNullOrEmpty(activeHotkey))
        {
            menu.Items.Add(new ToolStripMenuItem($"{activeHotkey} me llama desde cualquier ventana") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add("Guía: qué puedo decirle", null, (_, _) => Guide.Open());
        menu.Items.Add("Escribirle…", null, (_, _) => owner.OpenChat());
        menu.Items.Add("Perfil y acompañamiento…", null, (_, _) => owner.OpenCompanionSettings());
        menu.Items.Add("Silenciar acompañamiento por hoy", null, (_, _) => owner.SilenceCareToday());
        menu.Items.Add("Escucharme ahora", null, (_, _) => owner.ListenOnce());
        menu.Items.Add("Cancelar petición", null, (_, _) => owner.CancelRequest());
        menu.Items.Add("Diagnóstico: generar informe", null, async (_, _) => await owner.OpenReport());
        menu.Items.Add("Conversar (sin repetir su nombre)", null, (_, _) => owner.ConverseByMenu());

        alwaysListenItem.Checked = cfg.EscuchaContinua;
        alwaysListenItem.CheckedChanged += (_, _) => owner.SetAlwaysListen(alwaysListenItem.Checked);
        menu.Items.Add(alwaysListenItem);

        voiceItem.Checked = !cfg.Silencio;
        voiceItem.CheckedChanged += (_, _) => owner.SetSilence(!voiceItem.Checked);
        menu.Items.Add(voiceItem);

        // Elegir voz: al pulsar una, se presenta con ella para oírla.
        var voices = new ToolStripMenuItem("Voz");
        foreach (var option in VoiceCatalog.All())
            voices.DropDownItems.Add(new ToolStripMenuItem(option.Menu, null, (_, _) => owner.PickVoice(option.Id)) { Tag = option.Id });
        voices.DropDownOpening += (_, _) =>
        {
            string current = VoiceCatalog.Current(cfg).Id;
            foreach (ToolStripMenuItem item in voices.DropDownItems) item.Checked = (string?)item.Tag == current;
        };
        menu.Items.Add(voices);

        // Con qué piensa: su cerebro propio, o además un modelo de lenguaje local para lo que ese no entienda.
        var brains = new ToolStripMenuItem("Cerebro");
        void Brain(string label, string id, string said) =>
            brains.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => owner.PickBrain(id, said)) { Tag = id });
        Brain("Con modelo local: entiende lo que le digas (recomendado)", "auto", "Vale. Lo que no entienda yo sola se lo paso al modelo local. Si Ollama no está abierto, sigo con mi cerebro propio.");
        Brain("Solo el propio: instantáneo, pero entiende poco", "propio", "Vale, pienso solo con mi cerebro propio.");
        brains.DropDownOpening += (_, _) =>
        {
            string current = cfg.Cerebro.Trim().ToLowerInvariant() is "propio" ? "propio" : "auto";
            foreach (ToolStripMenuItem item in brains.DropDownItems) item.Checked = (string?)item.Tag == current;
        };
        menu.Items.Add(brains);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Submenu("Tamaño", ["más grande", "más pequeña", "normal"], v => owner.ChangeSize(v)));
        menu.Items.Add(Submenu("Gestos", ["saludar", "saltar", "bailar", "venir"], v => owner.DoGesture(v)));
        var idleAnimation = new ToolStripMenuItem("Animar el avatar en reposo") { Checked = cfg.AnimacionReposo, CheckOnClick = true };
        idleAnimation.CheckedChanged += (_, _) => owner.SetIdleAnimation(idleAnimation.Checked);
        menu.Items.Add(idleAnimation);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Dormir", null, (_, _) => owner.GetSleepy());
        menu.Items.Add("Esconder o mostrar", null, (_, _) => owner.ToggleHidden());
        menu.Items.Add("Abrir carpeta de datos", null, (_, _) =>
        {
            Directory.CreateDirectory(Config.DataDir);
            Process.Start(new ProcessStartInfo(Config.DataDir) { UseShellExecute = true });
        });
        menu.Items.Add("Salir", null, (_, _) => owner.Quit());
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
}
