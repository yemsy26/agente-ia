using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Chispa;

/// <summary>Registro de fases acotado: no conserva audio, imágenes ni texto salvo Depurar.</summary>
internal static class TraceLog
{
    internal const long FileLimit = 512 * 1024;
    internal const int FileCount = 4;
    internal const int QueueCapacity = 512;
    internal sealed record Context(string Request, string? Span);
    private sealed record Entry(string Directory, string? Json, TaskCompletionSource? Flushed = null);
    private static readonly AsyncLocal<Context?> current = new();
    private static readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private static readonly Task writer = Task.Run(WriteLoop);
    private static readonly string session = Guid.NewGuid().ToString("N")[..12];
    private static Config? config;
    private static long dropped, writeErrors;
    public static string? CurrentId => current.Value?.Request;
    public static long DroppedEvents => Interlocked.Read(ref dropped);
    public static long WriteErrors => Interlocked.Read(ref writeErrors);
    public static int PendingEvents => queue.Reader.CanCount ? queue.Reader.Count : 0;
    internal static Config? Configuration => Volatile.Read(ref config);
    public static string DirectoryPath => Path.Combine(Config.DataDir, "diagnostico");

    public static void Configure(Config cfg) => Volatile.Write(ref config, cfg);

    /// <summary>Una petición nueva. El Id acompaña los awaits y sus fases sin usar un hilo extra.</summary>
    public static Scope Begin(string phase, string? text = null, object? data = null)
    {
        var previous = current.Value;
        var context = new Context(Guid.NewGuid().ToString("N")[..12], null);
        current.Value = context;
        return new Scope(phase, text, data, previous, context, true);
    }

    /// <summary>Una fase de la petición actual, con inicio, fin y duración.</summary>
    public static Scope Measure(string phase, string? detail = null, object? data = null)
    {
        var previous = current.Value;
        var context = new Context(previous?.Request ?? Guid.NewGuid().ToString("N")[..12], Guid.NewGuid().ToString("N")[..8]);
        current.Value = context;
        return new Scope(phase, detail, data, previous, context, false);
    }

    public static void Event(string phase, string? detail = null, object? data = null) =>
        Emit(phase, "event", "ok", null, detail, data, null);
    public static void EventFor(string? request, string phase, string? detail = null, object? data = null)
    {
        var before = current.Value;
        if (request != null) current.Value = new Context(request, null);
        try { Event(phase, detail, data); } finally { current.Value = before; }
    }

    public static void Failure(string phase, Exception error, object? data = null) =>
        Emit(phase, "event", ErrorState(error), null, error.Message, data, error);

