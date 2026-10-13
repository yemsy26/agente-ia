using System.Runtime.InteropServices;

namespace Chispa;

internal sealed class UserProfile
{
    public string NombrePreferido { get; set; } = "";
    public bool TonoDulce { get; set; } = true;
    public string Instructions => (TonoDulce
        ? "Habla con un tono dulce, delicado y tranquilo. Usa frases breves; sin exageraciones, infantilizar, coquetear ni insistir."
        : "Habla con un tono cordial, directo y breve.") +
        (string.IsNullOrWhiteSpace(NombrePreferido) ? "" : $" El nombre preferido que indicó el usuario es: {NombrePreferido}.") +
        " El acompañamiento de bienestar solo lo controla el usuario en sus ajustes. No inventes observaciones sobre postura, comidas, salud o presencia. No programes avisos de salud por iniciativa propia.";
}

internal sealed class CompanionPreferences
{
    public bool Habilitado { get; set; } // consentimiento desde los ajustes
    public int MinutosPausa { get; set; } = 45;
    public int MinutosAusencia { get; set; } = 5;
    public bool HablarAvisos { get; set; } // voz opcional, nunca sustituye el permiso para una actividad
    public bool EjercicioSentado { get; set; } = true;
    public bool Agua { get; set; }
    public bool Comida { get; set; }
    public DateTime SilencioHastaUtc { get; set; }
}

/// <summary>Estimación local por entrada de teclado/ratón y escritorio desbloqueado. No identifica personas.</summary>
internal static class Presence
{
    internal static string LastStatus { get; private set; } = "Sin muestra";
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder name, int size, out int needed);
    public static double IdleSeconds()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info)) { LastStatus = "No se pudo consultar la actividad"; return double.PositiveInfinity; }
        IntPtr desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS: consulta, sin permiso para cambiar de escritorio
        if (desktop == IntPtr.Zero) { LastStatus = $"Escritorio no accesible ({Marshal.GetLastWin32Error()})"; return double.PositiveInfinity; }
        var desktopName = new System.Text.StringBuilder(256);
        bool readable = GetUserObjectInformation(desktop, 2, desktopName, 512, out _);
        bool interactive = readable && desktopName.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        LastStatus = readable ? $"Escritorio: {desktopName}" : $"No se pudo consultar el escritorio ({Marshal.GetLastWin32Error()})";
        CloseDesktop(desktop);
        if (!interactive) return double.PositiveInfinity;
        return unchecked((uint)Environment.TickCount - info.Tick) / 1000.0;
    }
    public static bool IsFullScreen()
    {
        var handle = Native.GetForegroundWindow();
        if (Native.Bounds(handle) is not { } rect) return false;
        var screen = Screen.FromHandle(handle).Bounds;
        return rect.Width >= screen.Width && rect.Height >= screen.Height;
    }
}

internal enum CareChoice { Accept, Decline, Snooze, Today, Ignore }
internal sealed record CareOffer(string Question, string Activity);

/// <summary>Reloj de actividad: no usa micrófono, cámara, red ni modelos. Todas las actividades requieren aceptar.</summary>
internal sealed class CareClock(CompanionPreferences prefs)
{
    private DateTime? last;
    private DateTime pendingUntil;
    private int ignored, sequence;
    private DateTime day;
    public double ActiveSeconds { get; private set; }
    public CareOffer? Pending { get; private set; }
    public CareOffer? Tick(DateTime now, double idleSeconds, bool available)
    {
        double delta = last is { } before ? Math.Clamp((now - before).TotalSeconds, 0, 10) : 0;
        last = now;
        if (day != now.ToLocalTime().Date) { day = now.ToLocalTime().Date; ignored = 0; }
        if (Pending != null && now >= pendingUntil) Reply(CareChoice.Ignore, now);
        if (!prefs.Habilitado || idleSeconds >= Math.Clamp(prefs.MinutosAusencia, 1, 30) * 60)
        {
            ActiveSeconds = 0;
            Pending = null;
            return null;
        }
        if (now < prefs.SilencioHastaUtc || ignored >= 2) return null;
        // Una pantalla encendida o una tarea ejecutándose no cuenta como trabajo del usuario.
        if (idleSeconds <= 60) ActiveSeconds += delta;
        if (!available || Pending != null || idleSeconds > 60 || ActiveSeconds < Math.Clamp(prefs.MinutosPausa, 15, 180) * 60) return null;
        var offers = new List<CareOffer>
        {
            new("Llevas un rato trabajando. ¿Te apetece una pausa breve?", "Tómate unos minutos a tu ritmo: aparta la mirada de la pantalla y cambia de actividad si te viene bien."),
        };
        if (prefs.EjercicioSentado) offers.Add(new("¿Te apetece un movimiento suave sentado?", "En una silla estable, apoya los pies y relaja los hombros. Si te resulta cómodo, levanta y baja suavemente los talones hasta cinco veces, con las puntas apoyadas. Respira sin forzar; si aparece dolor o mareo, detente."));
        if (prefs.Agua) offers.Add(new("¿Te vendría bien una pausa para beber algo?", "Puedes aprovechar un momento para beber, si te apetece. Sigue tus indicaciones habituales si tienes restricciones de líquidos."));
        if (prefs.Comida) offers.Add(new("¿Ya pudiste comer con calma? ¿Quieres hacer una pausa?", "Si te viene bien, deja el trabajo un rato para comer. Si ya comiste, puedes continuar a tu ritmo."));
        Pending = offers[sequence++ % offers.Count];
        pendingUntil = now.AddSeconds(45);
        ActiveSeconds = 0;
        return Pending;
    }
    public string? Reply(CareChoice choice, DateTime now)
    {
        if (Pending == null) return null;
        if (now >= pendingUntil && choice != CareChoice.Ignore) choice = CareChoice.Ignore;
        var offer = Pending;
        Pending = null;
        ActiveSeconds = 0;
        switch (choice)
        {
            case CareChoice.Accept: ignored = 0; return offer.Activity;
            case CareChoice.Ignore: ignored++; break;
            case CareChoice.Snooze: prefs.SilencioHastaUtc = now.AddMinutes(15); ActiveSeconds = Math.Clamp(prefs.MinutosPausa, 15, 180) * 60; break;
            case CareChoice.Today: prefs.SilencioHastaUtc = now.ToLocalTime().Date.AddDays(1).ToUniversalTime(); break;
        }
        return null;
    }
    public void Reset() { Pending = null; ActiveSeconds = 0; last = null; ignored = 0; }
}

