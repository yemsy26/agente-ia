using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Chispa;

/// <summary>Informe local para ubicar esperas y fallos sin exponer conversaciones ni credenciales.</summary>
internal static class SupportDiagnostics
{
    public static async Task<string> WriteReport(Config cfg, CancellationToken ct = default)
    {
        TraceLog.Configure(cfg);
        using var flushTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        flushTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await TraceLog.FlushAsync(flushTimeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        // Leer y agrupar el registro puede tocar 2 MiB; se hace fuera del hilo de la interfaz.
        return await Task.Run(() => BuildReport(cfg, ct), ct);
    }

    private static string BuildReport(Config cfg, CancellationToken ct)
    {
        var result = new StringBuilder();
        result.AppendLine("CHISPA · DIAGNÓSTICO LOCAL");
        result.AppendLine($"Generado: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        result.AppendLine("Este informe no contiene audio, capturas, claves de acceso ni archivos personales.");
        result.AppendLine(cfg.Depurar ? "Depurar está activo: los eventos pueden conservar fragmentos de texto depurados." : "Texto de las conversaciones: omitido (Depurar desactivado).");
        result.AppendLine();
        result.AppendLine($"Versión: {Assembly.GetExecutingAssembly().GetName().Version}");
        result.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; Windows {Environment.OSVersion.Version}");
        result.AppendLine($"Procesadores lógicos disponibles: {Environment.ProcessorCount}");
        using (var process = Process.GetCurrentProcess())
        {
            result.AppendLine($"Proceso Chispa: {process.Id}; memoria residente {MiB(process.WorkingSet64)} MiB; privada {MiB(process.PrivateMemorySize64)} MiB");
            result.AppendLine($"Memoria administrada actual: {MiB(GC.GetTotalMemory(false))} MiB; uso CPU acumulado {process.TotalProcessorTime.TotalSeconds:0.0} s");
        }
        result.AppendLine($"Cerebro configurado: {Label(cfg.Cerebro)}; modelo Ollama: {Label(cfg.ModeloOllama, "selección automática")}; modo pensar: {Label(cfg.Pensar)}");
        result.AppendLine($"Respuesta rápida: {cfg.RespuestaRapida}; límite {Math.Clamp(cfg.LimiteRespuestaSegundos, 5, 120)} s; máximo {Math.Clamp(cfg.MaximoTokensRespuesta, 64, 2048)} tokens; contexto {Math.Clamp(cfg.ContextoModelo, 2048, 8192)} tokens");
        result.AppendLine($"Voz configurada: {Label(cfg.Voz, "selección automática")}; silencio: {cfg.Silencio}; escucha continua: {cfg.EscuchaContinua}");
        result.AppendLine($"Atención: {cfg.SegundosAtenta} s; conversación: {cfg.SegundosConversacion} s; dormir: {cfg.DormirTrasSegundos} s");
        result.AppendLine($"Umbrales Windows: nombre {cfg.UmbralNombre:0.00}; comando {cfg.UmbralComando:0.00}; dictado {cfg.UmbralDictado:0.00}");
        result.AppendLine($"Runtime del oído cargado: {Label(Transcriber.RuntimeBackend)}; GPU confirmada por motor: {Transcriber.GpuBackendVerified}; evidencias nativas conservadas: {Transcriber.NativeEvidence.Length}");
        result.AppendLine($"Eventos omitidos por cola llena: {TraceLog.DroppedEvents}; errores al escribir: {TraceLog.WriteErrors}");
        result.AppendLine($"Cola de registro: {TraceLog.PendingEvents}/{TraceLog.QueueCapacity}; retención máxima de eventos: {TraceLog.FileCount * TraceLog.FileLimit / 1048576d:0.0} MiB");
        result.AppendLine("El runtime realmente usado se identifica en los eventos backend/provider; la configuración sola no demuestra uso de GPU.");
        result.AppendLine();

        var records = ReadRecords(ct);
        var spans = records.Where(r => String(r, "kind") == "end" && Number(r, "ms") != null).ToList();
        result.AppendLine($"Eventos recientes conservados: {records.Count}; fases terminadas: {spans.Count}");
        result.AppendLine("DURACIÓN POR FASE (milisegundos; solo fases terminadas)");
        foreach (var group in spans.GroupBy(r => String(r, "phase")).OrderByDescending(g => g.Max(r => Number(r, "ms") ?? 0)).Take(24))
        {
            var times = group.Select(r => Number(r, "ms") ?? 0).Order().ToArray();
            double p95 = times[Math.Max(0, (int)Math.Ceiling(times.Length * .95) - 1)];
            result.AppendLine($"  {group.Key}: n={times.Length}; media={times.Average():0.0}; p95={p95:0.0}; máximo={times[^1]:0.0}");
        }
        result.AppendLine();
        result.AppendLine("ERRORES, CANCELACIONES Y LÍMITES RECIENTES");
        var failures = records.Where(r => String(r, "state") is "error" or "timeout" or "cancelado" or "failed" or "fallo").TakeLast(24).ToList();
        if (failures.Count == 0) result.AppendLine("  Sin eventos de fallo registrados en los archivos conservados.");
        foreach (var record in failures) AppendRecord(result, record, cfg.Depurar);
        result.AppendLine();
        result.AppendLine("ÚLTIMAS PETICIONES Y SUS FASES");
        var requests = records.Select(r => String(r, "request")).Where(r => r.Length > 0).Distinct().TakeLast(8).ToList();
        foreach (var request in requests)
        {
            result.AppendLine($"  Petición {request}");
            foreach (var record in records.Where(r => String(r, "request") == request).TakeLast(45)) AppendRecord(result, record, cfg.Depurar);
        }
        result.AppendLine();
        result.AppendLine("Una fase con inicio y sin fin puede seguir en curso o haber sido interrumpida al cerrar el proceso.");
        result.AppendLine("Abrir se considera verificado según el resultado de Windows y la ventana detectada; revise la fase de verificación de la herramienta.");
        result.AppendLine("Para depurar: reproduzca una sola petición, genere este informe y compare la última fase de esa petición.");
        string directory = TraceLog.DirectoryPath;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "informe.txt");
        File.WriteAllText(path, result.ToString(), Encoding.UTF8);
        return path;
    }

    private static List<JsonElement> ReadRecords(CancellationToken ct)
    {
        var records = new List<JsonElement>();
        for (int i = TraceLog.FileCount - 1; i >= 0; i--)
        {
            string path = Path.Combine(TraceLog.DirectoryPath, i == 0 ? "eventos.jsonl" : $"eventos.{i}.jsonl");
            if (!File.Exists(path)) continue;
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                while (reader.ReadLine() is { } line)
                {
                    ct.ThrowIfCancellationRequested();
                    if (line.Length > 32 * 1024) continue;
                    try { using var document = JsonDocument.Parse(line); records.Add(document.RootElement.Clone()); }
                    catch (JsonException) { } // la última línea puede estar escribiéndose
                }
            }
            catch (IOException) { } // una rotación no debe impedir obtener el resto del informe
            catch (UnauthorizedAccessException) { }
        }
        return records.TakeLast(6000).ToList();
    }

