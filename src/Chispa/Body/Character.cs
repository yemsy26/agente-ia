using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Chispa;

/// <summary>
/// El personaje ilustrado: un dibujo de cuerpo entero, recortado, que cobra vida deformándolo un poco en cada
/// fotograma (respira, ladea la cabeza, se le mece el pelo), con párpados y bocas pintados aparte y un antebrazo
/// que se dobla para saludar. Sin motor 3D ni tarjeta gráfica: unas decenas de miles de píxeles por fotograma.
/// Las piezas viajan dentro del ejecutable; una carpeta «avatar» junto a él las sustituye (las genera src\Avatar).
/// </summary>
internal sealed class Character
{


    private const double Tall = 222;   // alto de la figura en el lienzo lógico de PetRenderer
    private const int Step = 4;        // malla de deformación, en píxeles de pantalla

    private static readonly Lazy<Character?> shared = new(Load);
    private static readonly string[] requiredPieces =
        ["cuerpo.png", "sin_brazo.png", "brazo.png", "pelo.png", "ojos_1.png", "ojos_2.png", "ojos_3.png", "boca_1.png", "boca_2.png", "boca_3.png"];

    /// <summary>null si faltan las piezas o están rotas; entonces se dibuja la forma de siempre.</summary>
    public static Character? Shared => shared.Value;

    private sealed record Layer(int X, int Y, int W, int H, int[] Pixels);

    /// <summary>Las piezas reducidas al tamaño al que se van a ver (se rehacen solo si cambia el tamaño).</summary>
    private sealed class Sized
    {
        public required double K;
        public required Layer Body, BodyNoArm, Arm;
        public required Layer[] Eyes, Mouths;
        public required int[] Work;   // el cuerpo con el gesto de la cara ya puesto
        public (bool NoArm, int Eye, int Mouth) Shown = (false, -1, -1);
    }

    /// <summary>Medidas del dibujo, en sus propios píxeles (figura.json).</summary>
    private sealed class Rig
    {
        public double Top, Floor, CenterX, NeckX, NeckY, Chin, Shoulders, FaceX, FaceY, FaceRx, FaceRy, Waist,
            SkirtTop, SkirtHem, SkirtEnd, SkirtFull, SkirtNone, Hip, LegSplit, HairScale,
            ArmX, ArmY, ElbowX, ElbowY, ElbowR0, ElbowR1, ArmRaise, ArmReach, EyesX, EyesY, LipsX, LipsY;
        public double[][] Ears = [];
    }

    private readonly Func<string, byte[]?> piece;
    private readonly Rig rig;
    private readonly byte[] hair;
    private readonly int hairW, hairH;
    private Sized? sized;
    private int[] canvas = [];
    private double[] us = [], vs = [];

    private static Character? Load() => Load(Path.Combine(AppContext.BaseDirectory, "avatar"));

    /// <summary>Un avatar externo defectuoso nunca impide usar el personaje integrado.</summary>
    internal static Character? Load(string folder)
    {
        if (Directory.Exists(folder))
        {
            try
            {
                byte[]? Read(string name) => File.Exists(Path.Combine(folder, name)) ? File.ReadAllBytes(Path.Combine(folder, name)) : null;
                var json = Read("figura.json") ?? throw new FileNotFoundException("falta figura.json");
                return new Character(Read, json);
            }
            catch (Exception ex)
            {
                Log.Write($"Avatar externo inválido ({folder}): {ex.Message}. Uso el personaje integrado.");
            }
        }

        try
        {
            return Embedded("figura.json") is { } json ? new Character(Embedded, json) : null;
        }
        catch (Exception ex)
        {
            Log.Write($"No pude cargar el personaje: {ex.Message}");
            return null;
        }
    }

