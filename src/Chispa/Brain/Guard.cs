using System.Text.RegularExpressions;

namespace Chispa;

/// <summary>
/// El guardián: revisa lo que un modelo de lenguaje va a decir antes de que se diga. Lo que afirma haber
/// hecho se contrasta con lo que de verdad se hizo; lo que afirma sobre el usuario, con la memoria, y ahí
/// decide el motor lógico: se deduce de lo que sabe, lo contradice o no se sabe. Solo pasa lo que se sostiene.
/// </summary>
internal static class Guard
{
    /// <summary>Un dato con forma fija: de quién, qué y cuánto vale. De cada asunto solo cabe un valor.</summary>
    internal readonly record struct Claim(string Subject, string Attribute, string Value, string Source)
    {
        public string Topic => $"{Subject}|{Attribute}";
    }

    private const string Me = "usuario";

    // Sobre texto normalizado. Valen en cualquier persona: «mi hermana se llama», «tu hermana se llama», «su hermana se llama».
    private static readonly Regex namedAndLives = new(@"\b(?:mi|tu|su) ([a-z]+) se llama ([a-z0-9]+) y vive en ([a-z0-9]+)");
    private static readonly Regex named = new(@"\b(?:mi|tu|su) ([a-z]+) se llama ([a-z0-9]+)");
    private static readonly Regex ownName = new(@"(?:\b(?:me llamo|te llamas|mi nombre es|tu nombre es|su nombre es)|^se llama) ([a-z0-9]+)");
    private static readonly Regex favorite = new(@"\b(?:mi|tu|su) ([a-z]+) (favorit[oa]s?|preferid[oa]s?) (?:es|son|era) (?:el |la |los |las )?([a-z0-9]+)");
    private static readonly Regex otherLives = new(@"\b(?:mi|tu|su) ([a-z]+) vive en ([a-z0-9]+)");
    private static readonly Regex ownHome = new(@"(?:\b(?:vivo|vives)|^vive) en ([a-z0-9]+)");
    private static readonly HashSet<string> notAValue =
        ["como", "asi", "igual", "que", "un", "una", "el", "la", "lo", "en", "de", "y", "o", "tambien", "ahi", "aqui", "alli", "cerca", "lejos", "muy", "algo", "eso", "esto"];

    // Solo vale cuando una acción se quedó sin confirmar: entonces, quien la dé por hecha se equivoca.
    private static readonly Regex claimsDone = new(
        @"\bya esta (?:abiert[oa]|hech[oa]|list[oa]|escrit[oa]|puest[oa]|subid[oa]|bajad[oa]|silenciad[oa])\b|\bya (?:lo |la |te lo |te la )?(?:abri|hice|escribi|subi|baje|puse|envie|silencie)\b" +
        @"|\b(?:he|acabo de) (?:abierto|abrir|subido|subir|bajado|bajar|escrito|escribir|puesto|poner|enviado|enviar|silenciado|silenciar)\b|\baqui (?:tienes|la tienes|lo tienes)\b|^(?:listo|hecho)\b");

    // Contar que se usa una herramienta, o actuar con acotaciones («*me escondo*»), no es hacerlo.
    private static readonly Regex narratesTool = new(
        @"\b(?:llamando|llamo|ejecuto|ejecutando|uso|usando|utilizo|utilizando|invoco|invocando|activo|activando)\b(?: [a-z]+){0,3} herramienta\b" +
        $@"|\bherramienta (?:{string.Join('|', ToolBox.Specs.Select(t => Reflexes.Normalize(t.Name)))})\b|\btool call\b" +
        @"|\b(?:usando|ejecutando|activando) (?:el|la) (?:accion|aspecto|herramienta|funcion)\b");
    // Restos de una llamada escrita en el texto que nadie supo leer: «investigar{consulta:…», «<cuerpo …», «<|"|>».
    private static readonly Regex leakedCall = new(
        $@"<\|""\|>|<tool_call|(?<![\w""])(?:{string.Join('|', ToolBox.Specs.Select(t => Regex.Escape(t.Name)))})\s*[{{(]|<(?:{string.Join('|', ToolBox.Specs.Select(t => Regex.Escape(t.Name)))})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex stageDirection = new(@"\*[^*\n]{3,80}\*|\((?:me |voy |estoy |se )[^)\n]{3,80}\)", RegexOptions.IgnoreCase);
    private static readonly Regex bodyVerb = new(
        @"\b(?:salto|saltando|bailo|bailando|brinco|brincando|escondo|escondiendome|oculto|ocultandome|duermo|durmiendo|dormir|acuesto|acostandome" +
        @"|camino|caminando|acerco|acercandome|saludo|saludando|aparezco|transformo|transformandome)\b");
    private static readonly string[] bodyTools = ["cuerpo", "tamano"];
    private static readonly string[] memoryTools = ["recordar", "avisar"];
    private static readonly Regex claimsKept = new(
        @"\b(?:te (?:lo )?(?:recordare|avisare)|me acordare|(?:he|acabo de|ya he) (?:guardado|apuntado|anotado|programado|guardarlo|apuntarlo|anotarlo|programarlo|guardar|apuntar|anotar|programar)" +
        @"|te lo (?:guardo|apunto|anoto|programo)|(?:queda|quedo|esta|lo tengo) (?:guardado|apuntado|anotado|programado))\b");
    private static readonly Regex claimsNewLook = new(@"\b(?:ahora|ya) soy mas (?:grande|pequena)|\b(?:cambie|ajuste) (?:mi|el) tamano\b");

