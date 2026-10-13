namespace Chispa;

/// <summary>
/// El cuerpo: dibuja la mascota, la mueve por la pantalla y responde al ratón.
/// El reloj se detiene dormida, escondida o en reposo sin animación; los gestos usan 30 fotogramas por segundo.
/// </summary>
internal sealed class PetForm : LayeredForm
{
    private sealed class Anim
    {
        public required string Tag;
        public required double Duration;               // segundos; infinito = bucle hasta Stop
        public required Action<double, double> Step;   // (progreso 0…1, segundos transcurridos)
        public Action? Done;
        public double Elapsed;
    }

    private const int HotkeyId = 1;

    public readonly Pose Pose = new();
    public Look Look { get; }

    public event Action? Clicked, DoubleClicked, MenuRequested, HotkeyPressed, Moved;

    private readonly System.Windows.Forms.Timer clock = new() { Interval = 33 };
    private readonly List<Anim> anims = [];
    private long lastTick;
    private Screen screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    private double centerX, lift;   // píxeles de pantalla; lift = altura sobre el suelo
    private double life, swaySpeed, swayFrom = double.NaN;   // el personaje ilustrado: tiempo despierta y muelle del pelo
    private int topY;
    private byte opacity = 255;
    private bool pressed, dragging;
    private bool animateIdle;
    private Point pressAt, grab;

    private readonly string wantedHotkey;

    /// <summary>El atajo que quedó registrado, o vacío si todos estaban ocupados.</summary>
    public string ActiveHotkey { get; private set; } = "";

    /// <summary>Diagnóstico: el reloj que anima el avatar (sin reflexión sobre campos privados).</summary>
    internal System.Windows.Forms.Timer Clock => clock;

    public PetForm(Look look, string hotkey, double? startX, bool animateIdle = true)
    {
        Look = look;
        this.animateIdle = animateIdle;
        wantedHotkey = hotkey;
        clock.Tick += (_, _) => Tick();
        centerX = startX ?? screen.WorkingArea.Right - 170;
        screen = Screen.FromPoint(new Point((int)centerX, screen.WorkingArea.Bottom - 1));
        ClampToScreen();
    }

    /// <summary>Su posición horizontal en la pantalla.</summary>
    public double CenterX => centerX;

    public double ScaleF => Look.Tamano * DeviceDpi / 96.0 * PetRenderer.Zoom;

    /// <summary>Vida suave en reposo si está activada; durante un gesto siempre acompaña el movimiento.</summary>
    private bool Alive => Visible && !Pose.Asleep && (animateIdle || anims.Count > 0);

    public void SetIdleAnimation(bool enabled)
    {
        animateIdle = enabled;
        if (Alive) StartClock();
        else if (anims.Count == 0)
        {
            clock.Stop();
            Pose.Head = Pose.Nod = Pose.Breath = Pose.Sway = swaySpeed = 0;
            Redraw();
        }
    }

    /// <summary>Punto de la pantalla justo encima de la cabeza (ahí apunta el globo).</summary>
    public Point HeadPoint
    {
        get
        {
            return new Point((int)centerX, topY + (int)(9 * ScaleF));
        }
    }

    public void Redraw()
    {
        if (!IsHandleCreated || !Visible) return;
        Pose.Airborne = lift > 1.5;
        using var bmp = PetRenderer.Render(Pose, ScaleF);
        topY = (int)Math.Round(screen.WorkingArea.Bottom - PetRenderer.Floor * ScaleF - lift);
        Present(bmp, (int)Math.Round(centerX - bmp.Width / 2.0), topY, opacity);
        Moved?.Invoke();
    }

    // ---------- animación ----------

    private void Run(string tag, double seconds, Action<double, double> step, Action? done = null)
    {
        anims.RemoveAll(a => a.Tag == tag);
        anims.Add(new Anim { Tag = tag, Duration = seconds, Step = step, Done = done });
        StartClock();
    }

    private void StartClock()
    {
        if (!Visible || clock.Enabled) return;
        clock.Interval = anims.Count == 0 ? 83 : 33;
        lastTick = Environment.TickCount64;
        clock.Start();
    }

    private void Stop(string tag) => anims.RemoveAll(a => a.Tag == tag);

    private void Tick()
    {
        long now = Environment.TickCount64;
        double dt = Math.Min(0.1, (now - lastTick) / 1000.0);
        lastTick = now;
        foreach (var a in anims.ToArray())
        {
            if (!anims.Contains(a)) continue;   // otra animación la canceló en este mismo paso
            a.Elapsed += dt;
            double p = double.IsInfinity(a.Duration) ? 0 : Math.Min(1, a.Elapsed / a.Duration);
            a.Step(p, a.Elapsed);
            if (p >= 1)
            {
                anims.Remove(a);
                a.Done?.Invoke();
            }
        }
        bool alive = Alive;
        if (alive) Live(dt);
        Redraw();
        if (anims.Count == 0 && !alive) clock.Stop();
        else
        {
            // La respiración es lenta: 12 fps bastan; los gestos y el habla siguen a 30 fps.
            int pace = anims.Count == 0 ? 83 : 33;
            if (clock.Interval != pace) clock.Interval = pace;
        }
    }

