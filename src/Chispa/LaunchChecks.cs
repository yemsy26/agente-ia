using System.Text.Json;

namespace Chispa;

/// <summary>Comprueba mensajes de ejecución sin abrir aplicaciones ni usar un modelo.</summary>
internal static class LaunchChecks
{
    public static async Task Run(Action<string, bool, string> check)
    {
        var body = new Diagnostics.FakeBody(TextWriter.Null) { Quiet = true };
        var cfg = new Config { Cerebro = "propio", SinConfirmar = ["abrir"] };
        var mind = new Mind(cfg, body, new Memory());
        var request = JsonSerializer.SerializeToElement(new { destino = "navegador" });

        body.LaunchOutcome = new(true, "No apareció una ventana correspondiente.");
        string unverified = await ToolBox.Run("abrir", request, mind);
        check("aceptar el despacho no se anuncia como una ventana abierta",
            unverified.Contains("no pude verificar su ventana", StringComparison.OrdinalIgnoreCase)
            && !unverified.StartsWith("Abrí ", StringComparison.OrdinalIgnoreCase)
            && !unverified.Contains("está al frente", StringComparison.OrdinalIgnoreCase), unverified);

        body.LaunchOutcome = new(true, "Ventana visible.", true, false);
        string visible = await ToolBox.Run("abrir", request, mind);
        check("una ventana visible sin foco comunica que Windows no permitió presentarla al frente",
            visible.Contains("está abierta", StringComparison.OrdinalIgnoreCase)
            && visible.Contains("no permitió", StringComparison.OrdinalIgnoreCase)
            && !visible.StartsWith("Abrí ", StringComparison.OrdinalIgnoreCase), visible);

        body.LaunchOutcome = new(true, "Ventana verificada al frente.", true, true);
        string verified = await ToolBox.Run("abrir", request, mind);
        check("la apertura verificada informa que la ventana está al frente",
            verified.StartsWith("Abrí ", StringComparison.OrdinalIgnoreCase)
            && verified.Contains("está al frente", StringComparison.OrdinalIgnoreCase), verified);

        body.LaunchOutcome = new(false, "Error de prueba al ejecutar.");
        string failed = await ToolBox.Run("abrir", request, mind);
        check("un error de ejecución conserva el motivo y no afirma apertura",
            failed.StartsWith("No pude abrir ", StringComparison.OrdinalIgnoreCase)
            && failed.Contains("Error de prueba", StringComparison.Ordinal), failed);
        check("los cuatro resultados probaron la herramienta sin abrir aplicaciones reales",
            body.Did.Count(d => d.StartsWith("abre: ", StringComparison.Ordinal)) == 4, string.Join(" | ", body.Did));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool interrupted = false;
        try { await Device.LaunchAsync("https://example.invalid/chispa-prueba-cancelada", canceled.Token); }
        catch (OperationCanceledException) { interrupted = true; }
        check("una apertura cancelada de antemano se detiene antes del despacho", interrupted, "");

        check("verificar una carpeta tolera mayúsculas y barra final",
            Device.SameFolder(@"C:\Trabajo\Proyecto\", @"c:\trabajo\proyecto"), "");
        check("verificar una carpeta normaliza segmentos relativos",
            Device.SameFolder(@"C:\Trabajo\Proyecto\..\Proyecto", @"C:\Trabajo\Proyecto"), "");
        check("una carpeta con nombre parecido no verifica el destino pedido",
            !Device.SameFolder(@"C:\Trabajo\Proyecto", @"C:\Trabajo\Proyecto anterior"), "");
        check("una subcarpeta no verifica la carpeta pedida",
            !Device.SameFolder(@"C:\Trabajo\Proyecto", @"C:\Trabajo\Proyecto\Documentos"), "");
    }
}
