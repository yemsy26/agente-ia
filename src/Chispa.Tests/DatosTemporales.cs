namespace Chispa.Tests;

/// <summary>
/// Pruebas que tocan el disco: apuntan CHISPA_DATOS a una carpeta temporal propia para no tocar
/// nunca los datos reales de la asistente, y la restauran al terminar.
/// </summary>
public abstract class DatosTemporales : IDisposable
{
    protected readonly string Dir;
    private readonly string? Previo;

    protected DatosTemporales()
    {
        Previo = Environment.GetEnvironmentVariable("CHISPA_DATOS");
        Dir = Path.Combine(Path.GetTempPath(), "chispa-pruebas-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CHISPA_DATOS", Dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CHISPA_DATOS", Previo);
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* en uso: el sistema la limpia */ }
    }
}
