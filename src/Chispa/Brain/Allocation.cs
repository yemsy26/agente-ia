using System.Text.RegularExpressions;

namespace Chispa;

/// <summary>
/// Repartir cosas (citas, tareas, personas) en huecos (días, horas, sitios) cumpliendo condiciones.
/// Las condiciones se escriben en frases cortas; el motor lógico da un reparto comprobado o demuestra
/// que no existe. Aquí no se adivina nada: una condición que no se entiende detiene el problema.
/// </summary>
internal sealed class Allocation
{
    private const int MaxNames = 10;

    private static readonly Regex leadingWords = new(@"^(?:el|la|los|las|al|del|un|una|mi|mis|ir a|ir al)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex listBreak = new(@"\s*,\s*(?:y\s+|e\s+)?|\s+y\s+|\s+e\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex negative = new(@"\b(?:no|nunca|ni|menos|excepto|salvo|tampoco|cierra|cierran|cerrad[oa]s?|ocupad[oa]s?|imposible|prohibid[oa]s?)\b");
    // Lo único que puede acompañar a los nombres en «X solo el lunes»: si aparece otra palabra, no se da por entendida.
    private static readonly HashSet<string> plainWords =
    [
        "el", "la", "los", "las", "un", "una", "en", "a", "al", "de", "del", "por", "para", "dia", "dias", "hora", "horas", "que", "y", "o", "u", "e",
        "es", "son", "sera", "seran", "esta", "estan", "solo", "solamente", "unicamente", "siempre", "tiene", "tienen", "debe", "deben", "ser", "ir",
        "va", "van", "puede", "pueden", "abre", "abren", "toca", "tocan", "queda", "quedan", "cae", "caen", "hay", "hacer", "poner", "pon",
    ];
    private static readonly Regex together = new(@"\b(?:mism[oa]s?|junt[oa]s|a la vez|coincid[a-z]*)\b");
    private static readonly Regex apart = new(@"\b(?:separad[oa]s|distint[oa]s?|diferentes?|aparte)\b");
    // «máximo dos por día», «una por hueco», «solo una reunión cada día»
    private static readonly Regex limit = new(@"\b(?:(?:maximo|como mucho|no mas de|hasta|solo|solamente|unicamente)(?: de)? )?([a-z0-9]+)(?: sol[oa])?(?: (?:cosa|cita|tarea|reunion|evento|persona)s?)? (?:por|en cada|cada|al)\b");
    // «no quiero dos el mismo día», «no más de dos en el mismo hueco»
    private static readonly Regex notSoMany = new(@"\bno (?:[a-z]+ ){0,3}?(mas de )?([a-z0-9]+)(?: (?:cosa|cita|tarea|reunion|evento|persona)s?)? (?:el|la|en el|en la|en un|en una|por|al) mism[oa]\b");
    private static readonly Regex noLimit = new(@"\b(?:pueden coincidir|sin limite|sin maximo|los que quepan|las que quepan)\b");

    private readonly string[] items, slots, itemKeys, slotKeys;
    private readonly List<(string Text, List<int[]> Clauses)> rules = [];
    private int? capacity;        // cuántas cosas admite un hueco; null = lo que toque por defecto
    private bool sharing;         // alguna condición pide que dos cosas vayan juntas

    private Allocation(string[] items, string[] slots)
    {
        (this.items, this.slots) = (items, slots);
        itemKeys = items.Select(n => Reflexes.Normalize(n)).ToArray();
        slotKeys = slots.Select(n => Reflexes.Normalize(n)).ToArray();
    }

    /// <summary>Parte «dentista, banco y taller» en sus nombres, sin artículos.</summary>
    public static string[] List(string text) =>
        listBreak.Split(text.Trim().TrimEnd('.', ';', ':', ','))
            .Select(Clean).Where(n => n.Length > 0).ToArray();

    private static string Clean(string name)
    {
        name = name.Trim().Trim('«', '»', '"', '\'', '.', ';', ':', ',').Trim();
        while (leadingWords.Match(name) is { Success: true } lead) name = name[lead.Length..];
        return name;
    }

    /// <summary>Plantea el problema, lo resuelve y devuelve el resultado en una frase.</summary>
    public static string Answer(IEnumerable<string> things, IEnumerable<string> places, IEnumerable<string> conditions, CancellationToken ct = default)
    {
        string[] items = things.Select(Clean).Where(n => n.Length > 0).ToArray();
        string[] slots = places.Select(Clean).Where(n => n.Length > 0).ToArray();
        if (items.Length < 2 || slots.Length < 2) return "Para repartir necesito al menos dos cosas y dos huecos.";
        if (items.Length > MaxNames || slots.Length > MaxNames) return $"Eso es mucho para mí: puedo repartir hasta {MaxNames} cosas en {MaxNames} huecos.";
        var problem = new Allocation(items, slots);
        if (problem.itemKeys.Concat(problem.slotKeys).Distinct().Count() != items.Length + slots.Length)
            return "Hay nombres repetidos, y así no sé cuál es cuál.";
        foreach (string condition in conditions.Select(c => c.Trim()).Where(c => c.Length > 0))
            if (!problem.Add(condition)) return $"No entendí la condición «{condition}», y prefiero no resolverlo a medias.";
        if (!Reason.Available) return "Para eso necesito mi motor de lógica y no lo encuentro.";
        return problem.Solve(ct) ?? "Mi motor de lógica no me contestó. Lo apunté en el registro.";
    }

    // ---------- de frases a lógica ----------

    private int Var(int item, int slot) => item * slots.Length + slot + 1;

    /// <summary>Las cosas o los huecos que nombra una frase, por orden de aparición.</summary>
    private static List<int> Mentions(string norm, string[] keys)
    {
        var found = new List<(int At, int Index)>();
        for (int i = 0; i < keys.Length; i++)
        {
            int at = Find(norm, keys[i]);
            // Si no está el nombre entero, vale una palabra suya que no comparta con ningún otro.
            if (at < 0)
                foreach (string word in keys[i].Split(' ').Where(w => w.Length >= 4))
                    if (keys.Count(k => k.Split(' ').Contains(word)) == 1 && (at = Find(norm, word)) >= 0) break;
            if (at >= 0) found.Add((at, i));
        }
        return found.OrderBy(f => f.At).Select(f => f.Index).ToList();

        static int Find(string text, string phrase) =>
            Regex.Match(text, $"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])") is { Success: true } m ? m.Index : -1;
    }

    private bool Add(string condition)
    {
        string norm = Reflexes.Normalize(condition);
        var its = Mentions(norm, itemKeys);
        var sls = Mentions(norm, slotKeys);
        var clauses = new List<int[]>();
        bool no = negative.IsMatch(norm);

        if (its.Count == 0 && sls.Count == 0)
        {
            if (noLimit.IsMatch(norm)) capacity = items.Length;
            else if (notSoMany.Match(norm) is { Success: true } many && Numbers.TryParse(many.Groups[2].Value, out double m) && m >= 2)
                capacity = many.Groups[1].Success ? (int)m : (int)m - 1;   // «no dos» deja una; «no más de dos» deja dos
            else if (limit.Match(norm) is { Success: true } max && Numbers.TryParse(max.Groups[1].Value, out double n) && n >= 1) capacity = (int)n;
            else return false;
            return true;
        }
        if (its.Count >= 2 && sls.Count == 0)
        {
            int a = its[0], b = its[1];
            bool before = norm.Contains("antes"), after = Regex.IsMatch(norm, @"\b(?:despues|luego)\b");
            if (before || after)
            {
                if (after) (a, b) = (b, a);   // «A después de B» es «B antes de A»
                bool just = Regex.IsMatch(norm, @"\b(?:justo|inmediatamente|seguid[oa])\b");
                for (int sa = 0; sa < slots.Length; sa++)
                    for (int sb = 0; sb < slots.Length; sb++)
                    {
                        bool allowed = no ? sa >= sb : just ? sb == sa + 1 : sa < sb;
                        if (!allowed) clauses.Add([-Var(a, sa), -Var(b, sb)]);
                    }
            }
            else if (apart.IsMatch(norm) || (together.IsMatch(norm) && no))
            {
                for (int x = 0; x < its.Count; x++)
                    for (int y = x + 1; y < its.Count; y++)
                        for (int s = 0; s < slots.Length; s++) clauses.Add([-Var(its[x], s), -Var(its[y], s)]);
            }
            else if (together.IsMatch(norm))
            {
                sharing = true;
                for (int x = 1; x < its.Count; x++)
                    for (int s = 0; s < slots.Length; s++)
                    {
                        clauses.Add([-Var(its[0], s), Var(its[x], s)]);
                        clauses.Add([Var(its[0], s), -Var(its[x], s)]);
                    }
            }
            else return false;
        }
        else if (its.Count >= 1 && sls.Count >= 1)
        {
            // «X no puede el lunes» lo prohíbe; «X solo el lunes» o «X el lunes o el martes» prohíbe los demás.
            // Con cualquier otra palabra por medio («X ya veremos el lunes») no se adivina cuál de las dos cosas es.
            var names = itemKeys.Concat(slotKeys).SelectMany(k => k.Split(' ')).ToHashSet();
            if (!no && norm.Split(' ').Any(w => !names.Contains(w) && !plainWords.Contains(w))) return false;
            foreach (int item in its)
                for (int s = 0; s < slots.Length; s++)
                    if (sls.Contains(s) == no) clauses.Add([-Var(item, s)]);
        }
        else return false;

        rules.Add((condition.Trim().TrimEnd('.', ';', ','), clauses));
        return true;
    }

    // ---------- resolver ----------

    private string? Solve(CancellationToken ct)
    {
        int n = items.Length, m = slots.Length;
        int cap = capacity ?? Math.Max(sharing ? 2 : 1, (n + m - 1) / m);
        var clauses = new List<int[]>();
        for (int i = 0; i < n; i++)
        {
            clauses.Add(Enumerable.Range(0, m).Select(s => Var(i, s)).ToArray());            // cada cosa, en algún hueco
            for (int a = 0; a < m; a++)
                for (int b = a + 1; b < m; b++) clauses.Add([-Var(i, a), -Var(i, b)]);       // y solo en uno
        }
        if (cap < n)
            foreach (int[] group in Groups(n, cap + 1))                                       // ningún hueco con más de las que admite
                for (int s = 0; s < m; s++) clauses.Add(group.Select(i => -Var(i, s)).ToArray());

        // Cada condición lleva un interruptor: así se puede preguntar qué pasaría sin ella.
        int[] switches = Enumerable.Range(n * m + 1, rules.Count).ToArray();
        for (int k = 0; k < rules.Count; k++)
            foreach (int[] clause in rules[k].Clauses) clauses.Add([-switches[k], .. clause]);

        if (Reason.Solve(clauses, [switches], ct: ct) is not { } first) return null;
        if (first[0] is { } model)
        {
            int[] chosen = model.Where(l => l > 0 && l <= n * m).ToArray();
            string plan = string.Join("; ", Enumerable.Range(0, m)
                .Select(s => (Slot: slots[s], Here: chosen.Where(l => (l - 1) % m == s).Select(l => items[(l - 1) / m]).ToList()))
                .Where(x => x.Here.Count > 0)
                .Select(x => $"{x.Slot}, {Join(x.Here, "y")}"));
            // ¿Hay otro reparto distinto que también lo cumpla todo?
            var again = Reason.Solve([.. clauses, chosen.Select(l => -l).ToArray()], [switches], ct: ct);
            return again == null ? $"Queda así: {plan}."
                : again[0] == null ? $"Queda así: {plan}. Es la única forma de cumplirlo todo."
                : $"Una forma que lo cumple todo: {plan}. Hay otras.";
        }

        // Imposible. ¿Bastaría con quitar una sola condición? Una consulta por condición, todas en un arranque.
        if (rules.Count == 0) return $"Eso es imposible: no caben {n} cosas en {m} huecos{(cap == 1 ? " de una en una" : $" con un máximo de {cap} por hueco")}.";
        var without = Enumerable.Range(0, rules.Count).Select(k => switches.Where((_, i) => i != k).ToArray()).ToList();
        without.Add([]);   // y sin ninguna condición: ¿cabían siquiera?
        if (Reason.Solve(clauses, without, ct: ct) is not { } tries) return null;
        if (tries[^1] == null) return $"Eso es imposible aunque quites todas las condiciones: no caben {n} cosas en {m} huecos{(cap == 1 ? " de una en una" : $" con un máximo de {cap} por hueco")}.";
        var culprits = Enumerable.Range(0, rules.Count).Where(k => tries[k] != null).Select(k => $"«{rules[k].Text}»").ToList();
        return "Eso es imposible: no hay forma de cumplir todas las condiciones a la vez. " +
               (culprits.Count == 0 ? "Chocan entre sí y no basta con quitar una sola."
                   : $"Si quitas {Join(culprits.Take(3).ToList(), "o")}, sí se puede.");
    }

    private static string Join(List<string> names, string last) =>
        names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} {last} {names[^1]}";

    /// <summary>Todos los grupos de <paramref name="size"/> elementos entre 0 y n-1.</summary>
    private static IEnumerable<int[]> Groups(int n, int size)
    {
        int[] pick = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return (int[])pick.Clone();
            int i = size - 1;
            while (i >= 0 && pick[i] == n - size + i) i--;
            if (i < 0) yield break;
            pick[i]++;
            for (int j = i + 1; j < size; j++) pick[j] = pick[j - 1] + 1;
        }
    }
}