    /// <summary>
    /// La vida del personaje ilustrado: respira, el pelo y la falda siguen con retraso lo que se mueve el cuerpo
    /// (un muelle), y la cabeza acompaña lo que está haciendo sin dar saltos.
    /// </summary>
    private void Live(double dt)
    {
        life += dt;
        Pose.Clock = life;
        Pose.Breath = 0.5 - 0.5 * Math.Cos(life * 2 * Math.PI / 3.8);

        double speed = double.IsNaN(swayFrom) ? 0 : (centerX - swayFrom) / Math.Max(dt, 0.001) / ScaleF;
        swayFrom = centerX;
        double pull = Math.Clamp(-speed / 200, -1, 1);
        swaySpeed += ((pull - Pose.Sway) * 60 - swaySpeed * 7) * dt;
        Pose.Sway = Math.Clamp(Pose.Sway + swaySpeed * dt, -1.5, 1.5);

        double head = 1.1 * Math.Sin(life * 0.55) + 0.5 * Math.Sin(life * 1.37), nod = 0.08 * Math.Sin(life * 0.41);
        if (Pose.Thinking) (head, nod) = (head - 3.5, nod - 0.7);        // mira hacia arriba, como buscando la respuesta
        else if (Pose.Listening) (head, nod) = (head + 4.5, nod - 0.1);  // ladea la cabeza hacia quien le habla
        if (Pose.MouthOpen > 0.05) nod += 0.16 * Math.Sin(life * 6.1);   // al hablar acompaña con la cabeza
        double ease = Math.Min(1, dt * 7);
        Pose.Head += (head - Pose.Head) * ease;
        Pose.Nod += (nod - Pose.Nod) * ease;
    }

    // ---------- gestos ----------

    public void Blink() => Run("blink", 0.18, (p, _) => Pose.EyeOpen = Math.Abs(1 - 2 * p), () => Pose.EyeOpen = 1);

    public void Glance()
    {
        double dir = Random.Shared.Next(2) == 0 ? -1 : 1;
        Run("gaze", 1.6, (p, _) => Pose.Gaze = dir * Math.Sin(Math.PI * p), () => Pose.Gaze = 0);
    }

    public void Jump(double height = 110)
    {
        if (dragging) return;
        Stop("fall");
        double peak = height * ScaleF, from = lift;
        Run("jump", 0.62, (p, _) =>
        {
            lift = from * (1 - p) + 4 * peak * p * (1 - p);
            Pose.Squash = -0.13 * Math.Sin(Math.PI * p);
        }, Land);
    }

    public void Hop() => Jump(26);

    public void Dance()
    {
        if (dragging) return;
        Run("dance", 3.2, (_, t) =>
        {
            Pose.Tilt = 9 * Math.Sin(t * 7.5);
            Pose.Wave = 0.55 + 0.45 * Math.Sin(t * 7.5);
            Pose.LegPhase = t * 7.5;
            lift = Math.Abs(Math.Sin(t * 7.5)) * 12 * ScaleF;
        }, () =>
        {
            Pose.Tilt = Pose.Wave = Pose.LegPhase = 0;
            lift = 0;
        });
    }

    public void WaveHand() => Run("wave", 1.5, (p, t) =>
    {
        double envelope = Math.Min(1, p * 6) * Math.Min(1, (1 - p) * 6);
        Pose.Wave = envelope * (0.8 + 0.2 * Math.Sin(t * 16));
    }, () => Pose.Wave = 0);

    public void WalkTo(Point target, Action? arrived = null)
    {
        if (dragging) return;
        screen = Screen.FromPoint(target);
        double half = PetRenderer.W * ScaleF / 2;
        var area = screen.WorkingArea;
        double to = Math.Clamp(target.X, area.Left + half, Math.Max(area.Left + half, area.Right - half));
        double from = centerX, distance = to - from;
        if (Math.Abs(distance) < 4)
        {
            arrived?.Invoke();
            return;
        }
        Run("walk", Math.Clamp(Math.Abs(distance) / (170 * ScaleF), 0.3, 6), (p, t) =>
        {
            centerX = from + distance * p;
            Pose.LegPhase = t * 11;
            Pose.Gaze = Math.Sign(distance);
            Pose.Tilt = 4 * Math.Sign(distance) * Math.Min(1, 8 * Math.Min(p, 1 - p));
        }, () =>
        {
            Pose.LegPhase = Pose.Gaze = 0;
            Pose.Tilt = 0;
            arrived?.Invoke();
        });
    }

    public void SetTalking(bool on)
    {
        if (on) Run("talk", double.PositiveInfinity, (_, t) => Pose.MouthOpen = 0.2 + 0.8 * Math.Abs(Math.Sin(t * 9.5)));
        else
        {
            Stop("talk");
            Pose.MouthOpen = 0;
            Redraw();
        }
    }

    public void SetThinking(bool on)
    {
        Pose.Thinking = on;
        if (on) Run("think", double.PositiveInfinity, (_, t) => Pose.ThinkPhase = t / 1.1 % 1);
        else
        {
            Stop("think");
            Redraw();
        }
    }

