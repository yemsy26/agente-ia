using System.Text;
using System.Text.Json;

namespace Chispa;

/// <summary>
/// El índice del diario: qué palabra (por su raíz) sale en qué línea de qué mes, guardado en diario/indice.json.
/// El diario nunca se reescribe, solo crece por el final, así que cada mes apunta cuántas líneas y cuántos bytes
/// llevaba indexados la última vez y solo se repasan las novedades. Si el archivo desaparece, se estropea o un mes
/// se recorta a mano, se rehace solo al usarlo; y sin él, la búsqueda sigue funcionando por barrido.
/// </summary>
internal sealed class JournalIndex
{
    private sealed class MesDatos
    {
        public int Lineas { get; set; }
        public long Bytes { get; set; }
    }

    private sealed class MesPalabras
    {
        public List<int> Lineas { get; set; } = [];
        public Dictionary<string, int> Nombres { get; set; } = [];   // palabra → veces dicha así en ese mes
    }

    private sealed class Data
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, MesDatos> Meses { get; set; } = [];
        public Dictionary<string, Dictionary<string, MesPalabras>> Palabras { get; set; } = [];   // raíz → mes → líneas y nombres
    }

    private static readonly JsonSerializerOptions line = new(Config.Json) { WriteIndented = false };

    private readonly object gate = new();
    private Data? data;

    private static string FilePath => Path.Combine(Journal.Dir, "indice.json");

    private Data LoadedUnsafe()
    {
        if (data != null) return data;
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Config.Json) is { } saved)
                data = saved;
        }
        catch (Exception ex) { Log.Write($"indice.json ilegible: {ex.Message}"); }
        return data ??= new Data();
    }

    /// <summary>Pone al día el índice (todo el diario, o un solo mes) con lo que haya en disco.</summary>
    public void Ensure(DateTime? month = null)
    {
        lock (gate)
        {
            Data d = LoadedUnsafe();
            if (!Directory.Exists(Journal.Dir))
            {
                if (d.Meses.Count > 0) { d.Meses.Clear(); d.Palabras.Clear(); SaveLocked(d); }
                return;
            }
            IEnumerable<string> files = Directory.EnumerateFiles(Journal.Dir, "*.jsonl");
            if (month != null)
            {
                string one = Path.Combine(Journal.Dir, month.Value.ToString("yyyy-MM") + ".jsonl");
                files = File.Exists(one) ? [one] : [];
            }
            bool changed = false;
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                var info = new FileInfo(file);
                var known = d.Meses.TryGetValue(name, out var k) ? k : new MesDatos();
                if (info.Length == known.Bytes) { d.Meses[name] = known; continue; }   // nada nuevo desde la última vez
                // Algo cambió fuera: se repasa el mes entero si lo recortaron, o solo las líneas nuevas si creció.
                int lines = CountLines(file);
                if (lines < known.Lineas) { DropMonthLocked(d, name); known = new MesDatos(); }
                if (lines > known.Lineas) ScanLinesLocked(file, name, known.Lineas + 1, lines);
                d.Meses[name] = new MesDatos { Lineas = lines, Bytes = info.Length };
                changed = true;
            }
            foreach (string name in d.Meses.Keys.Where(n => !File.Exists(Path.Combine(Journal.Dir, n + ".jsonl"))).ToList())
            { DropMonthLocked(d, name); changed = true; }
            if (changed) SaveLocked(d);
        }
    }

    /// <summary>Apuntala línea recién añadida al mes (el diario la acaba de escribir al final).</summary>
    public void Index(string month, int lineNo, string text)
    {
        lock (gate)
        {
            Data d = LoadedUnsafe();
            IndexLineLocked(d, month, lineNo, text);
            d.Meses[month] = new MesDatos { Lineas = lineNo, Bytes = new FileInfo(Path.Combine(Journal.Dir, month + ".jsonl")).Length };
            SaveLocked(d);
        }
    }

    /// <summary>Suelta todo lo que tiene en memoria (el diario entero acaba de borrarse).</summary>
    public void Clear()
    {
        lock (gate) data = null;
    }

    public int Lines(string month)
    {
        lock (gate) return LoadedUnsafe().Meses.TryGetValue(month, out var m) ? m.Lineas : 0;
    }

    /// <summary>Cuántas entradas hay en total entre dos meses (inclusive), por nombre «yyyy-MM».</summary>
    public int RangeLines(string firstMonth, string lastMonth)
    {
        lock (gate)
            return LoadedUnsafe().Meses
                .Where(kv => InRange(kv.Key, firstMonth, lastMonth))
                .Sum(kv => kv.Value.Lineas);
    }

    /// <summary>Las líneas que mencionan alguna de las raíces, en el tramo de meses, ordenadas.</summary>
    public List<(string Month, int Line)> Candidates(IEnumerable<string> stems, string firstMonth, string lastMonth)
    {
        lock (gate)
        {
            Data d = LoadedUnsafe();
            var hits = new HashSet<(string, int)>();
            foreach (string stem in stems)
            {
                if (!d.Palabras.TryGetValue(stem, out var byMonth)) continue;
                foreach (var (month, posting) in byMonth)
                {
                    if (!InRange(month, firstMonth, lastMonth)) continue;
                    foreach (int n in posting.Lineas) hits.Add((month, n));
                }
            }
            return hits.OrderBy(h => h.Item1).ThenBy(h => h.Item2).ToList();
        }
    }

    /// <summary>En cuántas entradas del tramo sale una raíz.</summary>
    public int DocFrequency(string stem, string firstMonth, string lastMonth)
    {
        lock (gate)
        {
            if (!LoadedUnsafe().Palabras.TryGetValue(stem, out var byMonth)) return 0;
            return byMonth.Where(kv => InRange(kv.Key, firstMonth, lastMonth)).Sum(kv => kv.Value.Lineas.Count);
        }
    }

    /// <summary>Raíz → en cuántas entradas del tramo aparece.</summary>
    public Dictionary<string, int> StemCounts(string firstMonth, string lastMonth)
    {
        lock (gate)
        {
            var counts = new Dictionary<string, int>();
            foreach (var (stem, byMonth) in LoadedUnsafe().Palabras)
            {
                int n = byMonth.Where(kv => InRange(kv.Key, firstMonth, lastMonth)).Sum(kv => kv.Value.Lineas.Count);
                if (n > 0) counts[stem] = n;
            }
            return counts;
        }
    }

    /// <summary>La palabra tal como se dijo más veces para una raíz («rueda» → «ruedas»), o la raíz misma.</summary>
    public string Word(string stem)
    {
        lock (gate)
        {
            if (!LoadedUnsafe().Palabras.TryGetValue(stem, out var byMonth)) return stem;
            return byMonth.SelectMany(kv => kv.Value.Nombres)
                .GroupBy(kv => kv.Key)
                .OrderByDescending(g => g.Sum(kv => kv.Value))
                .Select(g => g.Key)
                .FirstOrDefault() ?? stem;
        }
    }

    private static bool InRange(string month, string firstMonth, string lastMonth) =>
        string.CompareOrdinal(month, firstMonth) >= 0 && string.CompareOrdinal(month, lastMonth) <= 0;

    private static int CountLines(string file)
    {
        int n = 0;
        foreach (string _ in File.ReadLines(file, Encoding.UTF8)) n++;
        return n;
    }

    /// <summary>Indexa las líneas [from..to] de un mes (ya leídas hasta from-1).</summary>
    private void ScanLinesLocked(string file, string month, int from, int to)
    {
        Data d = LoadedUnsafe();
        int n = 0;
        foreach (string text in File.ReadLines(file, Encoding.UTF8))
        {
            n++;
            if (n < from) continue;
            if (n > to) break;
            IndexLineLocked(d, month, n, text);
        }
    }

    private static void IndexLineLocked(Data d, string month, int lineNo, string json)
    {
        if (json.Length < 2) return;
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(json, line); }
        catch (JsonException) { return; }
        if (entry == null || entry.Dijo.Trim().Length == 0) return;
        var seen = new HashSet<string>();
        foreach (var (word, stem) in Journal.Words($"{entry.Dijo} {entry.Respondio}"))
        {
            if (!seen.Add(stem)) continue;   // una raíz cuenta una sola vez por entrada, como en el barrido
            if (!d.Palabras.TryGetValue(stem, out var byMonth)) d.Palabras[stem] = byMonth = [];
            if (!byMonth.TryGetValue(month, out var posting)) byMonth[month] = posting = new MesPalabras();
            posting.Lineas.Add(lineNo);
            posting.Nombres[word] = posting.Nombres.TryGetValue(word, out int c) ? c + 1 : 1;
        }
    }

    private static void DropMonthLocked(Data d, string month)
    {
        d.Meses.Remove(month);
        foreach (var stem in d.Palabras.Keys.ToList())
        {
            d.Palabras[stem].Remove(month);
            if (d.Palabras[stem].Count == 0) d.Palabras.Remove(stem);
        }
    }

    private void SaveLocked(Data d)
    {
        try
        {
            Directory.CreateDirectory(Journal.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d, line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"No pude guardar indice.json: {ex.Message}");
        }
    }
}
