using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Chispa;

/// <summary>Una ventana abierta: cómo se llama y de qué programa es.</summary>
internal readonly record struct WindowRef(IntPtr Handle, string Title, string Process);
internal readonly record struct LaunchResult(bool Started, string Detail, bool WindowVisible = false, bool Foreground = false);

/// <summary>
/// Lo que toca el equipo de verdad: abrir programas y carpetas, mirar qué ventanas hay, cerrarlas y
/// fotografiar la pantalla. Nada de aquí decide si algo se puede hacer; eso lo resuelve quien lo llama.
/// </summary>
internal static class Device
{
    private static List<(string Name, string Key, string Path)>? programs;

    /// <summary>
    /// Extensiones que nunca se abren por su ruta: el shell las ejecuta con su asociación (guiones, instaladores,
    /// accesos directos de usuario…). La usa ToolBox.ResolveTarget y la defensa de LaunchAsync.
    /// </summary>
    internal static readonly string[] NeverByExtension =
    [
        ".exe", ".bat", ".cmd", ".com", ".ps1", ".ps2", ".psm1", ".vbs", ".vbe", ".vb", ".js", ".jse", ".msi",
        ".scr", ".lnk", ".reg", ".hta", ".wsf", ".ws", ".shb", ".scf", ".inf", ".pif", ".cpl", ".jar",
        ".py", ".msc", ".url", ".chm", ".appref-ms",
    ];

