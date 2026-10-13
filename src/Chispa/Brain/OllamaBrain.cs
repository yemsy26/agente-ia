using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Chispa;

/// <summary>
/// Cerebro local: un modelo de Ollama en este mismo equipo. Gratis, privado y sin internet.
/// El modelo solo ocupa memoria mientras se usa: Ollama lo descarga a los pocos minutos.
/// </summary>
internal sealed class OllamaBrain : IBrainProvider, ISeeingBrain
{
    private const string Eyes =
        "Eres los ojos de una asistente. Recibes una captura de la pantalla del usuario y una pregunta suya. " +
        "Contesta en español, en una a tres frases y sin listas ni símbolos, solo con lo que se ve en la imagen: " +
        "si pide leer algo, léelo tal cual; si algo no se distingue o no aparece, dilo en vez de suponerlo.";

    private static readonly JsonSerializerOptions wire = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions compact = new(Config.Json) { WriteIndented = false };
    private static readonly Regex tag = new(@"\[\[\s*(\w+)\s*(\{.*?\})?\s*\]\]", RegexOptions.Singleline);

    private readonly HttpClient http;
    private readonly string model;
    private bool nativeTools = true;   // pasa a false si el modelo no admite llamadas a funciones
    private bool canThink = true;      // pasa a false si el modelo no tiene modo de pensar y rechaza la opción

    /// <summary>
    /// Que «piense» por escrito antes de contestar: acierta más y tarda varias veces más.
    /// Se puede cambiar de una pregunta a otra. null = lo que traiga el modelo de serie.
    /// </summary>
    public bool? Think { get; set; }
    private long promptTokens, replyTokens, promptNs, replyNs, loadNs;
    private int requests;
    private int maxTokens = 384, contextSize = 4096;
    public Action<string>? Progress { get; set; }
    public void Configure(Config cfg) { maxTokens = Math.Clamp(cfg.MaximoTokensRespuesta, 64, 2048); contextSize = Math.Clamp(cfg.ContextoModelo, 2048, 8192); }
    internal static OllamaBrain WithClient(HttpClient http, string model) => new(http, model);

    /// <summary>Lo que ha costado hasta ahora: llamadas, palabras (tokens) leídas y escritas, y a qué velocidad.</summary>
    public string Stats =>
        requests == 0 ? "sin llamadas" :
        $"{requests} llamadas al modelo; leyó {promptTokens} tokens a {Rate(promptTokens, promptNs):F0} por segundo y escribió {replyTokens} a {Rate(replyTokens, replyNs):F0} por segundo; " +
        $"cargarlo costó {loadNs / 1e9:F1} s";

    private static double Rate(long tokens, long ns) => ns > 0 ? tokens / (ns / 1e9) : 0;

    private OllamaBrain(HttpClient http, string model)
    {
        this.http = http;
        this.model = model;
    }

    public string Name => $"Ollama ({model})";

    /// <summary>Devuelve null si Ollama no está abierto o no tiene ningún modelo.</summary>
    // Sin modelo elegido se usa el que mejor salió en el examen de entre los instalados y, si no está, el primero.
    private static readonly string[] recommended = ["qwen3.5:4b"];

