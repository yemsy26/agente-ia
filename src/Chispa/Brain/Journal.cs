using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chispa;

/// <summary>Una entrada del diario: algo que el usuario dijo y lo que pasó a continuación.</summary>
internal sealed class Entry
{
    public DateTime Cuando { get; set; } = DateTime.Now;
    public string Dijo { get; set; } = "";
    public string Respondio { get; set; } = "";
}

/// <summary>
/// El diario: todo lo que se han dicho, con su fecha, para siempre. Un archivo de texto por mes en la carpeta
/// «diario» (una línea por intercambio) y un resumen por día en «resumenes». No se carga entero en memoria: se
/// leen los meses que hagan falta para cada pregunta. Es lo que permite «¿qué hablamos del proyecto en agosto?».
/// </summary>
internal sealed class Journal
{
    private static readonly CultureInfo es = CultureInfo.GetCultureInfo("es-ES");
    private static readonly JsonSerializerOptions line = new(Config.Json) { WriteIndented = false };
    private static readonly HashSet<string> filler =
    [
        "que", "cual", "cuales", "como", "quien", "donde", "cuando", "cuanto", "por", "para", "con", "sin", "sobre", "es", "son", "era", "fue",
        "esta", "estan", "hay", "mi", "mis", "me", "yo", "tu", "tus", "te", "se", "le", "su", "sus", "el", "la", "los", "las", "lo", "un", "una",
        "unos", "unas", "de", "del", "al", "en", "y", "o", "a", "ya", "no", "si", "mas", "muy", "eso", "esto", "ese", "esa", "dije", "dijiste",
        "hablamos", "hablado", "pedi", "pedido", "conte", "contado", "comente", "dijimos", "acuerdas", "recuerdas", "sabes", "dime", "ayer", "hoy",
        "semana", "pasada", "pasado", "mes", "dia", "dias", "hace", "otro", "otra", "vez", "algo", "cosa", "cosas",
    ];

    private readonly object gate = new();
    private readonly JournalIndex index = new();   // el índice incremental (diario/indice.json) que hace baratas las búsquedas

    internal static string Dir => Path.Combine(Config.DataDir, "diario");
    private static string SummaryDir => Path.Combine(Config.DataDir, "resumenes");
    private static string MonthFile(DateTime day) => Path.Combine(Dir, $"{day:yyyy-MM}.jsonl");
    private static string SummaryFile(DateTime day) => Path.Combine(SummaryDir, $"{day:yyyy-MM-dd}.txt");