    /// <summary>Lo que se le añade a las instrucciones cuando hay que pedirle la respuesta otra vez.</summary>
    public const string DoItForReal =
        "\nAviso: en tu respuesta anterior describiste con palabras una acción o una herramienta en vez de usarla. " +
        "No escribas asteriscos, acotaciones ni nombres de herramientas. Si hace falta una herramienta, llámala de verdad; si no, responde solo con lo que sabes.";

    /// <summary>Lo que se le añade a las instrucciones cuando una herramienta falló: arreglarlo de verdad o confesarlo.</summary>
    public const string CorrectionMark = "Aviso: falló una herramienta.";
    public static string CorrectIt(string failure) =>
        CorrectionMark + " Si puedes conseguirlo con otra herramienta o con otro dato, hazlo ahora de verdad. " +
        $"Si no hay forma, respóndele al usuario contándole exactamente lo que pasó. El fallo fue: «{failure}»";

    // Frases con las que el modelo reconoce que algo falló: entonces sobra la ronda de corrección.
    private static readonly Regex admitsFailure = new(
        @"\b(?:no pude|no he podido|no se pudo|no pude hacerlo|no fue posible|no consigo|no consegui|no logr[oe]|ha fallado|fall[oó]|hubo un error|sigue abiert[oa]|no encuentro|no encontre|no existe|no esta instalad[oa]|no hay (?:ningun[ao]|forma de))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>¿La respuesta ya cuenta que algo falló? Entonces no hay nada que corregirle.</summary>
    public static bool AdmitsFailure(string reply, string failure) => admitsFailure.IsMatch(Reflexes.Normalize(reply));

    /// <summary>¿Describe una acción o una herramienta en vez de usarla? Entonces esa respuesta no vale.</summary>
    /// <param name="used">Las herramientas que sí usó en este turno.</param>
    public static bool FakesAction(string reply, IReadOnlyCollection<string> used)
    {
        string normalized = Reflexes.Normalize(reply);
        if (!used.Contains("abrir") && Regex.IsMatch(normalized, @"\b(?:abriendo|abro|abrire|he abierto|ya abri|voy a abrir|te abro|navegador abierto)\b")) return true;
        if (reply.Length > 0 && !reply.Any(char.IsLetter)) return true;   // «[]»: restos de una llamada mal escrita
        if (leakedCall.IsMatch(reply) || narratesTool.IsMatch(Reflexes.Normalize(reply))) return true;
        // «Te lo acabo de guardar, me acordaré»: sin haber usado recordar ni avisar, es mentira y además peligrosa.
        if (!used.Any(memoryTools.Contains) && claimsKept.IsMatch(Reflexes.Normalize(reply))) return true;
        if (used.Any(bodyTools.Contains)) return false;
        return claimsNewLook.IsMatch(Reflexes.Normalize(reply))
               || stageDirection.Matches(reply).Any(m => bodyVerb.IsMatch(Reflexes.Normalize(m.Value)));
    }

    public static bool MissingRequestedAction(string request, IReadOnlyCollection<string> used)
    {
        string text = Reflexes.Normalize(request);
        if (Regex.IsMatch(text, @"\b(?:no|nunca|sin)\b")) return false;
        return (!used.Contains("abrir") && OwnBrain.OpenDestination(request) != null)
            || (!used.Contains("cerrar") && OwnBrain.CloseDestination(request) != null);
    }

    /// <summary>Los datos con forma fija que afirma un texto. Lo que no encaja en ninguna forma no se toca.</summary>
    public static List<Claim> Claims(string text)
    {
        var found = new List<Claim>();
        foreach (string sentence in Regex.Split(text, @"(?<=[.!?;\n])\s*").Where(s => s.Trim().Length > 0))
        {
            if (sentence.Contains('?')) continue;   // preguntar no es afirmar
            string norm = Reflexes.Normalize(sentence);
            void Add(string subject, string attribute, string value)
            {
                if (!notAValue.Contains(value) && !found.Any(c => c.Subject == subject && c.Attribute == attribute))
                    found.Add(new(subject, attribute, value, sentence.Trim()));
            }
            foreach (Match m in namedAndLives.Matches(norm)) Add(m.Groups[1].Value, "ciudad", m.Groups[3].Value);
            foreach (Match m in named.Matches(norm)) Add(m.Groups[1].Value, "nombre", m.Groups[2].Value);
            foreach (Match m in ownName.Matches(norm)) Add(Me, "nombre", m.Groups[1].Value);
            foreach (Match m in favorite.Matches(norm)) Add(Me, m.Groups[1].Value + " favorito", m.Groups[3].Value);
            foreach (Match m in otherLives.Matches(norm)) Add(m.Groups[1].Value, "ciudad", m.Groups[2].Value);
            foreach (Match m in ownHome.Matches(norm)) Add(Me, "ciudad", m.Groups[1].Value);
        }
        return found;
    }

    /// <summary>Lo que se sabe de una afirmación a la vista de los datos conocidos. Lo decide el motor lógico.</summary>
    public static Verdict Judge(IReadOnlyList<Claim> known, Claim claim, CancellationToken ct = default)
    {
        // Una variable por cada «asunto = valor»; cada dato conocido es un hecho, y de cada asunto solo cabe un valor.
        var ids = new Dictionary<string, int>();
        int Id(Claim c) => ids.TryGetValue($"{c.Topic}={c.Value}", out int id) ? id : ids[$"{c.Topic}={c.Value}"] = ids.Count + 1;
        var clauses = known.Select(k => new[] { Id(k) }).ToList();
        int asked = Id(claim);
        foreach (var topic in known.Append(claim).GroupBy(c => c.Topic))
        {
            int[] values = topic.Select(Id).Distinct().ToArray();
            for (int a = 0; a < values.Length; a++)
                for (int b = a + 1; b < values.Length; b++) clauses.Add([-values[a], -values[b]]);
        }
        if (Reason.Judge(clauses, asked, ct) is { } verdict) return verdict;
        // Sin el motor, lo mismo a mano: con datos sueltos basta comparar.
        var same = known.Where(k => k.Topic == claim.Topic).ToList();
        return same.Count == 0 ? Verdict.Unknown : same.Any(k => k.Value == claim.Value) ? Verdict.Deduced : Verdict.Contradicted;
    }

    /// <summary>
    /// Revisa una respuesta del modelo. Devuelve lo que sí puede decirse: la misma, o una corrección.
    /// </summary>
    /// <param name="actionDenied">En este turno pidió un cambio en el equipo que el usuario no confirmó.</param>
    /// <param name="userSaid">Lo que el usuario ha dicho en esta conversación.</param>
    /// <param name="stopped">Por qué se frenó la respuesta original, o null si pasó.</param>
    public static string Review(string reply, bool actionDenied, Memory memory, IEnumerable<string> userSaid, out string? stopped, CancellationToken ct = default)
    {
        stopped = null;
        if (actionDenied && claimsDone.IsMatch(Reflexes.Normalize(reply)))
        {
            stopped = $"dio por hecho un cambio que no se confirmó: «{reply}»";
            return "No lo hice, porque faltó tu confirmación. Si quieres, pídemelo otra vez.";
        }
        var claims = Claims(reply);
        if (claims.Count == 0) return reply;

        var known = memory.All.SelectMany(f => Claims(f.Texto)).ToList();
        string heard = " " + Reflexes.Normalize(string.Join(" ", memory.All.Select(f => f.Texto).Concat(userSaid))) + " ";
        foreach (var claim in claims)
        {
            var verdict = Judge(known, claim, ct);
            if (verdict == Verdict.Contradicted)
            {
                string truth = known.First(k => k.Topic == claim.Topic).Source;
                stopped = $"«{claim.Source}» contradice lo que sé: «{truth}»";
                return $"Me dijiste que {OwnBrain.Second(truth.TrimEnd('.', '!'))}.";
            }
            // No consta con esa forma. Si el valor tampoco aparece en nada de lo que el usuario ha dicho, se lo está inventando.
            if (verdict == Verdict.Unknown && !heard.Contains($" {claim.Value} "))
            {
                stopped = $"«{claim.Source}» no sale de nada que me hayan contado";
                return "Eso todavía no me lo has contado, y prefiero no inventármelo.";
            }
        }
        return reply;
    }
}