    public static async Task<OllamaBrain?> TryConnect(string url, string preferredModel, CancellationToken ct)
    {
        var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120) };
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(3));
            using var doc = JsonDocument.Parse(await http.GetStringAsync("api/tags", probe.Token));
            var installed = doc.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0).ToList();
            // Nunca se pasa un modelo que no esté instalado: Ollama lo descargaría sin avisar (varios GB).
            string? model = preferredModel.Length > 0
                ? installed.FirstOrDefault(m => m.StartsWith(preferredModel, StringComparison.OrdinalIgnoreCase))
                : null;
            if (preferredModel.Length > 0 && model == null)
                Log.Write($"Ollama: el modelo «{preferredModel}» no está instalado; uso otro de los que hay.");
            model ??= recommended.Select(r => installed.FirstOrDefault(m => m.StartsWith(r, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(m => m != null)
                  ?? installed.FirstOrDefault();
            if (model != null) return new OllamaBrain(http, model);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            http.Dispose();
            ct.ThrowIfCancellationRequested();
            return null;
        }
        http.Dispose();
        return null;
    }

    public async Task<string> Reply(string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolSpec> tools,
                                    Func<string, JsonElement, Task<string>> runTool, CancellationToken ct)
    {
        using var phase = TraceLog.Measure("modelo.responder", data: new { provider = "ollama", runtime = model });
        try { return await ReplyCore(system, history, tools, runTool, ct); }
        catch (Exception ex) { phase.Fail(ex); throw; }
    }

    private async Task<string> ReplyCore(string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolSpec> tools,
                                    Func<string, JsonElement, Task<string>> runTool, CancellationToken ct)
    {
        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = new { type = "object", properties = t.Properties, required = t.Required } },
        }).ToArray();

        var messages = new List<object> { SystemMessage(system, tools) };
        messages.AddRange(history.Select(t => (object)new { role = t.FromUser ? "user" : "assistant", content = t.Text }));

        for (int round = 0; round < 4; round++)
        {
            ct.ThrowIfCancellationRequested();
            Progress?.Invoke(round == 0 ? "Consultando el modelo local…" : "Preparando el resultado…");
            var body = new { model, stream = true, keep_alive = "2m", think = canThink ? Think : null, messages, tools = nativeTools ? toolDefs : null,
                options = new { num_predict = maxTokens, num_ctx = contextSize, num_gpu = 99 } };
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(body, options: wire) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                string payload = await response.Content.ReadAsStringAsync(ct);
                if (canThink && Think != null && payload.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase))
                {
                    canThink = false;   // este modelo no tiene modo de pensar: no hay nada que encender ni apagar
                    round--;
                    continue;
                }
                if (nativeTools && payload.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                {
                    // Este modelo no sabe llamar funciones: se le enseña a pedirlas con etiquetas en el texto.
                    nativeTools = false;
                    messages[0] = SystemMessage(system, tools);
                    round--;
                    continue;
                }
                Log.Write($"Ollama {(int)response.StatusCode}: {payload}");
                throw new BrainException("Mi cerebro local dio un error. El detalle está en el registro.");
            }

            var (root, message) = await ReadStreaming(response, ct);
            long Count(string field) => root.TryGetProperty(field, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : 0;
            requests++;
            promptTokens += Count("prompt_eval_count");
            promptNs += Count("prompt_eval_duration");
            replyTokens += Count("eval_count");
            replyNs += Count("eval_duration");
            loadNs += Count("load_duration");
            string content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            content = Regex.Replace(content, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();

            var calls = new List<(string Name, JsonElement Args)>();
            if (!nativeTools) content = ExtractTags(content, calls);
            else if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var fn = call.GetProperty("function");
                    calls.Add((fn.GetProperty("name").GetString() ?? "", ParseArgs(fn.TryGetProperty("arguments", out var a) ? a : default)));
                }
            }
            bool asked = calls.Count > 0 && nativeTools;   // la pidió como es debido, no dentro del texto
            // A veces el modelo escribe la llamada en la respuesta en vez de pedirla. Si se reconoce, se ejecuta igual.
            if (calls.Count == 0) content = Salvage(content, tools, calls);

            if (calls.Count == 0)
            {
                if (content.Length == 0) throw new BrainException("El modelo terminó sin enviar una respuesta. El fallo quedó en el diagnóstico.");
                return content;
            }

            var performed = new List<(string Name, string Result)>();
            foreach (var (name, args) in calls)
                performed.Add((name, await runTool(name, args)));
            // Los efectos verificados ya tienen respuesta. No pedir otra inferencia para que el modelo los describa.
            if (performed.All(p => p.Name is "abrir" or "cerrar" or "volumen" or "multimedia" or "cuerpo" or "tamano" or "avisar" or "olvidar" or "recordar" or "escribir_texto"))
                return string.Join(" ", performed.Select(p => p.Result));
            messages.Add(message.Clone());
            if (asked)
            {
                foreach (var (name, result) in performed)
                    messages.Add(new { role = "tool", tool_name = name, content = result });
            }
            else
            {
                var results = new StringBuilder("Resultados de tus herramientas (no los copies tal cual; responde al usuario):\n");
                foreach (var (name, result) in performed) results.AppendLine($"- {name}: {result}");
                messages.Add(new { role = "user", content = results.ToString() });
            }
        }
        throw new BrainException("El modelo siguió consultando herramientas sin completar la respuesta. Detuve la consulta y registré el fallo.");
    }

    private async Task<(JsonElement Root, JsonElement Message)> ReadStreaming(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return (doc.RootElement.Clone(), doc.RootElement.GetProperty("message").Clone());
        }
        var elapsed = Stopwatch.StartNew();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        var calls = new List<JsonElement>();
        var seen = new HashSet<string>();
        JsonElement root = default;
        bool first = true, finished = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var chunk = doc.RootElement;
            if (chunk.TryGetProperty("error", out _)) throw new BrainException("El modelo interrumpió la consulta. El fallo quedó en el diagnóstico.");
            if (first) { first = false; TraceLog.Event("modelo.primer_fragmento", data: new { ms = elapsed.ElapsedMilliseconds, provider = "ollama" }); Progress?.Invoke("Generando la respuesta…"); }
            if (chunk.TryGetProperty("message", out var part))
            {
                if (part.TryGetProperty("content", out var content)) text.Append(content.GetString());
                if (part.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array)
                    foreach (var tool in tools.EnumerateArray())
                    {
                        var function = tool.GetProperty("function");
                        string key = function.GetProperty("name").GetString() + ":" + Canonical(ParseArgs(function.TryGetProperty("arguments", out var arguments) ? arguments : default));
                        if (seen.Add(key)) calls.Add(tool.Clone());
                    }
            }
            if (text.Length > 32 * 1024) throw new BrainException("La respuesta superó el límite de tamaño y la detuve.");
            if (chunk.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            { root = chunk.Clone(); finished = true; break; }
        }
        if (!finished) throw new BrainException("El modelo cerró la conexión sin completar la respuesta.");
        TraceLog.Event("modelo.generacion", data: new { ms = elapsed.ElapsedMilliseconds, caracteres = text.Length, llamadas = calls.Count, provider = "ollama" });
        return (root, JsonSerializer.SerializeToElement(new { role = "assistant", content = text.ToString(), tool_calls = calls }));
    }

    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number when element.TryGetDecimal(out var value) => value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.Undefined => "null",
        _ => element.GetRawText(),
    };

    /// <summary>Mira una imagen y contesta una pregunta sobre ella. Solo sirve con modelos que ven (Qwen3.5 sí).</summary>
    public async Task<string> See(byte[] image, string question, CancellationToken ct)
    {
        var body = new
        {
            model, stream = false, keep_alive = "1m", think = canThink ? (bool?)false : null,
            messages = new object[]
            {
                new { role = "system", content = Eyes },
                new { role = "user", content = question, images = new[] { Convert.ToBase64String(image) } },
            },
        };
        using var response = await http.PostAsJsonAsync("api/chat", body, wire, ct);
        string payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Log.Write($"Ollama al mirar la pantalla, {(int)response.StatusCode}: {payload}");
            return "Mi modelo no ha podido mirar la imagen.";
        }
        using var doc = JsonDocument.Parse(payload);
        long Count(string field) => doc.RootElement.TryGetProperty(field, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : 0;
        requests++;
        promptTokens += Count("prompt_eval_count");
        promptNs += Count("prompt_eval_duration");
        replyTokens += Count("eval_count");
        replyNs += Count("eval_duration");
        string seen = doc.RootElement.GetProperty("message").TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        seen = Regex.Replace(seen, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        return seen.Length > 0 ? seen : "No distingo nada en la captura.";
    }

    private const string Diarist =
        "Vas a recibir lo que un usuario y su asistente hablaron un día. Resúmelo en español, en tres a cinco frases seguidas, " +
        "sin listas: qué pidió, qué contó de sí mismo, qué quedó pendiente. Solo lo que aparece en el texto; nada inventado. " +
        "Escribe en tercera persona («pidió…», «contó que…»).";

    /// <summary>Resume un día de conversación para el diario. Suelta el modelo enseguida: se usa mientras duerme.</summary>
    public async Task<string> Summarize(string day, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        var body = new
        {
            model, stream = false, keep_alive = "20s", think = canThink ? (bool?)false : null,
            messages = new object[] { new { role = "system", content = Diarist }, new { role = "user", content = day } },
        };
        using var response = await http.PostAsJsonAsync("api/chat", body, wire, limit.Token);
        string payload = await response.Content.ReadAsStringAsync(limit.Token);
        if (!response.IsSuccessStatusCode) return "";
        using var doc = JsonDocument.Parse(payload);
        string text = doc.RootElement.GetProperty("message").TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        return Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
    }

    public void Dispose() => http.Dispose();

    private object SystemMessage(string system, IReadOnlyList<ToolSpec> tools)
    {
        if (nativeTools) return new { role = "system", content = system };
        var sb = new StringBuilder(system);
        sb.AppendLine().AppendLine("Herramientas");
        sb.AppendLine("Para usar una herramienta escribe, en una línea aparte, una etiqueta con este formato exacto: [[nombre {\"parametro\": \"valor\"}]]. Puedes usar varias. El sistema las ejecuta y las quita del texto antes de que el usuario lo oiga, así que no las menciones.");
        foreach (var t in tools)
            sb.AppendLine($"- {t.Name}: {t.Description} Parámetros: {JsonSerializer.Serialize(t.Properties, compact)}");
        return new { role = "system", content = sb.ToString() };
    }

    private static string ExtractTags(string content, List<(string Name, JsonElement Args)> calls)
    {
        foreach (Match m in tag.Matches(content))
            calls.Add((m.Groups[1].Value, ParseArgs(m.Groups[2].Success ? JsonSerializer.SerializeToElement(m.Groups[2].Value) : default)));
        return tag.Replace(content, "").Trim();
    }

    /// <summary>
    /// Rescata las llamadas que el modelo dejó escritas en el texto, en cualquiera de las formas que se le han visto:
    /// «investigar{consulta:&lt;|"|&gt;París&lt;|"|&gt;}», «&lt;cuerpo accion="saltar"/&gt;» o «{"name": "cuerpo", "arguments": {…}}».
    /// Solo vale el nombre de una herramienta que exista. Devuelve el texto sin ellas.
    /// </summary>
    internal static string Salvage(string content, IReadOnlyList<ToolSpec> tools, List<(string Name, JsonElement Args)> calls)
    {
        if (content.Length == 0) return content;
        string names = string.Join('|', tools.Select(t => Regex.Escape(t.Name)));
        const RegexOptions loose = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        string Take(Match m, string name, JsonElement? args)
        {
            if (args is not { } found) return m.Value;
            calls.Add((tools.First(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Name, found));
            return "";
        }

        content = Regex.Replace(content, $@"(?:<tool_call>\s*)?\{{\s*""name""\s*:\s*""({names})""\s*,\s*""(?:arguments|parameters)""\s*:\s*(\{{.*?\}})\s*\}}(?:\s*</tool_call>)?",
            m => Take(m, m.Groups[1].Value, AsObject(m.Groups[2].Value)), loose);
        content = Regex.Replace(content, $@"(?:call:)?(?<![\w""])({names})\s*(\{{.*?\}})",
            m => Take(m, m.Groups[1].Value, AsObject(m.Groups[2].Value)), loose);
        content = Regex.Replace(content, $@"<({names})((?:\s+[\w-]+\s*=\s*""[^""]*"")*)\s*/?>(?:\s*</\1>)?",
            m => Take(m, m.Groups[1].Value, JsonSerializer.SerializeToElement(
                Regex.Matches(m.Groups[2].Value, @"([\w-]+)\s*=\s*""([^""]*)""").ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Value))), loose);
        if (calls.Count == 0) return content;
        // Fuera las marcas internas que hayan quedado sueltas («<|"|>», «<turn|>»).
        return Regex.Replace(content, @"<\|[^<>]{0,20}>|<[^<>]{1,20}\|>", "").Trim();
    }

    /// <summary>Lee un objeto escrito con manga ancha: claves sin comillas y comillas con la marca de Gemma.</summary>
    private static JsonElement? AsObject(string text)
    {
        string json = text.Replace("<|\"|>", "\"");
        json = Regex.Replace(json, @"(?<=[{,]\s*)([A-Za-z_]\w*)\s*:", "\"$1\":");
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind == JsonValueKind.Object ? parsed.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Los argumentos llegan como objeto o como texto JSON, según el modelo.</summary>
    private static JsonElement ParseArgs(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Object) return raw.Clone();
        if (raw.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(raw.GetString() ?? "{}");
                if (parsed.RootElement.ValueKind == JsonValueKind.Object) return parsed.RootElement.Clone();
            }
            catch (JsonException) { /* argumentos mal formados: se trata como sin argumentos */ }
        }
        return JsonSerializer.SerializeToElement(new { });
    }
}
