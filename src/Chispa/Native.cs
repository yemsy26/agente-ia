using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Chispa;

/// <summary>Llamadas directas a Windows: ventana transparente, teclado, ventana activa y memoria.</summary>
internal static class Native
{
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000,
                     WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20;
    public const int WM_HOTKEY = 0x0312, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const ushort VK_RETURN = 0x0D, VK_SPACE = 0x20, VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF,
                        VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const int ULW_ALPHA = 2;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private const int GWL_EXSTYLE = -20, WM_CLOSE = 0x0010, DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>Las ventanas que el usuario ve en la barra de tareas, de delante hacia atrás.</summary>
    public static List<IntPtr> TopWindows()
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            // Fuera las invisibles, las de herramientas (como la propia mascota) y las "ocultas" de las apps de la Tienda.
            if (!IsWindowVisible(h) || (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Pide a una ventana que se cierre, igual que al pulsar su aspa: si hay algo sin guardar, el programa pregunta.</summary>
    public static bool RequestClose(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Restaura una ventana solicitada por el usuario y pide a Windows presentarla al frente.
    /// Las llamadas asíncronas no bloquean Chispa si la otra aplicación está ocupada.</summary>
    public static void PresentWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
        ShowWindowAsync(hWnd, IsIconic(hWnd) ? 9 /* SW_RESTORE */ : 5 /* SW_SHOW */);
        // HWND_TOP mueve la ventana en el orden normal: no queda permanentemente encima.
        const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x4000; // NOSIZE, NOMOVE, NOACTIVATE, SHOWWINDOW, ASYNCWINDOWPOS
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, flags);
        SetForegroundWindow(hWnd);
    }

    public static bool WindowIsVisible(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd) && !IsIconic(hWnd)
        && !(DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0);

    public static int ProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return (int)pid;
    }

    /// <summary>Dónde está una ventana en la pantalla, o null si está minimizada o no existe.</summary>
    public static Rectangle? Bounds(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || IsIconic(hWnd) || !GetWindowRect(hWnd, out RECT r)) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Pinta un bitmap con transparencia por píxel como contenido de la ventana y la coloca en (x, y).</summary>
    public static void PresentLayered(IntPtr hwnd, Bitmap bmp, int x, int y, byte opacity)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hBmp);
            var size = new SIZE { CX = bmp.Width, CY = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = x, Y = y };
            var blend = new BLENDFUNCTION { SourceConstantAlpha = opacity, AlphaFormat = 1 };
            UpdateLayeredWindow(hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
            if (hBmp != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hBmp); }
            DeleteDC(mem);
        }
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags };
        return input;
    }

    public static void PressKey(ushort vk, int times = 1)
    {
        INPUT[] inputs = [Key(vk, 0, 0), Key(vk, 0, KEYEVENTF_KEYUP)];
        for (int i = 0; i < times; i++) SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Escribe texto en la ventana que tenga el foco, como si lo tecleara el usuario.</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            ushort c = ch is '\r' or '\n' or '\t' ? ' ' : ch;
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Título y programa de una ventana (por defecto, la que el usuario tiene delante).</summary>
    public static (IntPtr Handle, string Title, string Process) WindowInfo(IntPtr? handle = null)
    {
        var h = handle ?? GetForegroundWindow();
        if (h == IntPtr.Zero) return (h, "", "");
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        string process = "";
        try
        {
            GetWindowThreadProcessId(h, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch (Exception) { /* proceso protegido o ya cerrado */ }
        return (h, sb.ToString(), process);
    }

    /// <summary>Devuelve al sistema toda la memoria que no se esté usando ahora mismo.</summary>
    public static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        EmptyWorkingSet(me.Handle);
    }
}