    /// <summary>Apunta un intercambio. Nunca se reescribe lo anterior: solo se añade al final del mes.</summary>
    public void Add(string said, string replied, DateTime? when = null)
    {
        said = said.Trim();
        if (said.Length == 0) return;
        var entry = new Entry { Cuando = when ?? DateTime.Now, Dijo = said, Respondio = replied.Trim() };
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Dir);
                string month = entry.Cuando.ToString("yyyy-MM");
                index.Ensure(entry.Cuando);
                string json = JsonSerializer.Serialize(entry, line);
                File.AppendAllText(MonthFile(entry.Cuando), json + "\n", Encoding.UTF8);
                index.Index(month, index.Lines(month) + 1, json);   // el índice espera la línea tal cual se escribe
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"No pude apuntar en el diario: {ex.Message}"); }
    }

    /// <summary>Las entradas entre dos fechas, de la más antigua a la más nueva. Sin fechas: todas.</summary>
    public List<Entry> Read(DateTime? from = null, DateTime? to = null)
    {
        var entries = new List<Entry>();
        if (!Directory.Exists(Dir)) return entries;
        string first = from?.ToString("yyyy-MM") ?? "0000-00", last = to?.ToString("yyyy-MM") ?? "9999-99";
        foreach (string file in Directory.EnumerateFiles(Dir, "*.jsonl").OrderBy(f => f))
        {
            string month = Path.GetFileNameWithoutExtension(file);
            if (string.CompareOrdinal(month, first) < 0 || string.CompareOrdinal(month, last) > 0) continue;
            try
            {
                lock (gate)
                    foreach (string text in File.ReadLines(file, Encoding.UTF8))
                    {
                        if (text.Length < 2) continue;
                        try
                        {
                            if (JsonSerializer.Deserialize<Entry>(text, line) is { } entry && (from == null || entry.Cuando >= from) && (to == null || entry.Cuando < to))
                                entries.Add(entry);
                        }
                        catch (JsonException) { /* una línea estropeada no invalida el resto del mes */ }
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"No pude leer {Path.GetFileName(file)}: {ex.Message}"); }
        }
        return entries;
    }

    /// <summary>
    /// Lo que mejor responde a una búsqueda: las entradas que comparten más palabras con contenido, pesando más las
    /// palabras raras, y a igualdad, las más recientes. Con fechas, solo dentro de ellas (y sin palabras, las últimas).
    /// </summary>
    /// <param name="minMatches">Cuántas palabras con contenido tiene que compartir una entrada para contar.</param>
    public List<Entry> Search(string query, DateTime? from = null, DateTime? to = null, int max = 6, int minMatches = 1)
    {
        string firstMonth = from?.ToString("yyyy-MM") ?? "0000-00", lastMonth = to?.ToString("yyyy-MM") ?? "9999-99";
        var wanted = Stems(query);
        if (wanted.Count == 0)
        {
            var entries = Read(from, to);
            return minMatches > 1 ? [] : entries.TakeLast(max).ToList();
        }
        try
        {
            return SearchByIndex(wanted, firstMonth, lastMonth, max, minMatches, from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write($"Índice del diario inutilizable: {ex.Message}");
        }
        return SearchByScan(wanted, max, minMatches, from, to);   // sin índice, el barrido de siempre
    }

    /// <summary>La búsqueda con el índice: solo se leen las líneas que mencionan las palabras pedidas.</summary>
    private List<Entry> SearchByIndex(HashSet<string> wanted, string firstMonth, string lastMonth,
                                      int max, int minMatches, DateTime? from, DateTime? to)
    {
        index.Ensure();
        var candidates = index.Candidates(wanted, firstMonth, lastMonth);
        if (candidates.Count == 0) return [];
        int total = index.RangeLines(firstMonth, lastMonth);
        var hits = ReadLines(candidates, from, to);
        var stemSets = hits.Select(h => Stems($"{h.Entry.Dijo} {h.Entry.Respondio}")).ToList();
        var weight = wanted.ToDictionary(w => w, w => Math.Log(1 + total / (1.0 + stemSets.Count(s => s.Contains(w)))));
        return hits
            .Select((h, i) => (h.Entry, h.Order, Matches: wanted.Count(stemSets[i].Contains),
                               Score: wanted.Where(stemSets[i].Contains).Sum(w => weight[w])))
            .Where(x => x.Matches >= Math.Min(minMatches, wanted.Count))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Order)
            .Take(max)
            .OrderBy(x => x.Order)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>La búsqueda de siempre: recorre los meses y puntúa cada entrada (respaldo sin índice).</summary>
    private List<Entry> SearchByScan(HashSet<string> wanted, int max, int minMatches, DateTime? from, DateTime? to)
    {
        var entries = Read(from, to);
        if (entries.Count == 0) return entries;
        var stems = entries.Select(e => Stems($"{e.Dijo} {e.Respondio}")).ToList();
        // Una palabra que sale en casi todo no distingue nada; una que sale en dos entradas, mucho.
        var weight = wanted.ToDictionary(w => w, w => Math.Log(1 + entries.Count / (1.0 + stems.Count(s => s.Contains(w)))));
        return entries
            .Select((e, i) => (Entry: e, Score: wanted.Where(stems[i].Contains).Sum(w => weight[w]), Matches: wanted.Count(stems[i].Contains), Order: i))
            .Where(x => x.Matches >= Math.Min(minMatches, wanted.Count))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Order)
            .Take(max)
            .OrderBy(x => x.Order)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>Lee solo las líneas pedidas de cada mes y devuelve las entradas, con su posición para ordenarlas.</summary>
    private List<(Entry Entry, string Order)> ReadLines(List<(string Month, int Line)> candidates, DateTime? from, DateTime? to)
    {
        var byMonth = candidates.GroupBy(c => c.Month).ToDictionary(g => g.Key, g => g.Select(c => c.Line).ToHashSet());
        var found = new List<(Entry, string)>();
        foreach (var (month, lines) in byMonth.OrderBy(kv => kv.Key))
        {
            string file = Path.Combine(Dir, month + ".jsonl");
            int n = 0;
            try
            {
                foreach (string text in File.ReadLines(file, Encoding.UTF8))
                {
                    n++;
                    if (!lines.Contains(n) || text.Length < 2) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<Entry>(text, line) is { } entry && (from == null || entry.Cuando >= from) && (to == null || entry.Cuando < to))
                            found.Add((entry, $"{month}:{n:D8}"));
                    }
                    catch (JsonException) { /* una línea estropeada no invalida el resto del mes */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"No pude leer {month}.jsonl: {ex.Message}"); }
        }
        return found;
    }

    /// <summary>De qué se ha hablado más en un tramo: los temas repetidos, con en cuántas entradas aparecen.</summary>
    public List<(string Tema, int Veces)> Topics(DateTime? from = null, DateTime? to = null, int max = 6)
    {
        string firstMonth = from?.ToString("yyyy-MM") ?? "0000-00", lastMonth = to?.ToString("yyyy-MM") ?? "9999-99";
        index.Ensure();
        int total = index.RangeLines(firstMonth, lastMonth);
        return index.StemCounts(firstMonth, lastMonth)
            .Where(kv => total == 0 || kv.Value * 2 <= total)   // una palabra que sale en casi todo no es un tema
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => index.DocFrequency(kv.Key, "0000-00", "9999-99"))   // a igualdad, el tema más específico
            .Take(max)
            .Select(kv => (index.Word(kv.Key), kv.Value))
            .ToList();
    }

    /// <summary>Cuántas entradas del tramo mencionan algo de la frase (al menos una palabra con contenido).</summary>
    public int Count(string query, DateTime? from = null, DateTime? to = null)
    {
        string firstMonth = from?.ToString("yyyy-MM") ?? "0000-00", lastMonth = to?.ToString("yyyy-MM") ?? "9999-99";
        var wanted = Stems(query);
        if (wanted.Count == 0) return 0;
        index.Ensure();
        return ReadLines(index.Candidates(wanted, firstMonth, lastMonth), from, to).Count;
    }

    public string? Summary(DateTime day)
    {
        try { return File.Exists(SummaryFile(day)) ? File.ReadAllText(SummaryFile(day), Encoding.UTF8).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void SaveSummary(DateTime day, string text)
    {
        try
        {
            Directory.CreateDirectory(SummaryDir);
            File.WriteAllText(SummaryFile(day), text.Trim() + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"No pude guardar el resumen del día: {ex.Message}"); }
    }

    /// <summary>Los días con conversación que aún no tienen resumen (o lo tienen de antes de la última entrada).</summary>
    public List<DateTime> DaysToSummarize(int lookBackDays = 14)
    {
        var since = DateTime.Today.AddDays(-lookBackDays);
        return Read(since).GroupBy(e => e.Cuando.Date)
            .Where(day => !File.Exists(SummaryFile(day.Key)) || File.GetLastWriteTime(SummaryFile(day.Key)) < day.Max(e => e.Cuando))
            .Select(day => day.Key).OrderBy(d => d).ToList();
    }

    /// <summary>Los resúmenes de los últimos días que lo tengan, del más antiguo al más reciente.</summary>
    public List<(DateTime Day, string Text)> RecentSummaries(int count = 3)
    {
        if (!Directory.Exists(SummaryDir)) return [];
        return Directory.EnumerateFiles(SummaryDir, "*.txt").OrderByDescending(f => f).Take(count)
            .Select(f => (Ok: DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day), Day: day))
            .Where(x => x.Ok && Summary(x.Day) is { Length: > 0 })
            .Select(x => (x.Day, Summary(x.Day)!)).OrderBy(x => x.Day).ToList();
    }

    /// <summary>Borra el diario y los resúmenes enteros (solo cuando el usuario pide olvidarlo todo).</summary>
    public void Clear()
    {
        foreach (string dir in new[] { Dir, SummaryDir })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"No pude borrar {dir}: {ex.Message}"); }
        }
        index.Clear();   // el índice en memoria se olvida junto con el diario
    }

    // ---------- fechas dichas de palabra ----------

    private static readonly string[] months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
    private static readonly string[] weekdays = ["domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado"];
    private static readonly Regex ago = new(@"\bhace (un|una|unos|unas|varios|varias|algunos|algunas|un par de|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|\d+) (dia|dias|semana|semanas|mes|meses|ano|anos)\b");

    /// <summary>¿Habla la frase de algo ya dicho o de otro día? Entonces merece mirar el diario aunque coincida poco.</summary>
    public static bool AsksAboutPast(string text) => pastCue.IsMatch(Reflexes.Normalize(text));

    private static readonly Regex pastCue = new(
        @"\b(?:te dije|te conte|te pedi|te comente|te hable|hablamos|dijimos|quedamos|recuerdas|te acuerdas|ayer|anteayer|anoche|la otra vez|el otro dia|semana pasada|mes pasado|ano pasado)\b|\bhace [a-z0-9 ]{1,12}(?:dias?|semanas?|mes|meses|anos?)\b");

    /// <summary>¿Pregunta de qué se ha hablado, sin buscar nada en concreto? Entonces toca listar temas, no entradas sueltas.</summary>
    public static bool AsksTopics(string text) => topicsCue.IsMatch(Reflexes.Normalize(text));

    private static readonly Regex topicsCue = new(
        @"\bde que (?:hemos|habeis|has|se ha) hablado\b|\bde que se hablo\b|\b(?:sobre que|de que) (?:hablamos|tratamos|hemos hablado|hemos tratado)\b|\b(?:que|cuales|los) temas\b");

    /// <summary>
    /// ¿Pregunta cuántas veces se ha hablado de algo? Devuelve de qué («» si no concreta), o null si no es una
    /// pregunta de recuento.
    /// </summary>
    public static string? CountTopic(string text) =>
        countAsk.Match(Reflexes.Normalize(text)) is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1].Value.Trim() : "") : null;

    private static readonly Regex countAsk = new(
        @"^(?:cuantas veces|cuanto|cuantos)\b.+?\b(?:te (?:he|hable|comente|conte|dije)|hablamos|hemos hablado|tratamos)\b(?:.*?\b(?:de|sobre|del)\s+(.+))?$");

    /// <summary>
    /// De qué fechas habla una frase: «ayer», «el lunes», «la semana pasada», «en agosto», «hace tres semanas», «2026-09».
    /// Devuelve el tramo [desde, hasta) y cómo llamarlo, o null si no nombra ninguna fecha.
    /// </summary>
    public static (DateTime From, DateTime To, string Label)? When(string text, DateTime? nowOverride = null)
    {
        string norm = Reflexes.Normalize(text);
        DateTime today = (nowOverride ?? DateTime.Now).Date;
        bool Has(string phrase) => $" {norm} ".Contains($" {phrase} ");

        if (Has("anteayer") || Has("antes de ayer")) return (today.AddDays(-2), today.AddDays(-1), "anteayer");
        if (Has("ayer")) return (today.AddDays(-1), today, "ayer");
        if (Has("hoy") || Has("esta manana") || Has("esta tarde")) return (today, today.AddDays(1), "hoy");
        if (Has("anoche")) return (today.AddDays(-1).AddHours(18), today.AddHours(5), "anoche");
        int monday = ((int)today.DayOfWeek + 6) % 7;
        if (Has("esta semana")) return (today.AddDays(-monday), today.AddDays(1), "esta semana");
        if (Has("semana pasada")) return (today.AddDays(-monday - 7), today.AddDays(-monday), "la semana pasada");
        if (Has("este mes")) return (new DateTime(today.Year, today.Month, 1), today.AddDays(1), "este mes");
        if (Has("mes pasado")) { var first = new DateTime(today.Year, today.Month, 1).AddMonths(-1); return (first, first.AddMonths(1), "el mes pasado"); }
        if (Has("este ano")) return (new DateTime(today.Year, 1, 1), today.AddDays(1), "este año");
        if (Has("ano pasado")) return (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year, 1, 1), "el año pasado");

        if (ago.Match(norm) is { Success: true } m)
        {
            string count = m.Groups[1].Value, unit = m.Groups[2].Value;
            int span = unit.StartsWith("dia") ? 1 : unit.StartsWith("semana") ? 7 : unit.StartsWith("mes") ? 30 : 365;
            // «hace unos meses» no dice cuántos: se mira de uno a seis.
            if (count is "unos" or "unas" or "varios" or "varias" or "algunos" or "algunas")
                return (today.AddDays(-6 * span), today.AddDays(-span + 1), m.Value);
            if (Numbers.TryParse(count is "un" or "una" ? "1" : count == "un par de" ? "2" : count, out double amount))
            {
                DateTime day = today.AddDays(-(int)amount * span);
                // «hace tres semanas» no es un día exacto: se mira alrededor.
                int slack = unit.StartsWith("dia") ? 0 : unit.StartsWith("semana") ? 3 : unit.StartsWith("mes") ? 15 : 180;
                return (day.AddDays(-slack), day.AddDays(slack + 1), m.Value);
            }
        }
        for (int i = 0; i < weekdays.Length; i++)
            if (Has(weekdays[i]))
            {
                int back = ((int)today.DayOfWeek - i + 7) % 7;
                if (back == 0) back = 7;   // «el lunes», dicho en lunes, es el de la semana pasada
                return (today.AddDays(-back), today.AddDays(-back + 1), $"el {today.AddDays(-back).ToString("dddd d 'de' MMMM", es)}");
            }
        for (int i = 0; i < months.Length; i++)
            if (Has(months[i]))
            {
                int year = Regex.Match(norm, @"\b(20\d\d)\b") is { Success: true } y ? int.Parse(y.Value) : i + 1 > today.Month ? today.Year - 1 : today.Year;
                var first = new DateTime(year, i + 1, 1);
                return (first, first.AddMonths(1), $"{months[i]} de {year}");
            }
        if (Regex.Match(norm, @"\b(20\d\d) (\d{1,2})\b") is { Success: true } ym && int.Parse(ym.Groups[2].Value) is >= 1 and <= 12)
        {
            var first = new DateTime(int.Parse(ym.Groups[1].Value), int.Parse(ym.Groups[2].Value), 1);
            return (first, first.AddMonths(1), first.ToString("MMMM 'de' yyyy", es));
        }
        return null;
    }

    /// <summary>Cómo contar una entrada: «el lunes 5 de octubre pediste… y contesté…».</summary>
    public static string Tell(Entry e, int maxReply = 160)
    {
        string reply = e.Respondio.Length > maxReply ? e.Respondio[..maxReply].TrimEnd() + "…" : e.Respondio;
        return $"[{e.Cuando.ToString("dddd d 'de' MMMM 'de' yyyy, HH:mm", es)}] Usuario: {e.Dijo}" + (reply.Length > 0 ? $" — Tú: {reply}" : "");
    }

    /// <summary>Las palabras con contenido de una frase, cada una con su raíz, sin repetir la misma palabra.</summary>
    internal static List<(string Word, string Stem)> Words(string text) =>
        Reflexes.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !filler.Contains(w))
            .Select(w => (Word: w, Stem: w.Length <= 4 ? w : w[..Math.Min(w.Length - 1, 5)]))
            .DistinctBy(p => p.Word)
            .ToList();

    private static HashSet<string> Stems(string text) => Words(text).Select(w => w.Stem).ToHashSet();
}