    private static byte[]? Embedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Avatar/" + name);
        if (stream == null) return null;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private Character(Func<string, byte[]?> piece, byte[] json)
    {
        // Lee y comprueba TODAS las piezas antes de usarlo en la ventana o en el icono.
        // Se conserva una copia: cambiar la carpeta mientras corre no rompe el siguiente cambio de tamaño.
        var pieces = requiredPieces.ToDictionary(name => name,
            name => piece(name) ?? throw new FileNotFoundException($"falta la pieza {name}"));
        this.piece = name => pieces.GetValueOrDefault(name);
        var dimensions = new Dictionary<string, Size>();
        foreach (string name in requiredPieces)
            dimensions[name] = With(name, bmp => bmp.Width is > 0 and <= 4096 && bmp.Height is > 0 and <= 4096
                ? bmp.Size : throw new InvalidDataException($"tamaño de {name} fuera de límite"));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        double Finite(double value) => double.IsFinite(value) ? value : throw new InvalidDataException("medida no finita en figura.json");
        double N(JsonElement e, string name) => Finite(e.GetProperty(name).GetDouble());
        double[] A(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(v => Finite(v.GetDouble())).ToArray();
        double[] neck = A(r, "cuello"), face = A(r, "cara"), skirt = A(r, "falda");
        var arm = r.GetProperty("brazo");
        var parts = r.GetProperty("partes");
        double[] elbow = A(arm, "codo");
        rig = new Rig
        {
            Top = N(r, "coronilla"), Floor = N(r, "suelo"), CenterX = N(r, "centro"),
            NeckX = neck[0], NeckY = neck[1], Chin = N(r, "menton"), Shoulders = N(r, "hombros"),
            FaceX = face[0], FaceY = face[1], FaceRx = face[2], FaceRy = face[3],
            Waist = N(r, "cintura"), SkirtTop = skirt[0], SkirtHem = skirt[1], SkirtEnd = skirt[2], SkirtFull = skirt[3], SkirtNone = skirt[4],
            Hip = N(r, "cadera"), LegSplit = N(r, "piernas"), HairScale = N(r.GetProperty("pelo"), "escala"),
            ArmX = N(arm, "x"), ArmY = N(arm, "y"), ElbowX = elbow[0], ElbowY = elbow[1], ElbowR0 = N(arm, "r0"), ElbowR1 = N(arm, "r1"), ArmRaise = N(arm, "sube"),
            EyesX = N(parts.GetProperty("ojos_1"), "x"), EyesY = N(parts.GetProperty("ojos_1"), "y"),
            LipsX = N(parts.GetProperty("boca_1"), "x"), LipsY = N(parts.GetProperty("boca_1"), "y"),
            Ears = r.GetProperty("orejas").EnumerateArray().Select(e => e.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray(),
        };

        var expected = new Size(r.GetProperty("ancho").GetInt32(), r.GetProperty("alto").GetInt32());
        if (dimensions["cuerpo.png"] != expected || dimensions["sin_brazo.png"] != expected
            || rig.Floor - rig.Top < expected.Height / 4.0 || rig.Top < 0 || rig.Floor > expected.Height || rig.Top >= rig.Chin
            || rig.FaceRx <= 0 || rig.FaceRy <= 0 || rig.HairScale <= 0
            || rig.Waist <= rig.Shoulders - 20 || rig.Shoulders <= rig.Chin
            || rig.SkirtTop >= rig.SkirtHem || rig.SkirtHem >= rig.SkirtEnd || rig.SkirtFull >= rig.SkirtNone
            || rig.Hip >= rig.Floor || rig.ElbowR0 >= rig.ElbowR1
            || rig.Ears.Length == 0 || rig.Ears.Any(e => e.Length != 2 || e.Any(v => !double.IsFinite(v))))
            throw new InvalidDataException("figura.json no coincide con las piezas o contiene medidas inválidas");
        foreach (string name in requiredPieces.Where(n => n.StartsWith("ojos_") || n.StartsWith("boca_")))
        {
            var part = parts.GetProperty(Path.GetFileNameWithoutExtension(name));
            double x = N(part, "x"), y = N(part, "y");
            if (x < 0 || y < 0 || x + dimensions[name].Width > expected.Width || y + dimensions[name].Height > expected.Height)
                throw new InvalidDataException($"la pieza {name} sale del cuerpo");
        }

        (hair, hairW, hairH) = With("pelo.png", map =>
        {
            var pixels = Pixels(map, out int w, out int h);
            return (pixels.Select(p => (byte)p).ToArray(), w, h);   // gris: vale cualquier canal
        });
        // Hasta dónde llega el antebrazo desde el codo: lo que hay que repintar cuando saluda.
        rig.ArmReach = With("brazo.png", layer =>
        {
            double far = 0;
            foreach (var (dx, dy) in new[] { (0, 0), (layer.Width, 0), (0, layer.Height), (layer.Width, layer.Height) })
                far = Math.Max(far, Math.Sqrt(Math.Pow(rig.ArmX + dx - rig.ElbowX, 2) + Math.Pow(rig.ArmY + dy - rig.ElbowY, 2)));
            return far;
        });
    }

    private T With<T>(string name, Func<Bitmap, T> use)
    {
        using var stream = new MemoryStream(piece(name) ?? throw new FileNotFoundException($"falta la pieza {name}"));
        using var bmp = new Bitmap(stream);
        return use(bmp);
    }

    private static int[] Pixels(Bitmap source, out int w, out int h)
    {
        (w, h) = (source.Width, source.Height);
        var data = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new int[w * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally { source.UnlockBits(data); }
    }

    /// <summary>Reduce una pieza al tamaño de pantalla, con su posición dentro del dibujo, sin perder nitidez en el borde.</summary>
    private static Layer Shrink(Bitmap source, double x, double y, double k)
    {
        double left = x * k, top = y * k;
        int ox = (int)Math.Floor(left), oy = (int)Math.Floor(top);
        int w = (int)Math.Ceiling(left + source.Width * k) - ox + 1, h = (int)Math.Ceiling(top + source.Height * k) - oy + 1;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var edges = new ImageAttributes())
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            edges.SetWrapMode(WrapMode.TileFlipXY);
            float fx = (float)(left - ox), fy = (float)(top - oy), fw = (float)(source.Width * k), fh = (float)(source.Height * k);
            g.DrawImage(source, [new PointF(fx, fy), new PointF(fx + fw, fy), new PointF(fx, fy + fh)],
                new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel, edges);
        }
        return new Layer(ox, oy, w, h, Pixels(bmp, out _, out _));
    }

    private Sized For(double k)
    {
        if (sized != null && Math.Abs(sized.K - k) < 1e-9) return sized;
        Layer Piece(string name, double x = 0, double y = 0) => With(name, bmp => Shrink(bmp, x, y, k));
        var body = Piece("cuerpo.png");
        return sized = new Sized
        {
            K = k,
            Body = body,
            BodyNoArm = Piece("sin_brazo.png"),
            Arm = Piece("brazo.png", rig.ArmX, rig.ArmY),
            Eyes = [.. Enumerable.Range(1, 3).Select(i => Piece($"ojos_{i}.png", rig.EyesX, rig.EyesY))],
            Mouths = [.. Enumerable.Range(1, 3).Select(i => Piece($"boca_{i}.png", rig.LipsX, rig.LipsY))],
            Work = new int[body.Pixels.Length],
        };
    }

    /// <summary>Retrato para el icono de la bandeja: la cara.</summary>
    public Icon Portrait() => With("cuerpo.png", body =>
    {
        using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int side = (int)(rig.Chin + 10 - rig.Top + 2);
            g.DrawImage(body, new Rectangle(0, 0, 64, 64), new Rectangle((int)(rig.FaceX - side / 2.0), (int)rig.Top - 2, side, side), GraphicsUnit.Pixel);
        }
        IntPtr handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { Native.DestroyIcon(handle); }
    });

