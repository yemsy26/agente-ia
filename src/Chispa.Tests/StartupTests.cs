using System.Diagnostics;
using System.Text;

namespace Chispa.Tests;

/// <summary>
/// Pruebas que arrancan el programa de verdad, como lo haría un usuario: el diagnóstico completo y un
/// arranque normal que se queda vivo. Cada una usa una carpeta temporal (CHISPA_DATOS) para no tocar
/// los datos reales ni chocar con una Chispa abierta.
/// </summary>
public sealed class StartupTests
{
    private static readonly string BaseDir = AppContext.BaseDirectory;

    /// <summary>
    /// El diagnóstico completo sin voz sale «TODO BIEN». Si el motor lógico (razon.exe) no viaja junto al
    /// programa compilado —como pasa en la carpeta de salida de las pruebas—, solo se tolera esa falta.
    /// </summary>
    [Xunit.Fact]
    public async Task ElDiagnosticoCompletoSinVozSaleTodoBien()
    {
        string datos = Directory.CreateTempSubdirectory("chispa-prueba-diag-").FullName;
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = BaseDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.Latin1,
                StandardErrorEncoding = Encoding.Latin1,
            };
            psi.ArgumentList.Add(Path.Combine(BaseDir, "Chispa.dll"));
            psi.ArgumentList.Add("--diagnostico");
            psi.ArgumentList.Add("--sin-voz");
            psi.Environment["CHISPA_DATOS"] = datos;

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("No arrancó el diagnóstico.");
            Task<string> salidaTarea = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTarea = process.StandardError.ReadToEndAsync();

            bool termino;
            try
            {
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);
                termino = true;
            }
            catch (OperationCanceledException)
            {
                termino = false;
            }
            if (!termino)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("El diagnóstico no terminó en cinco minutos.");
            }
            string salida = await salidaTarea;
            string errores = await errorTarea;

            var fallas = salida.Split('\n').Where(l => l.StartsWith("  FALLA")).Select(l => l.Trim()).ToList();
            bool hayMotor = File.Exists(Path.Combine(BaseDir, "razon", "razon.exe"));

            if (hayMotor)
            {
                Assert.True(process.ExitCode == 0, $"El diagnóstico salió con {process.ExitCode}.\n{salida}\n{errores}");
                Assert.Contains("TODO BIEN", salida);
            }
            else
            {
                // Sin motor lógico al lado, lo único que puede fallar es esa comprobación: una FALLA de «motor l…».
                Assert.True(process.ExitCode == 1, $"Sin motor lógico se esperaba salir con 1, salió con {process.ExitCode}.\n{salida}\n{errores}");
                Assert.Single(fallas);
                Assert.Contains("motor l", fallas[0]);
            }
        }
        finally
        {
            try { Directory.Delete(datos, recursive: true); } catch (Exception) { /* se limpia en la próxima pasada */ }
        }
    }

    /// <summary>
    /// Arranque normal: se queda viva el tiempo de prueba, escribe su registro de arranque y contesta el
    /// guion sin micrófono, voz ni modelo grande (configuración mínima para no molestar al equipo).
    /// </summary>
    [Xunit.Fact]
    public async Task ArrancaYSeQuedaViva()
    {
        string datos = Directory.CreateTempSubdirectory("chispa-prueba-soak-").FullName;
        try
        {
            // version:5 evita que la puesta al día cambie «propio» por «auto» (que llamaría a un modelo).
            File.WriteAllText(Path.Combine(datos, "config.json"),
                """{"version":5,"cerebro":"propio","silencio":true,"escucha_continua":false,"animacion_reposo":false}""");

            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = BaseDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.Latin1,
                StandardErrorEncoding = Encoding.Latin1,
            };
            psi.ArgumentList.Add(Path.Combine(BaseDir, "Chispa.dll"));
            psi.Environment["CHISPA_DATOS"] = datos;
            psi.Environment["CHISPA_GUION"] = "qué hora es;cuéntame un chiste;gracias";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("No arrancó la mascota.");
            Task<string> salidaTarea = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTarea = process.StandardError.ReadToEndAsync();

            // Vive y contesta: el guion suena a los 3, 6 y 9 segundos; esperamos bastante más que eso.
            bool viva;
            try
            {
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(40)).Token);
                viva = false;   // se cerró sola antes de tiempo
            }
            catch (OperationCanceledException)
            {
                viva = true;    // seguía viva cuando venció la espera: justo lo que se comprueba
            }
            if (!viva)
            {
                string salida = await salidaTarea + await errorTarea;
                Assert.Fail($"La mascota se cerró sola al arrancar (código {process.ExitCode}).\n{salida}");
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

            string registro = Path.Combine(datos, "chispa.log");
            Assert.True(File.Exists(registro), $"No escribió chispa.log en {datos}.");
            string texto = await File.ReadAllTextAsync(registro);
            Assert.Contains("Voces:", texto);   // la línea de arranque prueba que llegó viva hasta la mascota
        }
        finally
        {
            try { Directory.Delete(datos, recursive: true); } catch (Exception) { /* se limpia en la próxima pasada */ }
        }
    }
}