    public static async Task FlushAsync(CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new Entry(DirectoryPath, null, completed), ct);
        await completed.Task.WaitAsync(ct);
    }

    // Después del bucle de UI: el cierre conserva las últimas fases sin retener la aplicación.
    public static void FlushOnExit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { FlushAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (Exception) { /* el registro nunca impide cerrar */ }
    }

    public sealed class Scope : IDisposable
    {
        private readonly string phase;
        private readonly Context? previous;
        private readonly Context context;
        private readonly long started = Stopwatch.GetTimestamp();
        private string outcome = "ok";
        private int disposed;
        private readonly bool request;
        internal Scope(string phase, string? text, object? data, Context? previous, Context context, bool request)
        {
            this.phase = phase; this.previous = previous; this.context = context; this.request = request;
            Emit(phase, "start", "running", null, text, data, null);
        }
        public string Id => context.Request;
        public void Mark(string state) => outcome = Label(state);
        public void Fail(Exception error)
        {
            outcome = ErrorState(error);
            Failure(phase, error);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var endContext = current.Value;
            current.Value = context;
            Emit(phase, "end", outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null,
                request ? new { request = true } : null, null);
            // Un callback puede conservar un contexto de otra fase: no lo sustituimos por un turno anterior.
            current.Value = ReferenceEquals(endContext, context) ? previous : endContext;
        }
    }

    private static string ErrorState(Exception error) => error switch
    {
        TimeoutException => "timeout", OperationCanceledException => "cancelado", _ => "error",
    };

    private static void Emit(string phase, string kind, string state, double? milliseconds, string? detail, object? data, Exception? error)
    {
        try
        {
            var context = current.Value;
            bool debug = Volatile.Read(ref config)?.Depurar == true;
            var record = new Dictionary<string, object?>
            {
                ["utc"] = DateTimeOffset.UtcNow, ["session"] = session, ["request"] = context?.Request,
                ["span"] = context?.Span, ["phase"] = Label(phase), ["kind"] = kind, ["state"] = Label(state),
                ["ms"] = milliseconds is { } ms ? Math.Round(ms, 1) : null,
            };
            if (debug && !string.IsNullOrWhiteSpace(detail)) record["detail"] = Scrub(detail);
            if (data != null) record["data"] = SafeData(data, debug);
            if (error != null) { record["error_type"] = error.GetType().Name; record["hresult"] = error.HResult; }
            long omitted = Interlocked.Read(ref dropped);
            if (omitted > 0) record["omitted_total"] = omitted;
            var json = JsonSerializer.Serialize(record);
            if (json.Length > 32 * 1024)
            {
                record["data"] = "datos_truncados";
                json = JsonSerializer.Serialize(record);
            }
            if (!queue.Writer.TryWrite(new Entry(DirectoryPath, json))) Interlocked.Increment(ref dropped);
        }
        catch { Interlocked.Increment(ref dropped); } // el diagnóstico nunca interrumpe una orden
    }

    private static object? SafeData(object data, bool debug)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(data);
            return Clean(node, debug, 0);
        }
        catch { return "datos_no_serializables"; }
    }

    private static JsonNode? Clean(JsonNode? node, bool debug, int depth)
    {
        if (node == null || depth > 3) return null;
        if (node is JsonObject fields)
        {
            var safe = new JsonObject();
            foreach (var field in fields.Take(24))
            {
                string key = Label(field.Key);
                if (Regex.IsMatch(key, "key|token|password|secret|authorization|cookie|credential", RegexOptions.IgnoreCase))
                    safe[key] = "[privado]";
                else if (field.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    bool label = key is "tool" or "provider" or "backend" or "source" or "result" or "state" or "reason" or "runtime" or "mode";
                    safe[key] = label && Regex.IsMatch(text, @"^[\p{L}\p{N}_.:-]{1,80}$") ? text : debug ? Scrub(text) : "[texto omitido]";
                }
                else safe[key] = Clean(field.Value, debug, depth + 1);
            }
            return safe;
        }
        if (node is JsonArray array) return new JsonArray(array.Take(16).Select(n => Clean(n, debug, depth + 1)).ToArray());
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var textValue))
            return JsonValue.Create(debug ? Scrub(textValue) : "[texto omitido]");
        return node.DeepClone();
    }

    private static string Label(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "sin_dato";
        // Etiquetas técnicas, nunca rutas, URL, texto de una orden ni títulos de ventanas.
        string label = Regex.Replace(value, @"[^\p{L}\p{N}_.:-]", "_");
        return label.Length > 80 ? label[..80] : label;
    }

    internal static string Scrub(string value)
    {
        string text = Regex.Replace(value, @"(?i)\b(?:bearer\s+|sk-(?:ant-)?)[a-z0-9_\-.]{6,}", "[credencial omitida]");
        text = Regex.Replace(text, @"(?i)(api[_-]?key|token|password|secret|authorization)\s*[:=]\s*[^\s,;]+", "$1=[privado]");
        text = Regex.Replace(text, @"(?i)(https?://)[^\s/@]+:[^\s/@]+@", "$1[credencial omitida]@");
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private static async Task WriteLoop()
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            if (item.Flushed != null) { item.Flushed.TrySetResult(); continue; }
            try
            {
                System.IO.Directory.CreateDirectory(item.Directory);
                var path = Path.Combine(item.Directory, "eventos.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + item.Json!.Length * 4L >= FileLimit) Rotate(item.Directory);
                await File.AppendAllTextAsync(path, item.Json + Environment.NewLine);
            }
            catch { Interlocked.Increment(ref writeErrors); }
        }
    }

    private static void Rotate(string directory)
    {
        for (int i = FileCount - 1; i >= 1; i--)
        {
            string target = Path.Combine(directory, $"eventos.{i}.jsonl");
            string previous = Path.Combine(directory, i == 1 ? "eventos.jsonl" : $"eventos.{i - 1}.jsonl");
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(previous)) File.Move(previous, target);
        }
    }
}