    private static void AppendRecord(StringBuilder target, JsonElement record, bool includeDetail)
    {
        target.Append($"    {String(record, "utc")} {String(record, "phase")} · {String(record, "kind")} · {String(record, "state")}");
        if (Number(record, "ms") is { } elapsed) target.Append($" · {elapsed:0.0} ms");
        if (String(record, "error_type") is { Length: > 0 } type) target.Append($" · {type}");
        if (record.TryGetProperty("data", out var data)) target.Append($" · {SafeReportData(data, includeDetail)}");
        if (includeDetail && String(record, "detail") is { Length: > 0 } detail) target.Append($" · {TraceLog.Scrub(detail)}");
        target.AppendLine();
    }
    private static string SafeReportData(JsonElement data, bool debug)
    {
        if (debug) return TraceLog.Scrub(data.GetRawText());
        if (data.ValueKind != JsonValueKind.Object) return "datos omitidos";
        var safe = new Dictionary<string, object?>();
        foreach (var property in data.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number || property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                safe[property.Name] = property.Value.Clone();
            else if (property.Value.ValueKind == JsonValueKind.String && property.Name is "tool" or "provider" or "backend" or "source" or "result" or "state" or "reason" or "runtime" or "mode")
                safe[property.Name] = Label(property.Value.GetString() ?? "");
        }
        return JsonSerializer.Serialize(safe);
    }
    private static string String(JsonElement record, string property) => record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double? Number(JsonElement record, string property) => record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    private static string MiB(long bytes) => (bytes / (1024d * 1024d)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string Label(string value, string fallback = "sin dato") => string.IsNullOrWhiteSpace(value) ? fallback : System.Text.RegularExpressions.Regex.Replace(value, @"[^\p{L}\p{N}_.:-]", "_")[..Math.Min(value.Length, 100)];

    /// <summary>Pruebas con los datos temporales del diagnóstico, nunca con la memoria del usuario.</summary>
    internal static async Task RunChecks(Action<string, bool, string> check)
    {
        Config? previous = TraceLog.Configuration;
        string marker = "texto-personal-de-prueba-" + Guid.NewGuid().ToString("N");
        string key = "sk-ant-credencialdeprueba123456789";
        var cfg = new Config { Cerebro = "propio", Depurar = false };
        TraceLog.Configure(cfg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            string id;
            using (var request = TraceLog.Begin("diagnostico.peticion", marker))
            {
                id = request.Id;
                using (var phase = TraceLog.Measure("diagnostico.fase"))
                {
                    await Task.Delay(1, timeout.Token);
                    check("registro mantiene la petición tras await", TraceLog.CurrentId == id, "");
                    TraceLog.Event("diagnostico.metadatos", marker, new { text = marker, result = marker + " contiene texto", tool = "abrir", duration_ms = 17, verified = false });
                    phase.Mark("timeout");
                }
                TraceLog.Failure("diagnostico.fallo", new TimeoutException(marker));
            }
            await TraceLog.FlushAsync(timeout.Token);
            var records = ReadRecords(timeout.Token).Where(r => String(r, "request") == id).ToList();
            check("registro omite texto personal sin Depurar", records.Count > 0 && !records.Any(r => r.GetRawText().Contains(marker)), "");
            check("registro conserva duración y causa técnica", records.Any(r => String(r, "phase") == "diagnostico.fase" && String(r, "kind") == "end" && Number(r, "ms") > 0) && records.Any(r => String(r, "error_type") == nameof(TimeoutException)), "");
            check("registro separa petición y fases correlacionadas", records.Count(r => String(r, "kind") == "start") == 2 && records.All(r => String(r, "request") == id), "");
            cfg.Depurar = true;
            using (TraceLog.Begin("diagnostico.redaccion", $"Authorization: Bearer {key}; token=otra-credencial"))
                TraceLog.Event("diagnostico.credencial", data: new { api_key = key });
            await TraceLog.FlushAsync(timeout.Token);
            var debugRecords = ReadRecords(timeout.Token).Where(r => String(r, "phase").StartsWith("diagnostico.")).ToList();
            check("registro depurado también oculta credenciales", !debugRecords.Any(r => r.GetRawText().Contains(key) || r.GetRawText().Contains("otra-credencial")), "");
            cfg.Depurar = false;
            string path = await WriteReport(cfg, timeout.Token);
            string report = await File.ReadAllTextAsync(path, timeout.Token);
            check("informe local permite ubicar la fase y respeta privacidad", report.Contains("diagnostico.fase") && report.Contains("TimeoutException") && report.Contains(id) && !report.Contains(marker) && !report.Contains(key), "");
        }
        finally { if (previous != null) TraceLog.Configure(previous); else TraceLog.Configure(new Config()); }
    }
}
