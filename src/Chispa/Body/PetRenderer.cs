using System.Drawing.Drawing2D;
using System.Drawing.Text;
namespace Chispa;
internal sealed class Pose
{
    public double EyeOpen = 1;   // 0 cerrado … 1 abierto
    public double MouthOpen;     // 0 sonrisa … 1 boca abierta
    public double Squash;        // + aplastada, − estirada
    public double Wave;          // 0 brazo abajo … 1 brazo arriba
    public double LegPhase;      // ciclo de pasos
    public double Gaze;          // −1 mira a la izquierda … 1 a la derecha
    public double Tilt;          // inclinación en grados
    public double Head, Nod;     // solo el personaje: cabeza ladeada (grados) y cabeceo (−1 arriba … 1 abajo)
    public double Breath;        // solo el personaje: 0 … 1, aire en el pecho
    public double Sway;          // solo el personaje: −1 … 1, hacia dónde se le van el pelo y la falda
    public double Clock;         // solo el personaje: segundos que lleva despierta (ondas del pelo)
    public double ThinkPhase;    // 0 … 1, puntitos de "pensando"
    public bool Asleep, Thinking, Airborne, Listening;
}


/// <summary>Único avatar actual: ilustración articulada con recursos integrados.</summary>
internal static class PetRenderer
{
    public const int W = 200, H = 250, Floor = 236;
    public const double Zoom = 1.8;
    private static Character Avatar => Character.Shared ?? throw new InvalidOperationException("Los recursos del avatar están dañados. Recompila Chispa.");
    public static Bitmap Render(Pose pose, double scale) => Avatar.Render(pose, scale);
    public static Icon RenderIcon() => Avatar.Portrait();
    internal static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
    }
    internal static void DrawGlyph(Graphics g, string text, float x, float y, float size, float outline = 4f)
    {
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        using var pen = new Pen(Color.FromArgb(50, 60, 110), outline) { LineJoin = LineJoin.Round };
        path.AddString(text, family, (int)FontStyle.Bold, size, new PointF(x, y), StringFormat.GenericDefault);
        g.DrawPath(pen, path);
        g.FillPath(Brushes.White, path);
    }

    public static GraphicsPath RoundRect(float x, float y, float w, float h, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(w, h));
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

}
