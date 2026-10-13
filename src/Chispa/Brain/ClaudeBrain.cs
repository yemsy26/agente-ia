using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Chispa;

/// <summary>Cerebro en la nube: Claude, a través del SDK oficial de Anthropic.</summary>
internal sealed class ClaudeBrain(string model) : IBrainProvider
{
    private readonly AnthropicClient client = new();   // credenciales del entorno (ANTHROPIC_API_KEY)

    public string Name => $"Claude ({model})";

    public async Task<string> Reply(string system, IReadOnlyList<ChatTurn> history, IReadOnlyList<ToolSpec> tools,
                                    Func<string, JsonElement, Task<string>> runTool, CancellationToken ct)
    {
        List<MessageParam> messages = history
            .Select(t => new MessageParam { Role = t.FromUser ? Role.User : Role.Assistant, Content = t.Text })
            .ToList();
        List<ToolUnion> toolDefs = tools
            .Select(t => (ToolUnion)new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new() { Properties = t.Properties, Required = t.Required },
            })
            .ToList();

        string said = "";
        try
        {
            for (int round = 0; round < 8; round++)
            {
                var response = await client.Messages.Create(new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = 16000,
                    System = system,
                    // Charla hablada: respuestas cortas y rápidas. El esfuerzo regula cuánto razona antes de contestar.
                    OutputConfig = new OutputConfig { Effort = Effort.Low },
                    Tools = toolDefs,
                    Messages = messages,
                }, cancellationToken: ct);

                if (response.StopReason == "refusal") return "Con eso no te puedo ayudar.";

                // Se devuelve a la API el turno completo tal cual llegó (incluido el razonamiento firmado).
                List<ContentBlockParam> assistant = [];
                List<ToolUseBlock> calls = [];
                var text = new StringBuilder();
                foreach (ContentBlock block in response.Content)
                {
                    if (block.TryPickText(out TextBlock? spoken))
                    {
                        if (spoken.Text.Length == 0) continue;
                        text.Append(spoken.Text);
                        assistant.Add(new TextBlockParam { Text = spoken.Text });
                    }
                    else if (block.TryPickThinking(out ThinkingBlock? thought))
                        assistant.Add(new ThinkingBlockParam { Thinking = thought.Thinking, Signature = thought.Signature });
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                        assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                    else if (block.TryPickToolUse(out ToolUseBlock? use))
                    {
                        calls.Add(use);
                        assistant.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    }
                }
                if (text.Length > 0) said = text.ToString();

                // Si la respuesta se cortó, no se ejecuta nada: la entrada de una herramienta podría estar incompleta.
                if (calls.Count == 0 || response.StopReason != "tool_use") break;

                List<ContentBlockParam> results = [];
                foreach (var call in calls)
                {
                    string result = await runTool(call.Name, JsonSerializer.SerializeToElement(call.Input));
                    results.Add(new ToolResultBlockParam { ToolUseID = call.ID, Content = result });
                }
                messages.Add(new() { Role = Role.Assistant, Content = assistant });
                messages.Add(new() { Role = Role.User, Content = results });
            }
            return said;
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new BrainException("Mi clave de Claude no es válida. Revisa la variable ANTHROPIC_API_KEY.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new BrainException("Claude está saturado ahora mismo. Probemos en un momento.");
        }
        catch (Anthropic5xxException)
        {
            throw new BrainException("Claude tiene un problema temporal. Probemos en un momento.");
        }
        catch (AnthropicIOException)
        {
            throw new BrainException("No tengo conexión a internet para pensar con Claude.");
        }
        catch (AnthropicApiException ex)
        {
            Log.Write($"Claude: {ex.Message}");
            throw new BrainException("Claude rechazó mi petición. El detalle está en el registro.");
        }
    }

    public void Dispose() { }
}