internal sealed class CompanionSettingsForm : Form
{
    public CompanionSettingsForm(Config cfg, Action saved)
    {
        Text = "Chispa · Perfil y acompañamiento";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(510, 580);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        Controls.Add(layout);
        void Label(string text, int height = 30) => layout.Controls.Add(new Label { Text = text, Width = 460, Height = height });
        CheckBox Check(string text, bool value) { var c = new CheckBox { Text = text, Checked = value, Width = 460, Height = 29 }; layout.Controls.Add(c); return c; }
        Label("Cómo quieres que te llame (opcional)");
        var name = new TextBox { Text = cfg.Perfil.NombrePreferido, Width = 440, MaxLength = 40 }; layout.Controls.Add(name);
        var sweet = Check("Tono dulce y delicado, sin exagerar", cfg.Perfil.TonoDulce);
        Label("El acompañamiento es opcional. Cada propuesta espera tu decisión. Puedes rechazarla, posponerla o silenciar el día.", 60);
        var enabled = Check("Activar acompañamiento durante mi actividad", cfg.Acompanamiento.Habilitado);
        Label("Minutos de actividad entre propuestas (15–180)");
        var minutes = new NumericUpDown { Minimum = 15, Maximum = 180, Value = Math.Clamp(cfg.Acompanamiento.MinutosPausa, 15, 180), Width = 120 }; layout.Controls.Add(minutes);
        var seated = Check("Sugerir postura y movimientos suaves sentado", cfg.Acompanamiento.EjercicioSentado);
        var water = Check("Incluir pausas para beber", cfg.Acompanamiento.Agua);
        var food = Check("Incluir preguntas sobre una pausa para comer", cfg.Acompanamiento.Comida);
        var spoken = Check("Decir las propuestas también en voz alta", cfg.Acompanamiento.HablarAvisos);
        Label("Se pausa por inactividad, bloqueo o pantalla completa. No usa cámara ni identifica quién habla. Dos avisos ignorados silencian el resto del día en esta sesión.", 75);
        var save = new Button { Text = "Guardar preferencias", Width = 220, Height = 36 };
        save.Click += (_, _) =>
        {
            cfg.Perfil.NombrePreferido = name.Text.Trim();
            cfg.Perfil.TonoDulce = sweet.Checked;
            var p = cfg.Acompanamiento;
            p.Habilitado = enabled.Checked;
            p.MinutosPausa = (int)minutes.Value;
            p.EjercicioSentado = seated.Checked; p.Agua = water.Checked; p.Comida = food.Checked; p.HablarAvisos = spoken.Checked;
            cfg.Save(); saved(); Close();
        };
        layout.Controls.Add(save);
        AcceptButton = save;
    }
}

internal sealed class CarePromptForm : Form
{
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
    public CarePromptForm(CareOffer offer, Action<CareChoice> answer)
    {
        bool answered = false;
        void Choose(CareChoice choice) { if (answered) return; answered = true; answer(choice); }
        Text = "Chispa · Una pausa a tu ritmo";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 148);
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);
        Controls.Add(new Label { Text = offer.Question, Location = new Point(16, 16), Size = new Size(408, 56) });
        var buttons = new FlowLayoutPanel { Location = new Point(12, 82), Size = new Size(422, 48) };
        Controls.Add(buttons);
        foreach (var (text, choice) in new[] { ("Sí", CareChoice.Accept), ("Ahora no", CareChoice.Decline), ("En 15 min", CareChoice.Snooze), ("Hoy no", CareChoice.Today) })
        {
            var b = new Button { Text = text, Width = 98, Height = 36 };
            b.Click += (_, _) => Choose(choice); buttons.Controls.Add(b);
        }
        FormClosed += (_, _) => Choose(CareChoice.Ignore);
    }
}