    /// <summary>Ondas verdes junto a la cabeza mientras escucha sin exigir su nombre.</summary>
    public void SetListening(bool on)
    {
        if (Pose.Listening == on) return;
        Pose.Listening = on;
        Redraw();
    }

    /// <summary>Dormida no queda ninguna animación viva: el reloj se para solo.</summary>
    public void SetAsleep(bool asleep)
    {
        Pose.Asleep = asleep;
        Pose.EyeOpen = asleep ? 0 : 1;
        if (asleep)
        {
            clock.Stop();
            anims.Clear();
            Pose.Tilt = Pose.Wave = Pose.LegPhase = Pose.Gaze = Pose.MouthOpen = Pose.Squash = 0;
            Pose.Head = Pose.Nod = Pose.Breath = Pose.Sway = swaySpeed = 0;
            Pose.Thinking = Pose.Listening = false;
            lift = 0;
        }
        Redraw();
        if (Alive) StartClock();
    }

    public void FadeIn()
    {
        opacity = 0;
        if (!Visible) Show();
        swayFrom = double.NaN;
        Run("fade", 0.25, (p, _) => opacity = (byte)(255 * p), () => opacity = 255);
    }

    public void FadeOut() => Run("fade", 0.25, (p, _) => opacity = (byte)(255 * (1 - p)), () =>
    {
        Hide();
        opacity = 255;
    });

    /// <summary>Tras cambiar de tamaño: que no se salga de la pantalla.</summary>
    public void Refit()
    {
        ClampToScreen();
        Redraw();
        if (Alive) StartClock();
    }

    private void Land()
    {
        lift = 0;
        Run("squash", 0.22, (p, _) => Pose.Squash = 0.16 * (1 - p), () => Pose.Squash = 0);
    }

    private void Fall()
    {
        double from = lift;
        if (from < 2)
        {
            lift = 0;
            Redraw();
            return;
        }
        Run("fall", 0.18 + Math.Sqrt(from) / 55, (p, _) => lift = from * (1 - p * p), Land);
    }

    private void ClampToScreen()
    {
        double half = PetRenderer.W * ScaleF / 2;
        var area = screen.WorkingArea;
        centerX = Math.Clamp(centerX, area.Left + half, Math.Max(area.Left + half, area.Right - half));
    }

    // ---------- ratón y atajo ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks > 1)   // segundo clic de un doble clic: lo trata OnMouseDoubleClick
        {
            pressed = false;
            return;
        }
        pressed = true;
        dragging = false;
        pressAt = Cursor.Position;
        grab = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pressed) return;
        var cursor = Cursor.Position;
        if (!dragging && (Math.Abs(cursor.X - pressAt.X) > 5 || Math.Abs(cursor.Y - pressAt.Y) > 5))
        {
            dragging = true;
            Stop("walk");
            Stop("jump");
            Stop("fall");
            Stop("dance");
            Pose.Tilt = Pose.LegPhase = Pose.Squash = 0;
        }
        if (!dragging) return;

        screen = Screen.FromPoint(cursor);
        centerX = cursor.X - grab.X + Math.Ceiling(PetRenderer.W * ScaleF) / 2;
        lift = Math.Max(0, screen.WorkingArea.Bottom - PetRenderer.Floor * ScaleF - (cursor.Y - grab.Y));
        Redraw();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            MenuRequested?.Invoke();
            return;
        }
        if (e.Button != MouseButtons.Left || !pressed) return;
        pressed = false;
        if (dragging)
        {
            dragging = false;
            ClampToScreen();
            Fall();
        }
        else Clicked?.Invoke();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) DoubleClicked?.Invoke();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Si otro programa ya usa el atajo elegido, prueba con los de reserva.
        foreach (string candidate in new[] { wantedHotkey, "Ctrl+Alt+F9", "Ctrl+Alt+F10" }.Distinct())
        {
            if (!TryParseHotkey(candidate, out uint modifiers, out uint key)) continue;
            if (!Native.RegisterHotKey(Handle, HotkeyId, modifiers | Native.MOD_NOREPEAT, key)) continue;
            ActiveHotkey = candidate;
            break;
        }
        if (ActiveHotkey != wantedHotkey)
            Log.Write($"El atajo {wantedHotkey} no está disponible; uso {(ActiveHotkey.Length > 0 ? ActiveHotkey : "ninguno")}.");
    }

    /// <summary>"Ctrl+Alt+C", "Ctrl+Shift+Espacio", "Win+F9"…</summary>
    private static bool TryParseHotkey(string text, out uint modifiers, out uint key)
    {
        modifiers = key = 0;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift" or "mayus" or "mayús": modifiers |= Native.MOD_SHIFT; break;
                case "win": modifiers |= Native.MOD_WIN; break;
                case "espacio" or "space": key = Native.VK_SPACE; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out Keys parsed)) return false;
                    key = (uint)parsed;
                    break;
            }
        }
        return modifiers != 0 && key != 0;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) clock.Stop();
        else if (Alive || anims.Count > 0) StartClock();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) clock.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Redraw();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }
}
