using System.Text.Encodings.Web;
using System.Text.Json;

namespace Chispa;

/// <summary>Aspecto visible de la mascota.</summary>
internal sealed class Look
{
    public double Tamano { get; set; } = 0.8;

}

/// <summary>Preferencias guardadas en config.json (se puede editar a mano).</summary>
internal sealed class Config
{
    private const int CurrentVersion = 5;

    /// <summary>Versión del formato del archivo; permite poner al día valores antiguos al cargarlo.</summary>
    public int Version { get; set; }

    /// <summary>Nombre de la mascota; también es la palabra que la despierta.</summary>
    public string Nombre { get; set; } = "Chispa";

    // Voz
    public string Voz { get; set; } = "";              // vacío = la natural si está instalada; "windows" = la clásica
    public int VelocidadVoz { get; set; } = 1;         // -10 … 10
    public double TonoVoz { get; set; } = 1.0;         // solo voz natural: más de 1 = más aguda
    public int PasosVoz { get; set; } = 6;             // voz Supertonic: más pasos = algo más de calidad y más espera
    public bool CacheVoz { get; set; }                 // voz Supertonic: guarda en disco las frases cortas ya dichas (salen datos de lo que contesta; por defecto no)
    public bool Silencio { get; set; }                 // true = solo escribe en el globo

    // Oído
    public bool EscuchaContinua { get; set; } = true;  // false = micrófono apagado hasta pulsar el atajo o hacer clic
    public double UmbralComando { get; set; } = 0.55;
    public double UmbralNombre { get; set; } = 0.5;
    public double UmbralDictado { get; set; } = 0.3;
    public int SegundosAtenta { get; set; } = 8;       // tras responder, sigue escuchando sin que digas su nombre
    public int SegundosConversacion { get; set; } = 45; // lo mismo en modo conversación ("Chispa, hablemos")
    public int DormirTrasSegundos { get; set; } = 90;
    public string Atajo { get; set; } = "Ctrl+Alt+C";  // la llama desde cualquier ventana, sin micrófono

    // Seguridad. Abrir, cerrar, volumen y música se hacen en el acto si tú lo pediste con tus palabras.
    // Escribir en una ventana, o cualquier cambio que se le ocurra al modelo sin pedírselo, espera a "<nombre>, <frase>".
    public string FraseConfirmacion { get; set; } = "confirmo";
    public List<string> SinConfirmar { get; set; } = [];   // herramientas que nunca piden confirmación, p. ej. "escribir_texto"
    public bool ConfirmarTodo { get; set; }                // true = como antes: todo cambio en el equipo pide confirmación

    // Cerebro. "auto" usa el modelo local (Ollama) si está disponible y, si no, sigue con el propio.
    // "propio" no usa ningún modelo. "ollama" y "claude" lo fijan. El modelo solo recibe lo que su cerebro propio no sabe.
    public string Cerebro { get; set; } = "auto";
    public bool Guardian { get; set; } = true;         // revisa lo que un modelo grande va a decir antes de decirlo
    public string ModeloClaude { get; set; } = "claude-opus-5-5";
    public string ModeloOllama { get; set; } = "";     // vacío = primer modelo instalado
    public string Pensar { get; set; } = "auto";       // modelo local: "auto" = piensa solo lo que parece un problema; "siempre" (más acierto, más espera); "nunca"
    public string UrlOllama { get; set; } = "http://localhost:11434";
    public bool RespuestaRapida { get; set; } = true;
    public int LimiteRespuestaSegundos { get; set; } = 45;
    public int MaximoTokensRespuesta { get; set; } = 384;
    public int ContextoModelo { get; set; } = 4096;

    public Look Aspecto { get; set; } = new();

    /// <summary>Respirar y mecerse entre gestos; se puede apagar para trabajar con menos animación.</summary>
    public bool AnimacionReposo { get; set; } = true;

    public CompanionPreferences Acompanamiento { get; set; } = new();
    public UserProfile Perfil { get; set; } = new();

    /// <summary>Dónde estaba al cerrarla (píxeles desde la izquierda), para aparecer en el mismo sitio.</summary>
    public double? PosX { get; set; }

    /// <summary>Apunta en chispa.log cada cosa que oye y cómo la interpretó (para ajustar los umbrales).</summary>
    public bool Depurar { get; set; }

    /// <summary>Actualizaciones: repositorio de GitHub («dueño/repositorio»). Vacío = no mirar releases.</summary>
    public string Actualizaciones { get; set; } = "";

    public static string DataDir =>
        Environment.GetEnvironmentVariable("CHISPA_DATOS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chispa");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string FilePath => Path.Combine(DataDir, "config.json");

    public static bool Exists => File.Exists(FilePath);

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Json) ?? new Config();
                cfg.Aspecto ??= new();
                cfg.Aspecto.Tamano = double.IsFinite(cfg.Aspecto.Tamano) ? Math.Clamp(cfg.Aspecto.Tamano, .35, 2.2) : .8;
                cfg.Acompanamiento ??= new();
                cfg.Perfil ??= new();
                if (cfg.Version < CurrentVersion)
                {
                    // v3: el modelo local pasa a ser lo normal. Quien tenía el cerebro propio de serie pasa a "auto",
                    // que usa el modelo si está y, si no, se queda como estaba. Se puede volver en el menú > Cerebro.
                    if (cfg.Version < 3 && cfg.Cerebro is "propio" or "ninguno" or "") cfg.Cerebro = "auto";
                    // v5: solo se conserva el tamaño del avatar actual. Los campos retirados se ignoran.
                    cfg.Version = CurrentVersion;
                    cfg.Save();
                }
                return cfg;
            }
        }
        catch (Exception ex) { Log.Write($"config.json ilegible, uso valores por defecto: {ex.Message}"); }
        return new Config { Version = CurrentVersion };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write($"No pude guardar config.json: {ex.Message}"); }
    }
}

internal static class Log
{
    private static readonly object gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Config.DataDir);
                var path = Path.Combine(Config.DataDir, "chispa.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* el registro nunca debe tumbar la app */ }
    }
}