    public Bitmap Render(Pose p, double scale)
    {
        int cw = Math.Max(8, (int)Math.Ceiling(PetRenderer.W * scale)), ch = Math.Max(8, (int)Math.Ceiling(PetRenderer.H * scale));
        double k = scale * Tall / (rig.Floor - rig.Top);   // píxeles de pantalla por píxel del dibujo
        var s = For(k);

        // --- la postura, en números ---
        bool waving = p.Wave > 0.01;
        int eye = p.Asleep || p.EyeOpen < 0.2 ? 3 : p.EyeOpen < 0.5 ? 2 : p.EyeOpen < 0.8 ? 1 : 0;
        int mouth = p.MouthOpen <= 0.08 ? 0 : p.MouthOpen < 0.4 ? 1 : p.MouthOpen < 0.75 ? 2 : 3;
        Compose(s, waving, eye, mouth);

        double feetX = PetRenderer.W / 2.0 * scale, feetY = PetRenderer.Floor * scale;
        double tilt = p.Tilt * 0.55 * Math.PI / 180, cos = Math.Cos(tilt), sin = Math.Sin(tilt);
        double sx = k * (1 + 0.21 * p.Squash), sy = k * (1 - 0.35 * p.Squash);
        double roll = (p.Head + (p.Asleep ? 7 : 0)) * Math.PI / 180, yaw = p.Gaze, pitch = p.Asleep ? 0.9 : p.Nod;
        double breath = 3.2 * (p.Asleep ? 0.3 : p.Breath);
        double hairSway = 7 * p.Sway, skirtSway = 4 * p.Sway + 0.8 * Math.Sin(p.Clock * 1.5), clock = p.Clock;
        bool stepping = p.LegPhase != 0;
        double liftL = stepping ? 26 * Math.Max(0, Math.Sin(p.LegPhase)) : 0, liftR = stepping ? 26 * Math.Max(0, Math.Sin(p.LegPhase + Math.PI)) : 0;
        var m = rig;

        // De un punto del lienzo al punto del dibujo que le toca, y al revés.
        (double X, double Y) ToDrawing(double cx, double cy)
        {
            double dx = cx - feetX, dy = cy - feetY;
            return (m.CenterX + (dx * cos + dy * sin) / sx, m.Floor + (-dx * sin + dy * cos) / sy);
        }
        (double X, double Y) ToCanvas(double x, double y)
        {
            double rx = (x - m.CenterX) * sx, ry = (y - m.Floor) * sy;
            return (feetX + rx * cos - ry * sin, feetY + rx * sin + ry * cos);
        }

        // Cada píxel de pantalla pregunta de qué punto del dibujo sale: ahí se deshace, al revés, cada movimiento.
        (double, double) Body(double cx, double cy)
        {
            var (x, y) = ToDrawing(cx, cy);
            if (stepping && y > m.Hip)   // la pierna que da el paso se encoge hacia la cadera
            {
                double lift = liftL + (liftR - liftL) * Smooth(m.LegSplit - 12, m.LegSplit + 12, x);
                y = m.Hip + (y - m.Hip) / (1 - lift / (m.Floor - m.Hip));
            }
            if (y > m.SkirtTop && y < m.SkirtEnd)   // el vuelo de la falda
            {
                double wy = y < m.SkirtHem ? Smooth(m.SkirtTop, m.SkirtHem, y) : 1 - Smooth(m.SkirtHem, m.SkirtEnd, y);
                x -= skirtSway * wy * (1 - Smooth(m.SkirtFull, m.SkirtNone, Math.Abs(x - m.CenterX)));
            }
            double loose = HairAt(x, y);   // el pelo largo: se mece entero y además ondea
            if (loose > 0)
                x -= loose * (hairSway + 1.6 * Math.Sin(clock * 1.7 - y * 0.013) + 0.9 * Math.Sin(clock * 2.9 + 1 - y * 0.021));
            if (y < m.Waist) y += breath * (1 - Smooth(m.Shoulders - 20, m.Waist, y));   // al respirar suben hombros y cabeza
            if (y < m.Shoulders)
            {
                double head = 1 - Smooth(m.Chin, m.Shoulders, y);
                if (roll != 0)
                {
                    double a = roll * head, ca = Math.Cos(a), sa = Math.Sin(a), dx = x - m.NeckX, dy = y - m.NeckY;
                    (x, y) = (m.NeckX + dx * ca + dy * sa, m.NeckY - dx * sa + dy * ca);
                }
                double fx = (x - m.FaceX) / m.FaceRx, fy = (y - m.FaceY) / m.FaceRy, d = fx * fx + fy * fy;
                double face = d < 1 ? (1 - d) * (1 - d) : 0;   // la cara se desplaza más que el contorno: parece que gira
                x -= yaw * (9 * face + 3 * head);
                y -= pitch * (6 * face + 3 * head);
            }
            return (x, y);
        }

        double bend = p.Wave * m.ArmRaise * Math.PI / 180;
        (double, double) Arm(double cx, double cy)   // el antebrazo: gira alrededor del codo, doblando la tela cerca de él
        {
            var (x, y) = ToDrawing(cx, cy);
            double dx = x - m.ElbowX, dy = y - m.ElbowY;
            double a = bend * Smooth(m.ElbowR0, m.ElbowR1, Math.Sqrt(dx * dx + dy * dy)), ca = Math.Cos(a), sa = Math.Sin(a);
            return (m.ElbowX + dx * ca - dy * sa, m.ElbowY + dx * sa + dy * ca);
        }

        // --- píxeles ---
        if (canvas.Length != cw * ch) canvas = new int[cw * ch];
        else Array.Clear(canvas);
        if (!p.Airborne) Shadow(canvas, cw, ch, feetX, feetY - 1.5 * scale, 40 * scale, 5.5 * scale);

        var whole = new Rectangle(0, 0, cw, ch);
        Paint(whole, cw, s.Work, s.Body, k, Body);
        if (waving)
        {
            var (ex, ey) = ToCanvas(m.ElbowX, m.ElbowY);
            double reach = (m.ArmReach + 6) * Math.Max(sx, sy);
            var around = Rectangle.Intersect(whole, Rectangle.FromLTRB((int)(ex - reach), (int)(ey - reach), (int)(ex + reach) + 1, (int)(ey + reach) + 1));
            if (around.Width > 0 && around.Height > 0) Paint(around, cw, s.Arm.Pixels, s.Arm, k, Arm);
        }

        var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(whole, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        Marshal.Copy(canvas, 0, data.Scan0, canvas.Length);
        bmp.UnlockBits(data);

        // --- señales de estado, encima ---
        if (p.Asleep || p.Thinking || p.Listening)
        {
            // Un punto de la cabeza, ya inclinada, en unidades del lienzo lógico.
            PointF Head(double x, double y)
            {
                double dx = x - m.NeckX, dy = y - m.NeckY, ca = Math.Cos(roll), sa = Math.Sin(roll);
                var (cx, cy) = ToCanvas(m.NeckX + dx * ca - dy * sa, m.NeckY + dx * sa + dy * ca - breath);
                return new PointF((float)(cx / scale), (float)(cy / scale));
            }
            using var g = Graphics.FromImage(bmp);
            PetRenderer.Prepare(g);
            g.ScaleTransform((float)scale, (float)scale);
            PointF crown = Head(m.FaceX, m.Top), ear = Head(m.Ears[^1][0], m.Ears[^1][1]);

            if (p.Asleep)
            {
                PetRenderer.DrawGlyph(g, "z", ear.X + 5, Math.Max(1, crown.Y + 4), 11, 2.2f);
                PetRenderer.DrawGlyph(g, "Z", ear.X + 14, Math.Max(1, crown.Y - 12), 15, 2.2f);
            }
            else if (p.Thinking)
            {
                using var dotPen = new Pen(Color.FromArgb(50, 60, 110), 1.5f);
                for (int i = 0; i < 3; i++)
                {
                    double pulse = Math.Max(0, Math.Sin(2 * Math.PI * (p.ThinkPhase - i * 0.16)));
                    float r = (float)(2.6 + 1.7 * pulse), dx = ear.X + 10 + i * 9.5f, dy = ear.Y - 7 - i * 7;
                    g.FillEllipse(Brushes.White, dx - r, dy - r, r * 2, r * 2);
                    g.DrawEllipse(dotPen, dx - r, dy - r, r * 2, r * 2);
                }
            }
            else if (p.Listening)
            {
                // Los auriculares se encienden y salen ondas verdes: «te estoy escuchando, no hace falta mi nombre».
                foreach (var e in m.Ears)
                {
                    var at = Head(e[0], e[1]);
                    using var path = new GraphicsPath();
                    path.AddEllipse(at.X - 5.5f, at.Y - 7.5f, 11, 15);
                    using var glow = new PathGradientBrush(path) { CenterColor = Color.FromArgb(215, 120, 255, 190), SurroundColors = [Color.FromArgb(0, 60, 230, 140)] };
                    g.FillPath(glow, path);
                }
                using var halo = new Pen(Color.FromArgb(230, 255, 255, 255), 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var wave = new Pen(Color.FromArgb(35, 185, 105), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                foreach (var pen in new[] { halo, wave })
                    foreach (float r in new[] { 8f, 12.5f, 17f })
                        g.DrawArc(pen, ear.X - r, ear.Y - r, r * 2, r * 2, -38, 76);
            }
        }
        return bmp;
    }

    private static double Smooth(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private double HairAt(double x, double y)
    {
        int hx = (int)(x / rig.HairScale), hy = (int)(y / rig.HairScale);
        return hx < 0 || hy < 0 || hx >= hairW || hy >= hairH ? 0 : hair[hy * hairW + hx] / 255.0;
    }

    /// <summary>El cuerpo (con o sin antebrazo) más el gesto de la cara; solo se rehace cuando el gesto cambia.</summary>
    private static void Compose(Sized s, bool noArm, int eye, int mouth)
    {
        if (s.Shown == (noArm, eye, mouth)) return;
        Array.Copy((noArm ? s.BodyNoArm : s.Body).Pixels, s.Work, s.Work.Length);
        foreach (var patch in new[] { eye > 0 ? s.Eyes[eye - 1] : null, mouth > 0 ? s.Mouths[mouth - 1] : null })
        {
            if (patch == null) continue;
            for (int y = Math.Max(0, -patch.Y); y < patch.H && patch.Y + y < s.Body.H; y++)
                for (int x = Math.Max(0, -patch.X); x < patch.W && patch.X + x < s.Body.W; x++)
                {
                    uint src = (uint)patch.Pixels[y * patch.W + x];
                    if (src >> 24 == 0) continue;
                    int at = (patch.Y + y) * s.Body.W + patch.X + x;
                    s.Work[at] = (int)Over(src, (uint)s.Work[at]);
                }
        }
        s.Shown = (noArm, eye, mouth);
    }

    /// <summary>Un color encima de otro; los dos con el alfa ya multiplicado.</summary>
    private static uint Over(uint src, uint dst)
    {
        uint a = src >> 24;
        if (a == 255 || dst == 0) return src;
        uint inv = 255 - a;
        uint rb = (dst & 0xFF00FF) * inv + 0x800080, ag = ((dst >> 8) & 0xFF00FF) * inv + 0x800080;
        rb = ((rb + ((rb >> 8) & 0xFF00FF)) >> 8) & 0xFF00FF;
        ag = ((ag + ((ag >> 8) & 0xFF00FF)) >> 8) & 0xFF00FF;
        return src + (ag << 8 | rb);
    }

    /// <summary>Sombra suave en el suelo.</summary>
    private static void Shadow(int[] canvas, int cw, int ch, double cx, double cy, double rx, double ry)
    {
        for (int y = Math.Max(0, (int)(cy - ry)); y <= Math.Min(ch - 1, (int)(cy + ry) + 1); y++)
            for (int x = Math.Max(0, (int)(cx - rx)); x <= Math.Min(cw - 1, (int)(cx + rx) + 1); x++)
            {
                double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry, d = dx * dx + dy * dy;
                if (d < 1) canvas[y * cw + x] = (int)(70 * (1 - d) * (2 - (1 - d))) << 24;
            }
    }

    /// <summary>
    /// Pinta una capa deformada: <paramref name="source"/> dice, para un punto del lienzo, de qué punto del dibujo sale.
    /// Se pregunta solo en los nudos de una malla; entre nudo y nudo se reparte en línea recta.
    /// </summary>
    private void Paint(Rectangle area, int cw, int[] pixels, Layer layer, double k, Func<double, double, (double, double)> source)
    {
        int nx = (area.Width + Step - 1) / Step + 1, ny = (area.Height + Step - 1) / Step + 1;
        if (us.Length < nx * ny) (us, vs) = (new double[nx * ny], new double[nx * ny]);
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var (x, y) = source(area.X + i * Step + 0.5, area.Y + j * Step + 0.5);
                us[j * nx + i] = x * k - layer.X - 0.5;
                vs[j * nx + i] = y * k - layer.Y - 0.5;
            }

        int lw = layer.W, lh = layer.H;
        const double inv = 1.0 / Step;
        for (int j = 0; j < ny - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                int n = j * nx + i;
                double u00 = us[n], u10 = us[n + 1], u01 = us[n + nx], u11 = us[n + nx + 1];
                double v00 = vs[n], v10 = vs[n + 1], v01 = vs[n + nx], v11 = vs[n + nx + 1];
                if ((u00 < -1 && u10 < -1 && u01 < -1 && u11 < -1) || (u00 > lw && u10 > lw && u01 > lw && u11 > lw)
                    || (v00 < -1 && v10 < -1 && v01 < -1 && v11 < -1) || (v00 > lh && v10 > lh && v01 > lh && v11 > lh)) continue;

                int x0 = area.X + i * Step, y0 = area.Y + j * Step;
                int xEnd = Math.Min(area.Right, x0 + Step), yEnd = Math.Min(area.Bottom, y0 + Step);
                for (int y = y0; y < yEnd; y++)
                {
                    double fy = (y - y0) * inv;
                    double u = u00 + (u01 - u00) * fy, v = v00 + (v01 - v00) * fy;
                    double du = (u10 + (u11 - u10) * fy - u) * inv, dv = (v10 + (v11 - v10) * fy - v) * inv;
                    int row = y * cw;
                    for (int x = x0; x < xEnd; x++, u += du, v += dv)
                    {
                        int iu = (int)Math.Floor(u), iv = (int)Math.Floor(v);
                        if (iu < -1 || iv < -1 || iu >= lw || iv >= lh) continue;
                        uint p00, p10, p01, p11;
                        if (iu >= 0 && iv >= 0 && iu < lw - 1 && iv < lh - 1)
                        {
                            int at = iv * lw + iu;
                            p00 = (uint)pixels[at];
                            p10 = (uint)pixels[at + 1];
                            p01 = (uint)pixels[at + lw];
                            p11 = (uint)pixels[at + lw + 1];
                        }
                        else
                        {
                            bool left = iu >= 0, right = iu < lw - 1, up = iv >= 0, down = iv < lh - 1;
                            p00 = left && up ? (uint)pixels[iv * lw + iu] : 0;
                            p10 = right && up ? (uint)pixels[iv * lw + iu + 1] : 0;
                            p01 = left && down ? (uint)pixels[(iv + 1) * lw + iu] : 0;
                            p11 = right && down ? (uint)pixels[(iv + 1) * lw + iu + 1] : 0;
                        }
                        if ((p00 | p10 | p01 | p11) == 0) continue;

                        // Mezcla de los cuatro vecinos, de dos en dos canales.
                        uint wx = (uint)((u - iu) * 256), wy = (uint)((v - iv) * 256);
                        uint rbT = (((p00 & 0xFF00FF) * (256 - wx) + (p10 & 0xFF00FF) * wx) >> 8) & 0xFF00FF;
                        uint agT = ((((p00 >> 8) & 0xFF00FF) * (256 - wx) + ((p10 >> 8) & 0xFF00FF) * wx) >> 8) & 0xFF00FF;
                        uint rbB = (((p01 & 0xFF00FF) * (256 - wx) + (p11 & 0xFF00FF) * wx) >> 8) & 0xFF00FF;
                        uint agB = ((((p01 >> 8) & 0xFF00FF) * (256 - wx) + ((p11 >> 8) & 0xFF00FF) * wx) >> 8) & 0xFF00FF;
                        uint rb = ((rbT * (256 - wy) + rbB * wy) >> 8) & 0xFF00FF;
                        uint ag = ((agT * (256 - wy) + agB * wy) >> 8) & 0xFF00FF;
                        if (ag >> 16 == 0) continue;
                        canvas[row + x] = (int)Over(ag << 8 | rb, (uint)canvas[row + x]);
                    }
                }
            }
    }
}
