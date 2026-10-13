using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Chispa;

/// <summary>
/// Ventana sin marco con transparencia por píxel: siempre encima, nunca roba el foco
/// y los clics atraviesan todo lo que sea transparente.
/// </summary>
internal class LayeredForm : Form
{
    public LayeredForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)Native.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected void Present(Bitmap bmp, int x, int y, byte opacity = 255) => Native.PresentLayered(Handle, bmp, x, y, opacity);
}

/// <summary>Globo de texto sobre la cabeza de la mascota. Los clics lo atraviesan.</summary>
internal sealed class BubbleForm : LayeredForm
{
    private readonly System.Windows.Forms.Timer hideTimer = new();
    private string text = "";
    private Point anchor;
    private bool stale;   // el texto cambió y hay que volver a pintar aunque no se haya movido

    public BubbleForm() => hideTimer.Tick += (_, _) => HideNow();

    /// <summary>Pruebas de interfaz: el temporizador que oculta el globo y el texto que tiene puesto.</summary>
    internal System.Windows.Forms.Timer HideTimer => hideTimer;
    internal string BubbleText => text;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <param name="anchor">Punto de la pantalla al que apunta el pico del globo.</param>
    /// <param name="hideAfterMs">0 = queda visible hasta que se oculte o cambie.</param>
    public void ShowText(string text, Point anchor, int hideAfterMs = 0)
    {
        this.text = text.Length > 420 ? text[..417] + "…" : text;
        stale = true;
        hideTimer.Stop();
        if (!Visible) Show();
        Follow(anchor);
        if (hideAfterMs > 0) HideAfter(hideAfterMs);
    }

    public void HideAfter(int ms)
    {
        hideTimer.Stop();
        hideTimer.Interval = Math.Max(100, ms);
        hideTimer.Start();
    }

    public void HideNow()
    {
        hideTimer.Stop();
        if (Visible) Hide();
    }

    public void Follow(Point anchor)
    {
        if (!Visible || text.Length == 0 || (!stale && anchor == this.anchor)) return;
        this.anchor = anchor;
        stale = false;

        float s = DeviceDpi / 96f;
        var area = Screen.FromPoint(anchor).WorkingArea;
        using var bmp = Render(text, s, tipX: w => anchor.X - Math.Clamp(anchor.X - w / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - w - 4)));
        int x = Math.Clamp(anchor.X - bmp.Width / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - bmp.Width - 4));
        Present(bmp, x, Math.Max(area.Top + 4, anchor.Y - bmp.Height));
    }

    /// <param name="s">Escala de pantalla (1 = 96 ppp).</param>
    /// <param name="tipX">Dado el ancho del globo, a qué distancia de su borde izquierdo va el pico.</param>
    internal static Bitmap Render(string text, float s, Func<int, float> tipX)
    {
        float pad = 12 * s, tail = 11 * s, radius = 14 * s, border = 2 * s;
        using var font = new Font("Segoe UI", 15 * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = 0 };

        SizeF measured;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            measured = pg.MeasureString(text, font, (int)(290 * s), format);
        }

        int w = (int)Math.Ceiling(measured.Width + pad * 2 + border * 2);
        int h = (int)Math.Ceiling(measured.Height + pad * 2 + border * 2 + tail);
        float tip = Math.Clamp(tipX(w), radius + tail, Math.Max(radius + tail, w - radius - tail));

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float boxH = h - tail - border;
        using var box = PetRenderer.RoundRect(border / 2, border / 2, w - border, boxH, radius);
        using var paper = new SolidBrush(Color.FromArgb(252, 252, 255));
        using var pen = new Pen(Color.FromArgb(55, 55, 70), border) { LineJoin = LineJoin.Round };
        using var inkBrush = new SolidBrush(Color.FromArgb(30, 30, 45));
        PointF[] beak = [new(tip - tail * 0.8f, boxH), new(tip, h - border), new(tip + tail * 0.8f, boxH)];

        g.FillPath(paper, box);
        g.DrawPath(pen, box);
        g.FillPolygon(paper, beak);
        g.DrawLine(pen, beak[0], beak[1]);
        g.DrawLine(pen, beak[1], beak[2]);
        g.DrawString(text, font, inkBrush, new RectangleF(pad + border, pad + border / 2, measured.Width + 1, measured.Height + 1), format);
        return bmp;
    }
}

/// <summary>Cajita para escribirle a la mascota cuando no quieres (o no puedes) hablar.</summary>
internal sealed class ChatForm : Form
{
    private readonly TextBox box = new();

    public event Action<string>? Submitted;

    public ChatForm(string name)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(55, 55, 70);
        Padding = new Padding(2);

        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(8, 7, 8, 5) };
        box.BorderStyle = BorderStyle.None;
        box.Font = new Font("Segoe UI", 11f);
        box.Dock = DockStyle.Fill;
        box.PlaceholderText = $"Escríbele a {name}…  (Enter envía, Esc cierra)";
        box.KeyDown += OnKeyDown;
        inner.Controls.Add(box);
        Controls.Add(inner);
        ClientSize = new Size(400, box.PreferredHeight + 18);
        Deactivate += (_, _) => Hide();
    }

    public void Open(Point anchor)
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        Location = new Point(
            Math.Clamp(anchor.X - Width / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - Width - 4)),
            Math.Clamp(anchor.Y - Height - 6, area.Top + 4, area.Bottom - Height - 4));
        Show();
        Activate();
        box.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            Hide();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string typed = box.Text.Trim();
            box.Clear();
            Hide();
            if (typed.Length > 0) Submitted?.Invoke(typed);
        }
    }

    /// <summary>Pruebas de interfaz: la caja de texto y el camino de teclado, sin ventanas de verdad.</summary>
    internal TextBox Box => box;
    internal void SimulateKeyDown(KeyEventArgs e) => OnKeyDown(this, e);
}
