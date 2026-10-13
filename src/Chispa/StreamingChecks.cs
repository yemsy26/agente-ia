using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chispa;

/// <summary>Prueba el protocolo local sin Ollama, GPU, red ni efectos en el equipo.</summary>
internal static class StreamingChecks
{
    private sealed class Handler(Func<Stream> content) : HttpMessageHandler
    {
        internal readonly List<JsonElement> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false));
            Bodies.Add(document.RootElement.Clone());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return response;
        }
    }

    /// <summary>Entrega un fragmento, luego espera permiso; permite comprobar progreso y cancelación reales.</summary>
    private sealed class PausedStream(string first, string remainder) : Stream
    {
        private readonly byte[] prefix = Encoding.UTF8.GetBytes(first), suffix = Encoding.UTF8.GetBytes(remainder);
        private int prefixPosition, suffixPosition;
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (prefixPosition < prefix.Length)
            {
                int length = Math.Min(buffer.Length, prefix.Length - prefixPosition);
                prefix.AsMemory(prefixPosition, length).CopyTo(buffer);
                prefixPosition += length;
                return length;
            }
            Waiting.TrySetResult();
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            int count = Math.Min(buffer.Length, suffix.Length - suffixPosition);
            suffix.AsMemory(suffixPosition, count).CopyTo(buffer);
            suffixPosition += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static async Task Run(Action<string, bool, string> check)
    {
        void Check(string title, bool passed, string detail = "") => check(title, passed, detail);
        static Task<string> NoTool(string _, JsonElement __) => throw new InvalidOperationException("No se esperaba una herramienta en esta respuesta.");
        static HttpClient Client(Handler handler) => new(handler) { BaseAddress = new Uri("http://prueba.invalid/"), Timeout = TimeSpan.FromSeconds(5) };

        // No basta comprobar el texto al terminar: el primer fragmento debe producir progreso mientras la red espera.
        var paused = new PausedStream(
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Hola \"},\"done\":false,\"eval_count\":999}\n",
            "{\"message\":{\"content\":\"listo.\"},\"done\":true,\"prompt_eval_count\":12,\"prompt_eval_duration\":120000000,\"eval_count\":4,\"eval_duration\":80000000,\"load_duration\":200000000}\n");
        using (var handler = new Handler(() => paused))
        using (var brain = OllamaBrain.WithClient(Client(handler), "modelo-prueba"))
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            brain.Configure(new Config { MaximoTokensRespuesta = 128, ContextoModelo = 2048 });
            brain.Think = false;
            var progress = new ConcurrentQueue<string>();
            brain.Progress = progress.Enqueue;
            Task<string> pending = brain.Reply("Responde breve.", [new(true, "Hola")], [], NoTool, limit.Token);
            await paused.Waiting.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            Check("streaming muestra progreso antes de terminar", !pending.IsCompleted && progress.Any(p => p.Contains("Generando")));
            paused.Release.TrySetResult();
            string reply = await pending.ConfigureAwait(false);
            Check("streaming acumula los fragmentos de texto", reply == "Hola listo.", reply);
            Check("streaming usa estadísticas del último fragmento", brain.Stats.Contains("1 llamadas") && brain.Stats.Contains("leyó 12 tokens") && brain.Stats.Contains("escribió 4") && !brain.Stats.Contains("999"), brain.Stats);
            JsonElement body = handler.Bodies.Single(), options = body.GetProperty("options");
            Check("streaming aplica límites y desactiva pensamiento solicitado", body.GetProperty("stream").GetBoolean() && !body.GetProperty("think").GetBoolean() && options.GetProperty("num_predict").GetInt32() == 128 && options.GetProperty("num_ctx").GetInt32() == 2048 && options.GetProperty("num_gpu").GetInt32() == 99);
        }

        // La llamada se repite en el protocolo con espacios distintos: sigue siendo la misma acción.
        string toolStream = "{\"message\":{\"content\":\"Claro, ya lo abrí.\",\"tool_calls\":[{\"function\":{\"name\":\"abrir\",\"arguments\":{\"destino\":\"navegador\"}}}]},\"done\":false}\n" +
            "{\"message\":{\"tool_calls\":[{ \"function\" : { \"name\" : \"abrir\", \"arguments\" : { \"destino\" : \"navegador\" } } }]},\"done\":true,\"eval_count\":2}\n";
        using (var handler = new Handler(() => new MemoryStream(Encoding.UTF8.GetBytes(toolStream))))
        using (var brain = OllamaBrain.WithClient(Client(handler), "modelo-prueba"))
        {
            int calls = 0;
            bool correctArgs = true;
            const string failure = "No pude abrir el navegador: Windows rechazó la apertura.";
            string answer = await brain.Reply("Ayuda.", [new(true, "abre el navegador")], [ToolBox.Specs.First(t => t.Name == "abrir")], (name, args) =>
            {
                calls++;
                correctArgs &= name == "abrir" && args.GetProperty("destino").GetString() == "navegador";
                return Task.FromResult(failure);
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Check("streaming ejecuta una vez una llamada repetida", calls == 1 && correctArgs, $"ejecuciones={calls}");
            Check("streaming responde con el resultado real sin otra inferencia", answer == failure && handler.Bodies.Count == 1, answer);
        }

        using (var handler = new Handler(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"eval_count\":0}\n"))))
        using (var brain = OllamaBrain.WithClient(Client(handler), "modelo-prueba"))
        {
            bool failed = false;
            string detail = "";
            try { await brain.Reply("Ayuda.", [new(true, "salúdame")], [], NoTool, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (BrainException ex) { failed = !string.IsNullOrWhiteSpace(ex.Message); detail = ex.Message; }
            Check("streaming comunica un fallo si done llega sin contenido", failed && handler.Bodies.Count == 1, detail);
        }

        const string readingLoop = "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"contexto_equipo\",\"arguments\":{}}}]},\"done\":true}\n";
        using (var handler = new Handler(() => new MemoryStream(Encoding.UTF8.GetBytes(readingLoop))))
        using (var brain = OllamaBrain.WithClient(Client(handler), "modelo-prueba"))
        {
            bool failed = false;
            int readCalls = 0;
            string detail = "";
            try
            {
                await brain.Reply("Ayuda.", [new(true, "resume el contexto")], [ToolBox.Specs.First(t => t.Name == "contexto_equipo")], (name, _) =>
                {
                    if (name != "contexto_equipo") throw new InvalidOperationException("Solo se permite la lectura simulada del contexto.");
                    readCalls++;
                    return Task.FromResult("Contexto de prueba: no se consultó el equipo real.");
                }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (BrainException ex) { failed = !string.IsNullOrWhiteSpace(ex.Message); detail = ex.Message; }
            Check("streaming limita a cuatro rondas de lectura y comunica el fallo", failed && handler.Bodies.Count == 4 && readCalls == 4, $"HTTP={handler.Bodies.Count}, lecturas simuladas={readCalls}; {detail}");
        }

        using (var handler = new Handler(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"message\":{\"content\":\"Respuesta parcial\"},\"done\":false}\n"))))
        using (var brain = OllamaBrain.WithClient(Client(handler), "modelo-prueba"))
        {
            bool failed = false;
            try { await brain.Reply("Ayuda.", [new(true, "hola")], [], NoTool, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (BrainException ex) { failed = ex.Message.Contains("sin completar"); }
            Check("streaming rechaza una conexión cerrada sin respuesta final", failed);
        }

        var blocked = new PausedStream("{\"message\":{\"content\":\"Pensando\"},\"done\":false}\n", "");
        using (var handler = new Handler(() => blocked))
        using (var brain = OllamaBrain.WithClient(Client(handler), "modelo-prueba"))
        using (var cancel = new CancellationTokenSource())
        {
            Task<string> waiting = brain.Reply("Ayuda.", [new(true, "hola")], [], NoTool, cancel.Token);
            await blocked.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            cancel.Cancel();
            bool canceled = false;
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled = true; }
            Check("streaming cancela una lectura que nunca termina", canceled);
        }
    }
}
