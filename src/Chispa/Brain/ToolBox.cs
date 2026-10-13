using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chispa;

/// <summary>Lo que el cuerpo sabe hacer. Lo implementa la app real y un doble para pruebas.</summary>
internal interface IBody
{
    void Say(string text);
    /// <summary>saltar, bailar, saludar, venir, esconderse, aparecer, dormir, callar. Devuelve el resultado en una frase.</summary>
    string Act(string action);
    string ChangeSize(string tamano);
    void Thinking(bool on);
    void SetMuted(bool muted);
    /// <summary>Modo conversación: sigue escuchando sin exigir su nombre hasta que se le diga que es todo.</summary>
    void Converse(bool on);
    /// <summary>Ejecuta algo dentro de un rato (avisos y temporizadores).</summary>
    void Later(TimeSpan delay, Action action);
    void OpenGuide();
    /// <summary>Cambia su nombre, que es también la palabra que la despierta.</summary>
    void Rename(string name);
    /// <summary>Ajusta la voz por pasos (tono: + más aguda; velocidad: + más rápida); con reset vuelve a la normal.</summary>
    void TuneVoice(int pitchSteps, int speedSteps, bool reset = false);
    /// <summary>
    /// Cambia de voz: "siguiente", "anterior", "hombre", "mujer" o el identificador de una.
    /// Devuelve cómo presentarla ("de mujer número 3"), o vacío si no hay ninguna así.
    /// </summary>
    string ChangeVoice(string which);

    // ---- lo que toca el equipo (quien lo llama ya comprobó que se puede) ----

    /// <summary>Abre un programa, una carpeta o una dirección.</summary>
    Task<LaunchResult> Launch(string command, CancellationToken ct = default);
    /// <summary>Pulsa una tecla del sistema (volumen, música).</summary>
    void PressKey(ushort key, int times = 1);
    /// <summary>Teclea un texto en la ventana que tiene el foco; con enter, pulsa Intro al final.</summary>
    void TypeText(string text, bool enter);
    /// <summary>Las ventanas abiertas, de delante hacia atrás.</summary>
    IReadOnlyList<WindowRef> OpenWindows();
    /// <summary>Pide a una ventana que se cierre, como al pulsar su aspa.</summary>
    void CloseWindow(WindowRef window);
    /// <summary>Una foto (JPEG) de lo que el usuario tiene delante, o null si no se pudo.</summary>
    byte[]? Screenshot();
}

internal sealed record ToolSpec(string Name, string Description, Dictionary<string, JsonElement> Properties, string[] Required);

/// <summary>Herramientas que la IA puede usar. Las que cambian algo en el equipo esperan la confirmación hablada.</summary>
internal static class ToolBox
{
    public const string Denied = "El usuario no lo confirmó; no se hizo nada.";

    // Solo destinos conocidos: nunca ejecuta comandos arbitrarios.
    private static readonly Dictionary<string, (string Command, string Label)> apps = new()
    {
        ["calculadora"] = ("calc.exe", "la calculadora"),
        ["bloc de notas"] = ("notepad.exe", "el bloc de notas"),
        ["explorador"] = ("explorer.exe", "el explorador de archivos"),
        ["configuracion"] = ("ms-settings:", "la configuración de Windows"),
        ["navegador"] = ("https://www.google.com", "el navegador"),
        ["paint"] = ("mspaint.exe", "Paint"),
        ["reloj"] = ("ms-clock:", "el reloj"),
        ["camara"] = ("microsoft.windows.camera:", "la cámara"),
        ["tienda"] = ("ms-windows-store:", "la tienda de Windows"),
        ["google"] = ("https://www.google.com", "Google"),
        ["youtube"] = ("https://www.youtube.com", "YouTube"),
        ["wikipedia"] = ("https://es.wikipedia.org", "Wikipedia"),
        ["gmail"] = ("https://mail.google.com", "Gmail"),
        ["correo"] = ("https://mail.google.com", "el correo"),
        ["whatsapp"] = ("https://web.whatsapp.com", "WhatsApp"),
        ["mapas"] = ("https://www.google.com/maps", "los mapas"),
    };

