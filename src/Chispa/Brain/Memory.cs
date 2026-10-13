using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chispa;

internal sealed class Fact
{
    public string Texto { get; set; } = "";
    public DateTime Fecha { get; set; } = DateTime.Now;

    /// <summary>Hasta cuándo valió. Vacío = sigue siendo cierto. Un dato corregido no se borra: se cierra.</summary>
    public DateTime? Hasta { get; set; }
}

/// <summary>
/// Lo que ha aprendido del usuario, en memoria.json. Se lee del disco solo cuando hace falta
/// y se suelta al dormir; a la IA se le pasan únicamente los recuerdos que vienen al caso.
/// </summary>
internal sealed class Memory
{
    private const int MaxFacts = 5000;   // de cada frase solo viajan al modelo los que vienen al caso (ver Relevant)
    private readonly object gate = new();
    private List<Fact>? facts;

    private static string FilePath => Path.Combine(Config.DataDir, "memoria.json");

    /// <summary>Lo que sabe ahora: los datos que siguen valiendo.</summary>
    public IReadOnlyList<Fact> All => Loaded.Where(f => f.Hasta == null).ToList();

    /// <summary>Lo que supo y dejó de valer («tu color favorito era el verde»), del más reciente al más antiguo.</summary>
    public IReadOnlyList<Fact> Past => Loaded.Where(f => f.Hasta != null).OrderByDescending(f => f.Hasta).ToList();

    private List<Fact> Loaded
    {
        get
        {
            lock (gate)
            {
                if (facts != null) return facts;
                try
                {
                    if (File.Exists(FilePath))
                        facts = JsonSerializer.Deserialize<List<Fact>>(File.ReadAllText(FilePath), Config.Json);
                }
                catch (Exception ex) { Log.Write($"memoria.json ilegible: {ex.Message}"); }
                return facts ??= [];
            }
        }
    }

    /// <summary>Guarda un dato nuevo. Devuelve false si ya lo sabía.</summary>
    public bool Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        string key = Reflexes.Normalize(text);
        lock (gate)
        {
            if (All.Any(f => Reflexes.Normalize(f.Texto) == key)) return false;
            Loaded.Add(new Fact { Texto = text });
            if (Loaded.Count > MaxFacts) Loaded.RemoveRange(0, Loaded.Count - MaxFacts);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Guarda un dato que sustituye a lo que supiera sobre el mismo asunto: tras "mi color favorito es el azul",
    /// "mi color favorito es el verde" lo corrige en vez de acumularse. Devuelve true si corrigió algo.
    /// </summary>
    public bool Replace(string subject, string text)
    {
        lock (gate)
        {
            string key = Reflexes.Normalize(subject);
            // Lo anterior no se borra: se cierra con la fecha de hoy, para poder decir qué valía antes y hasta cuándo.
            var outdated = Loaded.Where(f => f.Hasta == null && Reflexes.Normalize(f.Texto).StartsWith(key, StringComparison.Ordinal)
                                             && Reflexes.Normalize(f.Texto) != Reflexes.Normalize(text)).ToList();
            foreach (var fact in outdated) fact.Hasta = DateTime.Now;
            bool added = Add(text);
            if (outdated.Count > 0 && !added) Save();
            return outdated.Count > 0;
        }
    }

    /// <summary>Cierra un recuerdo (deja de valer) sin borrarlo, para poder decir qué valía antes y hasta cuándo.</summary>
    public void Close(Fact fact)
    {
        lock (gate)
        {
            if (fact.Hasta != null) return;
            fact.Hasta = DateTime.Now;
            Save();
        }
    }

    /// <summary>El recuerdo que mejor responde a una pregunta, o null si ninguno tiene que ver.</summary>
    public string? Answer(string question)
    {
        var asked = Stems(question);
        if (asked.Count == 0) return null;
        // "¿Cómo se llama mi perro?" pregunta por el perro: un recuerdo que no lo nombre no es la respuesta.
        var owned = Regex.Matches(Reflexes.Normalize(question), @"\bmis? ([a-z0-9]+)").Select(m => Stem(m.Groups[1].Value)).ToList();
        string? best = null;
        int bestScore = 0;
        foreach (var fact in All)   // a igual puntuación gana el más reciente
        {
            var stems = Stems(fact.Texto);
            if (!owned.All(stems.Contains)) continue;
            int score = stems.Count(asked.Contains);
            if (score > 0 && score >= bestScore) (best, bestScore) = (fact.Texto, score);
        }
        return best;
    }

    /// <summary>Olvida los recuerdos que mencionan el texto. Devuelve cuántos borró.</summary>
    public int Forget(string about)
    {
        var words = Words(about);
        if (words.Count == 0) return 0;
        lock (gate)
        {
            int removed = Loaded.RemoveAll(f => words.All(w => Words(f.Texto).Contains(w)));
            if (removed > 0) Save();
            return removed;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Loaded.Clear();
            Save();
        }
    }

    /// <summary>Los recuerdos más útiles para esta frase: los que comparten palabras con ella y los más recientes.</summary>
    public List<string> Relevant(string query, int max = 10)
    {
        var all = All;
        if (all.Count <= max) return all.Select(f => f.Texto).ToList();
        var words = Words(query);
        return all
            .Select((f, i) => (f.Texto, Score: Words(f.Texto).Count(words.Contains) * 10.0 + (double)i / all.Count))
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => x.Texto)
            .ToList();
    }

    /// <summary>Suelta la copia en RAM; el archivo sigue en disco.</summary>
    public void Unload()
    {
        lock (gate) facts = null;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Loaded, Config.Json));
        }
        catch (Exception ex) { Log.Write($"No pude guardar memoria.json: {ex.Message}"); }
    }

    private static HashSet<string> Words(string text) =>
        Reflexes.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToHashSet();

    private static readonly HashSet<string> filler =
    [
        "que", "cual", "cuales", "como", "quien", "donde", "cuando", "cuanto", "cuantos", "cuantas", "por", "para", "con",
        "es", "son", "era", "fue", "esta", "estan", "hay", "mi", "mis", "me", "yo", "tu", "tus", "te", "se", "le", "su", "sus",
        "el", "la", "los", "las", "lo", "un", "una", "unos", "unas", "de", "del", "al", "en", "sabes", "recuerdas",
        "acuerdas", "dime", "dije", "dijiste", "mas", "muy", "eso", "esto", "ese", "esa",
    ];

    /// <summary>Raíces de las palabras con contenido: "llamo" y "llama" cuentan como la misma.</summary>
    private static HashSet<string> Stems(string text) =>
        Reflexes.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !filler.Contains(w))
            .Select(Stem)
            .ToHashSet();

    private static string Stem(string word) => word switch
    {
        "nombre" or "nombres" => "llam",                    // "¿cuál es mi nombre?" ↔ "me llamo…"
        "cumpleanos" or "naci" or "nacimiento" => "cump",   // "¿cuándo es mi cumpleaños?" ↔ "nací el…"
        _ => word.Length <= 3 ? word : word[..Math.Min(word.Length - 1, 4)],
    };
}
