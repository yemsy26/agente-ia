namespace Chispa;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Diagnostics.Run(args);

        // Una sola Chispa por carpeta de datos (las pruebas usan otra carpeta y no chocan con la de verdad).
        string folder = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(Config.DataDir.ToLowerInvariant())));
        using var single = new Mutex(true, $"Chispa.Mascota.{folder}", out bool first);
        if (!first)
        {
            // Salir en silencio despistaba: ya hay una Chispa abierta, junto al reloj.
            MessageBox.Show("Chispa ya está abierta: búscala junto al reloj, en la bandeja del sistema.", "Chispa", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { TraceLog.Failure("interfaz", e.Exception); Log.Write($"Error no controlado: {e.Exception}"); };
        // Lo que falla fuera del hilo de la interfaz (audio, escritura del diario, tareas huérfanas) no debe
        // matar el proceso en silencio: queda en el registro antes de caer.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { TraceLog.Failure("fondo", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString())); }
            catch (Exception) { /* si ni el registro se puede escribir, no hay nada que hacer */ }
            Log.Write($"Error no controlado en segundo plano: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { TraceLog.Failure("tarea_huerfana", e.Exception.GetBaseException()); } catch (Exception) { }
            Log.Write($"Excepción en una tarea sin observar: {e.Exception}");
            e.SetObserved();
        };

        bool firstRun = !Config.Exists;
        var cfg = Config.Load();
        if (firstRun) cfg.Save();
        try { Application.Run(new PetApp(cfg, firstRun)); }
        finally { TraceLog.FlushOnExit(); }
        return 0;
    }
}