    private static readonly Lazy<HttpClient> http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(12) });

    public static readonly ToolSpec[] Specs =
    [
        new("cuerpo", "Mueve tu cuerpo en la pantalla. Úsala cuando te pidan una de estas acciones o cuando encaje como gesto.",
            new() { ["accion"] = P(new { type = "string", @enum = new[] { "saltar", "bailar", "saludar", "venir", "esconderse", "aparecer", "dormir" }, description = "venir = caminar hasta el cursor del ratón" }) },
            ["accion"]),
        new("tamano", "Ajusta el tamaño del avatar actual.",
            new() { ["tamano"] = P(new { type = "string", @enum = new[] { "mas_grande", "mas_pequeno", "normal" } }) }, ["tamano"]),
        new("recordar", "Guarda en tu memoria permanente un dato duradero sobre el usuario (nombre, gustos, rutinas, personas, preferencias). Una frase clara por dato. Si el guardián la rechaza porque contradice lo que ya sabes, no insistas.",
            new() { ["dato"] = P(new { type = "string", description = "El dato en tercera persona, p. ej. «Se llama Ana y prefiere que la tutee»" }) },
            ["dato"]),
        new("diario", "Busca en el diario de todas las conversaciones con el usuario, desde el primer día: qué dijo, qué pidió, qué le contestaste y cuándo. Úsala cuando pregunte por algo hablado otro día o quiera retomar un tema.",
            new()
            {
                ["consulta"] = P(new { type = "string", description = "De qué se habló: palabras del tema, p. ej. «informe de ventas», «ruedas del coche». Vacío si solo importa la fecha." }),
                ["cuando"] = P(new { type = "string", description = "Opcional: «ayer», «el lunes», «la semana pasada», «agosto», «hace dos meses», «2026-09»" }),
            },
            []),
        new("avisar", "Programa un aviso: llegado el momento se lo dices en voz alta al usuario. Úsala siempre que pida que le recuerdes o le avises de algo a una hora o dentro de un rato. Sin esta herramienta no hay aviso: no digas que te acordarás si no la has usado.",
            new()
            {
                ["cuando"] = P(new { type = "string", description = "«en 10 minutos», «en una hora», «a las 17:30», «a las cinco de la tarde», «mañana a las 8»" }),
                ["mensaje"] = P(new { type = "string", description = "De qué hay que avisarle, p. ej. «llamar al banco»" }),
            },
            ["cuando"]),
        new("olvidar", "Borra de tu memoria los recuerdos que mencionan un tema, cuando el usuario lo pida.",
            new() { ["sobre"] = P(new { type = "string", description = "Palabras clave del recuerdo a borrar" }) },
            ["sobre"]),
        new("contexto_equipo", "Lee datos actuales del equipo: fecha y hora, zona horaria y país configurados, usuario de Windows, batería, espacio libre en el disco, las ventanas que hay abiertas y la que el usuario tiene delante.",
            new(), []),
        new("investigar", "Busca en Wikipedia en español y devuelve un resumen. Para hechos del mundo que no sepas con seguridad. No incluyas datos personales del usuario en la consulta.",
            new() { ["consulta"] = P(new { type = "string", description = "Tema o nombre a buscar" }) },
            ["consulta"]),
        new("razonar", "Resuelve con lógica exacta un reparto: colocar varias cosas (citas, tareas, personas) en huecos (días, horas, sitios) cumpliendo condiciones. Úsala siempre para estos problemas en vez de resolverlos tú: devuelve un reparto comprobado, o que es imposible y por qué. Di su resultado tal cual.",
            new()
            {
                ["cosas"] = P(new { type = "array", items = new { type = "string" }, description = "Lo que hay que colocar, p. ej. [\"dentista\", \"banco\", \"taller\"]" }),
                ["huecos"] = P(new { type = "array", items = new { type = "string" }, description = "Dónde se colocan, en orden, p. ej. [\"lunes\", \"martes\", \"miércoles\"]" }),
                ["reglas"] = P(new { type = "array", items = new { type = "string" }, description = "Todas las condiciones que dio el usuario, una por frase, con los mismos nombres y una de estas formas: «dentista no puede lunes» (cerrado, ocupado, no quiere), «taller solo miércoles», «banco lunes o martes», «banco antes de taller», «banco después de taller», «banco justo antes de taller», «banco y taller juntos», «banco y taller separados», «máximo 1 por hueco» (no quiere dos en el mismo), «máximo 2 por hueco». No añadas condiciones que el usuario no dijo ni te dejes ninguna." }),
            },
            ["cosas", "huecos"]),
        new("abrir", "Abre en el equipo del usuario un programa instalado (por su nombre: «chrome», «word», «calculadora», «bloc de notas»…), una carpeta («descargas», «documentos», «escritorio», «imágenes», «música», «vídeos», o su ruta) o una página web (dirección http o https completa; para buscar algo, la dirección de búsqueda de Google o YouTube). Úsala en cuanto el usuario pida abrir, poner, mostrar o buscar algo: no preguntes antes.",
            new() { ["destino"] = P(new { type = "string", description = "Nombre del programa o de la carpeta, o dirección web completa" }) },
            ["destino"]),
        new("cerrar", "Cierra una ventana abierta, como si el usuario pulsara su aspa. Úsala cuando pida cerrar algo.",
            new() { ["ventana"] = P(new { type = "string", description = "Parte del título o nombre del programa (p. ej. «chrome», «bloc de notas»). Vacío = la ventana que tiene delante." }) },
            []),
        new("ver_pantalla", "Mira la pantalla del usuario (se hace una captura en este momento) y contesta una pregunta sobre lo que se ve. Úsala cuando pregunte qué hay, qué dice o qué pone en pantalla, o pida leer o explicar algo que tiene delante.",
            new() { ["pregunta"] = P(new { type = "string", description = "Qué quiere saber de lo que hay en pantalla" }) },
            ["pregunta"]),
        new("volumen", "Sube, baja o silencia el volumen del equipo.",
            new()
            {
                ["accion"] = P(new { type = "string", @enum = new[] { "subir", "bajar", "silenciar" } }),
                ["pasos"] = P(new { type = "integer", description = "Cada paso es un 2 %. Por defecto 5." }),
            },
            ["accion"]),
        new("multimedia", "Controla la música o el vídeo que esté sonando.",
            new() { ["accion"] = P(new { type = "string", @enum = new[] { "pausa_reproducir", "siguiente", "anterior" } }) },
            ["accion"]),
        new("escribir_texto", "Escribe un texto en la ventana que el usuario tiene activa, como si lo tecleara (sirve para responder mensajes). Pide confirmación hablada al usuario.",
            new()
            {
                ["texto"] = P(new { type = "string" }),
                ["enviar"] = P(new { type = "boolean", description = "true para pulsar Enter al terminar (envía el mensaje)" }),
            },
            ["texto"]),
    ];

    public static async Task<string> Run(string name, JsonElement args, Mind mind, bool parsedRequest = false)
    {
        using var trace = TraceLog.Measure("herramienta", args.ValueKind == JsonValueKind.Undefined ? null : args.GetRawText(), new { tool = name });
        string Str(string key) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Null ? "" : v.ToString()
                : "";
        // Una lista: lo normal es que llegue como tal, pero algún modelo la manda en una sola frase.
        string[] Many(string key) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).ToArray()
                : Regex.Split(Str(key), @"\s*[;\n]+\s*").SelectMany(part => key == "reglas" ? new[] { part } : Allocation.List(part)).ToArray();

        try
        {
            mind.ThrowIfTurnCanceled();
            switch (name)
            {
                case "cuerpo":
                    return mind.Body.Act(Str("accion"));
                case "tamano":
                    return mind.Body.ChangeSize(Str("tamano"));
                case "recordar":
                    return mind.Record(Str("dato"));   // el Guardián lo revisa antes de persistirlo
                case "diario":
                    return mind.Recall(Str("consulta"), Str("cuando"));
                case "avisar":
                    return mind.Own.Schedule(Str("cuando"), Str("mensaje"))
                           ?? "No entendí cuándo. Dímelo como «en 10 minutos», «a las 17:30» o «mañana a las 8».";
                case "olvidar":
                {
                    int removed = mind.Memory.Forget(Str("sobre"));
                    return removed > 0 ? $"Borrados {removed} recuerdos." : "No había ningún recuerdo sobre eso.";
                }
                case "contexto_equipo":
                    return DeviceContext(mind.Body);
                case "investigar":
                    return await Research(Str("consulta"));
                case "razonar":
                    return Allocation.Answer(Many("cosas"), Many("huecos"), Many("reglas"), mind.TurnCancellation);
                case "abrir":
                {
                    if (ResolveTarget(Str("destino")) is not { } target) return NotFound;
                    if (!await mind.Allowed(name, $"abrir {target.Label}", target.Command, parsedRequest)) return Denied;
                    mind.ThrowIfTurnCanceled();
                    var result = await mind.Body.Launch(target.Command, mind.TurnCancellation);
                    TraceLog.Event("apertura_verificacion", data: new { tool = name, iniciada = result.Started, visible = result.WindowVisible, frente = result.Foreground });
                    mind.ThrowIfTurnCanceled();
                    if (!result.Started) { trace.Mark("fallo"); return $"No pude abrir {target.Label}: {result.Detail}"; }
                    if (!result.WindowVisible) { trace.Mark("fallo"); return $"Windows aceptó abrir {target.Label}, pero no pude verificar su ventana. {result.Detail}"; }
                    return result.Foreground ? $"Abrí {target.Label} y está al frente." : $"La ventana correspondiente a {target.Label} está abierta, pero Windows no permitió ponerla al frente.";
                }
                case "cerrar":
                {
                    var windows = mind.Body.OpenWindows();
                    if (windows.Count == 0) return "No veo ninguna ventana abierta.";
                    if (FindWindow(windows, Str("ventana")) is not { } window)
                        return $"No encuentro ninguna ventana así. Las que hay abiertas: {string.Join("; ", windows.Take(6).Select(w => Short(w.Title)))}.";
                    if (!await mind.Allowed(name, $"cerrar la ventana «{Short(window.Title)}»")) return Denied;
                    mind.ThrowIfTurnCanceled();
                    string label = Short(window.Title);
                    // Ejecutar y verificar: se cierra y se mira si de verdad desapareció de la lista; si no,
                    // un segundo intento y, si tampoco, se dice el fallo en vez de darlo por hecho.
                    for (int intento = 1; intento <= 2; intento++)
                    {
                        mind.Body.CloseWindow(window);
                        for (int i = 0; i < 20 && mind.Body.OpenWindows().Any(w => w.Handle == window.Handle); i++)
                            await Task.Delay(50, mind.TurnCancellation);
                        mind.ThrowIfTurnCanceled();
                        if (!mind.Body.OpenWindows().Any(w => w.Handle == window.Handle))
                            return $"Cerrada «{label}».";
                        TraceLog.Event("cierre_verificacion", data: new { ventana = label, intento });
                    }
                    trace.Mark("fallo");
                    return $"No pude cerrar «{label}»: la ventana sigue abierta tras dos intentos.";
                }
                case "ver_pantalla":
                {
                    // La captura puede enseñar banca o correo: si no fue idea del usuario, espera su confirmación.
                    if (!await mind.Allowed(name, "mirar la pantalla")) return Denied;
                    return await mind.See(Str("pregunta"));
                }
                case "volumen":
                {
                    string action = Str("accion");
                    ushort key = action switch { "subir" => Native.VK_VOLUME_UP, "bajar" => Native.VK_VOLUME_DOWN, "silenciar" => Native.VK_VOLUME_MUTE, _ => 0 };
                    if (key == 0) return "Acción no válida: usa subir, bajar o silenciar.";
                    int steps = action == "silenciar" ? 1 : Math.Clamp(int.TryParse(Str("pasos"), out int n) ? n : 5, 1, 25);
                    if (!await mind.Allowed(name, action == "silenciar" ? "silenciar o reactivar el sonido" : $"{action} el volumen")) return Denied;
                    mind.ThrowIfTurnCanceled();
                    mind.Body.PressKey(key, steps);
                    return "Hecho.";
                }
                case "multimedia":
                {
                    string action = Str("accion");
                    ushort key = action switch { "pausa_reproducir" => Native.VK_MEDIA_PLAY_PAUSE, "siguiente" => Native.VK_MEDIA_NEXT, "anterior" => Native.VK_MEDIA_PREV, _ => 0 };
                    if (key == 0) return "Acción no válida: usa pausa_reproducir, siguiente o anterior.";
                    string what = action switch { "siguiente" => "pasar a la siguiente pista", "anterior" => "volver a la pista anterior", _ => "pausar o reanudar lo que suena" };
                    if (!await mind.Allowed(name, what)) return Denied;
                    mind.ThrowIfTurnCanceled();
                    mind.Body.PressKey(key);
                    return "Hecho.";
                }
                case "escribir_texto":
                {
                    string text = Str("texto");
                    bool send = Str("enviar").Equals("true", StringComparison.OrdinalIgnoreCase);
                    if (text.Length == 0) return "Falta el texto.";
                    var target = Native.WindowInfo();
                    using var me = Process.GetCurrentProcess();
                    if (target.Handle == IntPtr.Zero || target.Process == me.ProcessName)
                        return "No hay una ventana activa donde escribir. Pide al usuario que haga clic donde quiere el texto y lo vuelva a pedir.";
                    string where = target.Title.Length > 0 ? target.Title : target.Process;
                    if (where.Length > 50) where = where[..50] + "…";
                    if (!await mind.Allowed(name, $"escribir «{text}» en la ventana «{where}»{(send ? " y pulsar Enter" : "")}")) return Denied;
                    mind.ThrowIfTurnCanceled();
                    // Nunca escribe en una ventana distinta de la que anunció.
                    if (Native.GetForegroundWindow() != target.Handle) return "Cancelado: la ventana activa cambió mientras esperaba la confirmación.";
                    mind.Body.TypeText(text, send);
                    return "Escrito.";
                }
                default:
                    return $"No existe la herramienta «{name}».";
            }
        }
        catch (OperationCanceledException ex) { trace.Fail(ex); throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trace.Fail(ex);
            Log.Write($"Herramienta {name}: {ex.Message}");
            return $"Error al ejecutar {name}: {ex.Message}";
        }
    }

    private static JsonElement P(object schema) => JsonSerializer.SerializeToElement(schema);

    public const string NotFound = "No encuentro eso entre los programas instalados ni entre las carpetas conocidas. Puedo abrir un programa por su nombre, una carpeta (Descargas, Documentos, Escritorio…) o una dirección web.";

    private static readonly Regex targetLead = new(@"^(?:(?:el|la|los|las|mi|mis|un|una|al|del|de|programa|aplicacion|app|carpeta|pagina|web|sitio|ventana|esta|este|esa|ese|actual|activa) )+");
    // Por su ruta se abren carpetas y documentos, nunca programas ni guiones: para los programas está el menú Inicio.
    // La lista es la misma que la defensa de Device, para que no haya un camino que no la mire.
    private static readonly string[] neverByPath = Device.NeverByExtension;
    // Cómo se llama de verdad el programa de lo que la gente nombra por lo que hace.
    private static readonly Dictionary<string, string[]> programAliases = new()
    {
        ["navegador"] = ["chrome", "msedge", "firefox", "opera", "brave"],
        ["bloc de notas"] = ["notepad", "bloc de notas"],
        ["explorador"] = ["explorer", "explorador de archivos"],
        ["calculadora"] = ["calculadora", "calculator", "calc"],
        ["word"] = ["winword", "word"],
        ["musica"] = ["spotify", "music"],
        ["correo"] = ["outlook", "gmail", "mail"],
    };

    private static readonly Regex insideOf = new(@"^(.+?) (?:que esta |que hay |que tengo |de dentro |dentro )?(?:en|de|del) (?:el |la |mi |mis )?(escritorio|documentos|descargas|imagenes|fotos|musica|videos)$");

    private static string Short(string title) => title.Length > 50 ? title[..50] + "…" : title;

    private static bool HasWords(string text, string phrase) => $" {text} ".Contains($" {phrase} ");

    /// <summary>Qué abrir cuando el usuario dice «chrome», «la carpeta de descargas» o una dirección: lo que se lanza y cómo llamarlo.</summary>
    internal static (string Command, string Label)? ResolveTarget(string target)
    {
        target = target.Trim().Trim('«', '»', '"', '\'');
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return (uri.AbsoluteUri, $"la página {uri.Host}");
        if (target.Length > 2 && (target[1] == ':' || target.StartsWith(@"\\")))
        {
            if (Directory.Exists(target)) return (target, $"la carpeta {Path.GetFileName(target.TrimEnd('\\', '/'))}");
            return File.Exists(target) && !neverByPath.Contains(Path.GetExtension(target).ToLowerInvariant())
                ? (target, $"el archivo {Path.GetFileName(target)}") : null;
        }

        string wanted = Reflexes.Normalize(target);
        string name = targetLead.Replace(wanted + " ", "").Trim();
        if (name.Length == 0) return null;
        bool folderAsked = HasWords(wanted, "carpeta");
        if (folderAsked)
        {
            // «la carpeta facturas que está en el escritorio»: primero dónde, luego cuál.
            if (insideOf.Match(name) is { Success: true } where && Device.KnownFolder(where.Groups[2].Value) is { } parent)
                return Device.FindFolder(where.Groups[1].Value.Trim(), parent.Path);
            // Si pidió una carpeta, se le da una carpeta o nada: nunca un programa que se llame parecido.
            return Device.KnownFolder(name) ?? Device.FindFolder(name);
        }
        if (apps.TryGetValue(name, out var exactApp)) return exactApp;
        if (Device.KnownFolder(name) is { } folder) return folder;

        // Un programa del menú Inicio: el que se llama igual; el más corto que empieza o acaba por lo dicho («chrome» →
        // «Google Chrome»); o el más largo contenido en lo dicho. Una palabra suelta en mitad de un nombre largo no basta.
        var programs = Device.Programs();
        var found = programs.FirstOrDefault(p => p.Key == name);
        if (found.Path == null)
            found = programs.Where(p => p.Key.StartsWith(name + " ") || p.Key.EndsWith(" " + name) || (HasWords(p.Key, name) && name.Length * 2 >= p.Key.Length))
                .OrderBy(p => p.Key.Length).FirstOrDefault();
        if (found.Path == null) found = programs.Where(p => p.Key.Length >= 4 && HasWords(name, p.Key)).OrderByDescending(p => p.Key.Length).FirstOrDefault();
        return found.Path != null ? (found.Path, found.Name) : Device.FindFolder(name);
    }

    /// <summary>La ventana que el usuario quiere decir: la de delante si no nombra ninguna, o la que coincide en título o programa.</summary>
    internal static WindowRef? FindWindow(IReadOnlyList<WindowRef> windows, string said)
    {
        string wanted = targetLead.Replace(Reflexes.Normalize(said) + " ", "").Trim();
        if (wanted.Length == 0) return windows.Count > 0 ? windows[0] : null;
        string[] names = programAliases.TryGetValue(wanted, out var aliases) ? [wanted, .. aliases] : [wanted];
        foreach (var window in windows)
        {
            string title = Reflexes.Normalize(window.Title), process = Reflexes.Normalize(window.Process);
            if (names.Any(n => HasWords(title, n) || process == n || process.StartsWith(n))) return window;
        }
        return null;
    }

    /// <summary>El programa conocido que más se parece a lo pedido, para proponérselo cuando nada coincide.</summary>
    internal static (string Name, string Label)? ClosestApp(string said)
    {
        string wanted = targetLead.Replace(Reflexes.Normalize(said) + " ", "").Trim();
        if (wanted.Length < 3) return null;
        (string Name, string Label)? best = null;
        double bestScore = 0.5;
        foreach (var app in apps)
        {
            double score = HasWords(app.Key, wanted) || HasWords(wanted, app.Key) ? 0.8 : Reflexes.Similarity(wanted, app.Key);
            if (score > bestScore) { best = (app.Key, app.Value.Label); bestScore = score; }
        }
        return best;
    }

    /// <summary>La ventana abierta que más se parece a lo dicho, para proponérsela cuando ninguna coincide.</summary>
    internal static WindowRef? ClosestWindow(IReadOnlyList<WindowRef> windows, string said)
    {
        string wanted = targetLead.Replace(Reflexes.Normalize(said) + " ", "").Trim();
        if (wanted.Length < 3) return null;
        WindowRef? best = null;
        double bestScore = 0.5;
        foreach (var window in windows)
        {
            string title = Reflexes.Normalize(window.Title), process = Reflexes.Normalize(window.Process);
            double score = Math.Max(Reflexes.Similarity(wanted, process),
                title.Split(' ').Where(w => w.Length > 0).DefaultIfEmpty("").Max(w => Reflexes.Similarity(wanted, w)));
            if (score > bestScore) { best = window; bestScore = score; }
        }
        return best;
    }

    /// <summary>La ventana que el usuario tiene delante, o null si no hay ninguna.</summary>
    public static string? ActiveWindow()
    {
        var window = Native.WindowInfo();
        if (window.Handle == IntPtr.Zero) return null;
        return window.Title.Length > 0 ? $"«{window.Title}» (programa: {window.Process})" : window.Process;
    }

    public static string Battery()
    {
        var power = SystemInformation.PowerStatus;
        return power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)
            ? "sin batería (equipo de sobremesa)"
            : $"{power.BatteryLifePercent:P0}, {(power.PowerLineStatus == PowerLineStatus.Online ? "enchufado" : "sin enchufar")}";
    }

    /// <summary>Cuánto sitio queda en el disco donde está Windows.</summary>
    public static string Disk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return $"En el disco {drive.Name.TrimEnd('\\')} quedan {drive.AvailableFreeSpace / 1e9:0} GB libres de {drive.TotalSize / 1e9:0}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "No pude mirar el disco."; }
    }

    /// <summary>País o región configurado en Windows: lo más que sabe del lugar sin usar internet.</summary>
    public static string Region() => RegionInfo.CurrentRegion.DisplayName;

    private static string DeviceContext(IBody body)
    {
        var es = CultureInfo.GetCultureInfo("es-ES");
        var sb = new StringBuilder();
        sb.AppendLine($"Disco: {Disk()}");
        var windows = body.OpenWindows();
        sb.AppendLine($"Ventanas abiertas ({windows.Count}): {string.Join("; ", windows.Take(8).Select(w => Short(w.Title)))}");
        sb.AppendLine($"Fecha y hora: {DateTime.Now.ToString("dddd d 'de' MMMM 'de' yyyy, HH:mm", es)}");
        sb.AppendLine($"Zona horaria: {TimeZoneInfo.Local.DisplayName}");
        sb.AppendLine($"País o región configurado en Windows: {Region()}");
        sb.AppendLine($"Usuario de Windows: {Environment.UserName}");
        sb.AppendLine($"Batería: {Battery()}");
        sb.AppendLine($"Ventana activa: {ActiveWindow() ?? "ninguna"}");
        return sb.ToString();
    }

    /// <summary>Busca en Wikipedia en español; devuelve título y resumen de los primeros resultados.</summary>
    public static async Task<List<(string Title, string Extract)>> Wikipedia(string query, int count = 2)
    {
        string url = $"https://es.wikipedia.org/w/api.php?action=query&format=json&generator=search&gsrlimit={count}"
                     + "&prop=extracts&exintro=1&explaintext=1&exchars=900&gsrsearch=" + Uri.EscapeDataString(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Chispa/0.2 (asistente de escritorio)");
        using var response = await http.Value.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var found = new List<(string Title, string Extract)>();
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages)) return found;
        foreach (var page in pages.EnumerateObject().OrderBy(p => p.Value.TryGetProperty("index", out var i) ? i.GetInt32() : 99))
        {
            string title = page.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string extract = page.Value.TryGetProperty("extract", out var e) ? e.GetString() ?? "" : "";
            if (extract.Length > 0) found.Add((title, extract));
        }
        return found;
    }

    /// <summary>El tiempo en una ciudad, con Open-Meteo (gratis y sin clave). Devuelve null si no encuentra la ciudad o el servicio responde raro.</summary>
    public static async Task<string?> Weather(string city, bool tomorrow)
    {
        try
        {
            var inv = CultureInfo.InvariantCulture;
            using var geo = JsonDocument.Parse(await http.Value.GetStringAsync(
                "https://geocoding-api.open-meteo.com/v1/search?count=1&language=es&name=" + Uri.EscapeDataString(city)));
            if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var place = results[0];
            string name = place.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? city : city;
            if (!place.TryGetProperty("latitude", out var lat) || !place.TryGetProperty("longitude", out var lon)) return null;
            string url = "https://api.open-meteo.com/v1/forecast?timezone=auto&forecast_days=2"
                         + $"&latitude={lat.GetDouble().ToString("0.####", inv)}"
                         + $"&longitude={lon.GetDouble().ToString("0.####", inv)}"
                         + "&current=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max";
            using var doc = JsonDocument.Parse(await http.Value.GetStringAsync(url));

            if (!doc.RootElement.TryGetProperty("daily", out var daily)) return null;
            int day = tomorrow ? 1 : 0;
            if (!daily.TryGetProperty("temperature_2m_max", out var maxEl) || maxEl.GetArrayLength() <= day ||
                !daily.TryGetProperty("temperature_2m_min", out var minEl) || minEl.GetArrayLength() <= day) return null;
            double max = maxEl[day].GetDouble(), min = minEl[day].GetDouble();
            string rain = "";
            if (daily.TryGetProperty("precipitation_probability_max", out var chanceEl) && chanceEl.GetArrayLength() > day)
            {
                var chance = chanceEl[day];
                rain = chance.ValueKind == JsonValueKind.Number ? $", con un {chance.GetDouble():0} por ciento de probabilidad de lluvia" : "";
            }
            int Code(string field)
            {
                if (!daily.TryGetProperty(field, out var codes) || codes.GetArrayLength() <= day || codes[day].ValueKind != JsonValueKind.Number) return -1;
                return codes[day].GetInt32();
            }

            if (tomorrow)
                return $"Mañana en {name} {Sky(Code("weather_code"), future: true)}, entre {min:0} y {max:0} grados{rain}.";
            string nowPart = "";
            if (doc.RootElement.TryGetProperty("current", out var now) && now.TryGetProperty("temperature_2m", out var temp))
                nowPart = $" hay {temp.GetDouble():0} grados y {Sky(now.TryGetProperty("weather_code", out var wc) && wc.ValueKind == JsonValueKind.Number ? wc.GetInt32() : -1, future: false)}.";
            return nowPart.Length > 0
                ? $"En {name}{nowPart} Hoy va de {min:0} a {max:0} grados{rain}."
                : $"En {name} hoy va de {min:0} a {max:0} grados{rain}.";

            // Códigos meteorológicos de la OMM, que es lo que devuelve el servicio.
            static string Sky(int code, bool future) => code switch
            {
                0 => future ? "estará despejado" : "está despejado",
                1 or 2 => future ? "habrá pocas nubes" : "hay pocas nubes",
                3 => future ? "estará nublado" : "está nublado",
                45 or 48 => future ? "habrá niebla" : "hay niebla",
                >= 51 and <= 57 => future ? "lloviznará" : "llovizna",
                (>= 61 and <= 67) or (>= 80 and <= 82) => future ? "lloverá" : "llueve",
                (>= 71 and <= 77) or 85 or 86 => future ? "nevará" : "nieva",
                >= 95 => future ? "habrá tormenta" : "hay tormenta",
                _ => future ? "el cielo estará variable" : "el cielo está variable",
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException or IndexOutOfRangeException)
        {
            Log.Write($"El clima no se pudo leer: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> Research(string query)
    {
        if (query.Trim().Length == 0) return "Falta la consulta.";
        var found = await Wikipedia(query);
        return found.Count > 0
            ? string.Join("\n\n", found.Select(f => $"{f.Title}: {f.Extract}"))
            : "No encontré nada sobre eso en Wikipedia.";
    }
}