    /// <summary>Los programas del menú Inicio: su nombre y el acceso directo que los abre.</summary>
    public static IReadOnlyList<(string Name, string Key, string Path)> Programs()
    {
        if (programs != null) return programs;
        var found = new Dictionary<string, (string Name, string Key, string Path)>();
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        ];
        foreach (string root in roots.Where(r => r.Length > 0 && Directory.Exists(r)))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    string key = Reflexes.Normalize(name);
                    // Los desinstaladores y las ayudas no son lo que nadie quiere abrir al nombrar un programa.
                    if (key.Length < 2 || key.Contains("uninstall") || key.Contains("desinstal") || key.StartsWith("ayuda de") || key.Contains("readme")) continue;
                    found.TryAdd(key, (name, key, path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* carpeta que no se deja leer */ }
        }
        return programs = found.Values.ToList();
    }

    /// <summary>Las carpetas que todo el mundo llama por su nombre.</summary>
    public static (string Path, string Label)? KnownFolder(string wanted)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string[] Names, string Path, string Label)[] folders =
        [
            (["descargas", "downloads"], Path.Combine(profile, "Downloads"), "la carpeta Descargas"),
            (["escritorio", "desktop"], Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "la carpeta Escritorio"),
            (["documentos", "mis documentos", "documents"], Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "la carpeta Documentos"),
            (["imagenes", "fotos", "mis imagenes", "pictures"], Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "la carpeta Imágenes"),
            (["musica", "mi musica", "music"], Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "la carpeta Música"),
            (["videos", "mis videos"], Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "la carpeta Vídeos"),
            (["carpeta personal", "mi carpeta", "carpeta de usuario", "mis archivos", "archivos"], profile, "tu carpeta personal"),
        ];
        foreach (var (names, path, label) in folders)
            if (path.Length > 0 && names.Any(n => wanted == n || wanted.EndsWith(" " + n) || wanted.StartsWith(n + " ")))
                return (path, label);
        return null;
    }

    /// <summary>
    /// Una carpeta del usuario por su nombre («facturas», «proyecto chispa»): se busca en el escritorio, documentos,
    /// descargas y su carpeta personal, hasta dos niveles. <paramref name="inside"/> limita la búsqueda a una de ellas.
    /// </summary>
    public static (string Path, string Label)? FindFolder(string wanted, string? inside = null)
    {
        if (wanted.Length < 2) return null;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots = inside != null ? [inside] :
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            profile,
        ];
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        string[] words = wanted.Split(' ');
        (string Path, int Score)? best = null;
        int seen = 0;
        foreach (string root in roots.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct())
        {
            IEnumerable<string> level = Directory.EnumerateDirectories(root, "*", options);
            for (int depth = 0; depth < 2; depth++)
            {
                var next = new List<string>();
                foreach (string path in level)
                {
                    if (++seen > 6000) break;
                    string key = Reflexes.Normalize(Path.GetFileName(path));
                    // Igual que lo dicho vale más que contenerlo; y cuanto más cerca de la raíz, antes.
                    int score = key == wanted ? 100 : words.All(w => $" {key} ".Contains($" {w} ")) ? 60 - key.Length / 4 : 0;
                    if (score > 0 && (best == null || score - depth * 5 > best.Value.Score)) best = (path, score - depth * 5);
                    if (depth == 0 && !Path.GetFileName(path).StartsWith('.')) next.Add(path);
                }
                if (depth == 0) level = next.SelectMany(p => { try { return Directory.EnumerateDirectories(p, "*", options).ToList(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<string>(); } });
            }
        }
        return best is { } found ? (found.Path, $"la carpeta {Path.GetFileName(found.Path)}") : null;
    }

    /// <summary>Abre y verifica una ventana del destino. Aceptación del shell, visibilidad y foco son resultados distintos.</summary>
    public static async Task<LaunchResult> LaunchAsync(string command, CancellationToken token = default)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            bool web = Uri.TryCreate(command, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            // Defensa en profundidad: ResolveTarget ya validó el destino, pero ningún otro llamador debe poder
            // ejecutar guiones ni binarios sueltos. Solo pasan direcciones web, esquemas ms-*/microsoft.*,
            // carpetas, accesos directos (.lnk del menú Inicio) y ejecutables por nombre simple.
            bool protocol = !web && Uri.TryCreate(command, UriKind.Absolute, out var uri2)
                && (uri2.Scheme.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)
                    || uri2.Scheme.StartsWith("microsoft.", StringComparison.OrdinalIgnoreCase));
            bool folder = Directory.Exists(command);
            if (!web && !protocol && !folder && !Path.GetExtension(command).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                if (command.Contains('\\') || command.Contains('/'))
                {
                    if (NeverByExtension.Contains(Path.GetExtension(command).ToLowerInvariant()))
                        return new(false, "Por seguridad no abro ese tipo de archivo por su ruta.");
                }
                else if (!Path.GetExtension(command).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    return new(false, "Solo abro programas por su nombre.");
            }
            var before = OpenWindows().ToDictionary(w => w.Handle, w => w.Title);
            string[] expected = await Task.Run(() => ExpectedProcesses(command, web, folder), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            int pid = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var process = Process.Start(new ProcessStartInfo(command) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
                // URLs y carpetas pueden reutilizar un proceso sin devolver Process.
                return process?.Id ?? 0;
            }, token).ConfigureAwait(false);

            Log.Write($"[apertura] despachada tipo={(folder ? "carpeta" : web ? "web" : "programa")} pid={pid} ms={elapsed.ElapsedMilliseconds}");
            IntPtr candidate = IntPtr.Zero;
            bool presented = false;
            Task<IntPtr>? folderLookup = null;
            // Sondeo acotado, sin bloquear la interfaz ni cargar un modelo para comprobar Windows.
            while (elapsed.Elapsed < TimeSpan.FromSeconds(7))
            {
                token.ThrowIfCancellationRequested();
                if (folder)
                {
                    // Explorer puede estar bloqueado: no esperar indefinidamente una llamada COM ni crear un hilo por sondeo.
                    folderLookup ??= Task.Run(() => FolderWindow(command), token);
                    if (folderLookup.IsCompleted)
                    {
                        candidate = await folderLookup.ConfigureAwait(false);
                        folderLookup = null;
                    }
                }
                else candidate = MatchingWindow(OpenWindows(), before, expected, pid, web);
                if (candidate != IntPtr.Zero)
                {
                    if (!presented) { Native.PresentWindow(candidate); presented = true; }
                    await Task.Delay(100, token).ConfigureAwait(false);
                    bool visible = Native.WindowIsVisible(candidate);
                    // La restauración es asíncrona: pedir el foco otra vez cuando ya dejó de estar minimizada.
                    if (visible && Native.GetForegroundWindow() != candidate)
                    {
                        Native.SetForegroundWindow(candidate);
                        await Task.Delay(80, token).ConfigureAwait(false);
                    }
                    bool foreground = Native.GetForegroundWindow() == candidate;
                    if (visible && foreground)
                    {
                        Log.Write($"[apertura] verificada visible=true frente=true ms={elapsed.ElapsedMilliseconds}");
                        return new(true, "Ventana abierta y presentada al frente.", true, true);
                    }
                    // Windows puede denegar el foco. Dejar tiempo a la restauración, sin insistir durante siete segundos.
                    if (visible && elapsed.Elapsed > TimeSpan.FromMilliseconds(900))
                    {
                        Log.Write($"[apertura] verificada visible=true frente=false ms={elapsed.ElapsedMilliseconds}");
                        return new(true, "La ventana está visible; Windows no confirmó el foco al frente.", true, false);
                    }
                }
                await Task.Delay(140, token).ConfigureAwait(false);
            }
            bool shown = candidate != IntPtr.Zero && Native.WindowIsVisible(candidate);
            Log.Write($"[apertura] sin_confirmar visible={shown} ms={elapsed.ElapsedMilliseconds}");
            return new(true, shown ? "La ventana está visible; no confirmé que quedara al frente."
                : "Windows aceptó la orden, pero no apareció una ventana del destino en siete segundos.", shown, false);
        }
        catch (OperationCanceledException)
        {
            Log.Write($"[apertura] cancelada ms={elapsed.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Write($"[apertura] error ms={elapsed.ElapsedMilliseconds} tipo={ex.GetType().Name}: {ex.Message}");
            return new(false, ex.Message);
        }
    }

    private static IntPtr MatchingWindow(List<WindowRef> windows, Dictionary<IntPtr, string> before, string[] expected, int pid, bool web)
    {
        if (pid != 0)
        {
            var own = windows.FirstOrDefault(w => Native.ProcessId(w.Handle) == pid);
            if (own.Handle != IntPtr.Zero) return own.Handle;
        }
        var matching = windows.Where(w => expected.Contains(w.Process, StringComparer.OrdinalIgnoreCase)).ToList();
        // Una ventana nueva o cuyo título cambió es la mejor evidencia cuando la app reutiliza su proceso.
        var changed = matching.FirstOrDefault(w => !before.TryGetValue(w.Handle, out string? title) || title != w.Title);
        if (changed.Handle != IntPtr.Zero) return changed.Handle;
        if (matching.Count > 0) return matching[0].Handle;
        // Si no se obtuvo la asociación, se permite únicamente una ventana de navegador que apareció o cambió.
        if (web && expected.Length == 0)
        {
            string[] browsers = ["brave", "chrome", "msedge", "firefox", "opera", "vivaldi", "iexplore"];
            return windows.FirstOrDefault(w => browsers.Contains(w.Process, StringComparer.OrdinalIgnoreCase)
                && (!before.TryGetValue(w.Handle, out string? title) || title != w.Title)).Handle;
        }
        return IntPtr.Zero;
    }

    private static string[] ExpectedProcesses(string command, bool web, bool folder)
    {
        if (folder) return ["explorer"];
        string target = command;
        if (web)
        {
            try
            {
                string scheme = new Uri(command).Scheme;
                using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + scheme + @"\UserChoice");
                string? id = choice?.GetValue("ProgId") as string;
                using var association = id == null ? null : Registry.ClassesRoot.OpenSubKey(id + @"\shell\open\command");
                string? launch = association?.GetValue(null) as string;
                if (launch == null) return [];
                var executable = Regex.Match(launch, "^(?:\"(?<exe>[^\"]+)\"|(?<exe>\\S+))");
                target = executable.Groups["exe"].Value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return []; }
        }
        else if (Path.GetExtension(command).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null, shortcut = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type != null)
                {
                    shell = Activator.CreateInstance(type);
                    shortcut = ((dynamic)shell!).CreateShortcut(command);
                    string path = ((dynamic)shortcut).TargetPath;
                    // Un acceso UWP puede iniciar Explorer con shell:AppsFolder: una carpeta ya abierta no prueba esa app.
                    if (Path.GetFileNameWithoutExtension(path).Equals("explorer", StringComparison.OrdinalIgnoreCase)
                        && ((string)((dynamic)shortcut).Arguments).Length > 0) return [];
                    if (path.Length > 0) target = path;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally { ReleaseCom(shortcut); ReleaseCom(shell); }
        }
        string name = Path.GetFileNameWithoutExtension(target);
        return name.ToLowerInvariant() switch
        {
            "calc" or "calculator" => ["CalculatorApp", "Calculator", "calc"],
            "notepad" => ["Notepad"],
            "ms-windows-store:calculator" => ["CalculatorApp", "Calculator"],
            _ => name.Length > 0 ? [name] : [],
        };
    }

    private static IntPtr FolderWindow(string path)
    {
        object? shell = null, windows = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return IntPtr.Zero;
            shell = Activator.CreateInstance(type);
            windows = ((dynamic)shell!).Windows();
            int count = ((dynamic)windows).Count;
            for (int i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = ((dynamic)windows).Item(i);
                    string location = ((dynamic)window!).LocationURL;
                    if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile && SameFolder(uri.LocalPath, path))
                        return new IntPtr((long)((dynamic)window).HWND);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { ReleaseCom(window); }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { ReleaseCom(windows); ReleaseCom(shell); }
        return IntPtr.Zero;
    }

    internal static bool SameFolder(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    /// <summary>Las ventanas abiertas con título, de delante hacia atrás, sin contar las de la propia mascota.</summary>
    public static List<WindowRef> OpenWindows()
    {
        using var me = Process.GetCurrentProcess();
        var windows = new List<WindowRef>();
        foreach (IntPtr handle in Native.TopWindows())
        {
            var info = Native.WindowInfo(handle);
            if (info.Title.Length > 0 && info.Process != me.ProcessName) windows.Add(new(handle, info.Title, info.Process));
        }
        return windows;
    }

    /// <summary>
    /// Una foto (JPEG) de lo que el usuario tiene delante: su ventana activa o, si no hay, la pantalla principal.
    /// Se queda en memoria; nunca se guarda en disco.
    /// </summary>
    public static byte[]? Screenshot(int maxSide = 1600)
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            var active = Native.WindowInfo();
            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Rectangle area = active.Process != me.ProcessName && Native.Bounds(active.Handle) is { Width: > 200, Height: > 150 } window
                ? Rectangle.Intersect(window, SystemInformation.VirtualScreen)
                : screen;
            if (area.Width <= 0 || area.Height <= 0) area = screen;

            using var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return Jpeg(shot, maxSide);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        {
            Log.Write($"No pude fotografiar la pantalla: {ex.Message}");
            return null;
        }
    }

    /// <summary>Codifica una imagen en JPEG, encogiéndola si pasa del tamaño máximo.</summary>
    public static byte[] Jpeg(Bitmap image, int maxSide = 1600)
    {
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(image.Width, image.Height));
        using var sized = scale < 1 ? new Bitmap((int)(image.Width * scale), (int)(image.Height * scale), PixelFormat.Format24bppRgb) : null;
        if (sized != null)
        {
            using var g = Graphics.FromImage(sized);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, sized.Width, sized.Height);
        }
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        (sized ?? image).Save(stream, codec, quality);
        return stream.ToArray();
    }
}
