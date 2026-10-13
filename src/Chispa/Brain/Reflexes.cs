using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chispa;

/// <param name="Voice">Frases que el micrófono reconoce directamente tras el nombre.</param>
/// <param name="Pattern">Variantes aceptadas en texto ya normalizado (minúsculas, sin acentos ni signos).</param>
internal sealed record Reflex(string Id, string[] Voice, string Pattern);

/// <summary>Órdenes que se resuelven sin IA: respuesta inmediata y coste cero.</summary>
internal static class Reflexes
{
    public static readonly Reflex[] All =
    [
        new("hora", ["qué hora es", "dime la hora"], "que hora es|dime la hora|que hora tienes"),
        new("fecha", ["qué día es hoy", "qué fecha es hoy"], "que dia es(?: hoy)?|que fecha es(?: hoy)?|a que estamos"),
        new("saltar", ["salta", "da un salto"], "salta|brinca|da un salto"),
        new("bailar", ["baila"], "baila|ponte a bailar"),
        new("saludar", ["saluda"], "saluda|hola|buenos dias|buenas tardes|buenas noches"),
        new("esconder", ["escóndete", "ocúltate"], "escondete|ocultate|desaparece"),
        new("aparecer", ["aparece", "vuelve"], "aparece|vuelve|regresa|muestrate"),
        new("dormir", ["duérmete", "a dormir"], "duermete|a dormir|descansa"),
        new("grande", ["más grande", "crece"], "mas grande|crece|hazte (?:mas )?grande"),
        new("pequena", ["más pequeña", "encógete"], "mas pequen[ao]|encogete|hazte (?:mas )?pequen[ao]"),
        new("venir", ["ven aquí", "ven"], "ven(?: aqui| aca)?|acercate"),
        new("silencio", ["silencio", "cállate"], "silencio|callate|no hables|modo silencio"),
        new("hablar", ["puedes hablar"], "puedes hablar|habla|activa (?:la|tu) voz"),
        new("cancela", ["cancela"], "cancela|cancelar|no lo hagas|olvidalo"),
        new("quien", ["quién eres", "qué puedes hacer"], "quien eres|que eres|como te llamas|que puedes hacer|que sabes hacer"),
        new("memoria", ["qué sabes de mí", "qué recuerdas"], "que sabes de mi|que recuerdas(?: de mi)?|que has aprendido|que te he ensenado"),
        new("olvidar_todo", ["olvida todo"], "olvida todo|borra tu memoria"),
        new("conversar", ["hablemos", "escúchame", "vamos a hablar"], "hablemos|escuchame|vamos a hablar|quiero hablar(?: contigo)?|modo conversacion|conversemos"),
        new("fin", ["eso es todo"], "eso es todo|ya esta|ya termine|nada mas|es todo|gracias eso es todo"),
        new("guia", ["ayuda"],"ayuda|ayudame|abre la guia|la guia|guia|manual|que comandos (?:hay|tienes)|como te hablo|como funciona(?:s)?"),
        new("normal", ["tamaño normal"], "tamano normal|vuelve a tu tamano normal"),
        new("chiste", ["cuéntame un chiste"], "(?:cuentame|dime|echa|hazme|quiero) (?:un|otro) chiste|otro chiste|un chiste|hazme reir"),
        new("voz", ["otra voz", "cambia de voz"], "otra voz|cambia(?:te)? (?:de|la) voz|siguiente voz|prueba otra voz|no me gusta (?:esa|esta|tu) voz"),
        new("voz_hombre", ["voz de hombre"], "(?:(?:pon|usa|quiero|prefiero) )?(?:una |la )?voz (?:de )?(?:hombre|masculina|chico)"),
        new("voz_mujer", ["voz de mujer"], "(?:(?:pon|usa|quiero|prefiero) )?(?:una |la )?voz (?:de )?(?:mujer|femenina|chica)"),
    ];

    private static readonly (string Id, Regex Rx)[] compiled =
        All.Select(r => (r.Id, new Regex($"^(?:{r.Pattern})$", RegexOptions.CultureInvariant))).ToArray();

    private static readonly Regex remember =
        new(@"^(?:recu[eé]rda(?:me)?|apunta|anota|aprende)\s+que\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>Id del reflejo que corresponde al texto normalizado, o null si hace falta pensar.</summary>
    public static string? Match(string normalized)
    {
        foreach (var (id, rx) in compiled)
            if (rx.IsMatch(normalized)) return id;
        return null;
    }

    /// <summary>
    /// El reflejo cuya frase se parece mucho a lo dicho ("salta ya", "mas grande"), para entender
    /// variantes y pequeños errores del oído. Una negación ("no saltes") nunca cuenta.
    /// </summary>
    public static string? Near(string normalized)
    {
        if (normalized.Length < 4 || Regex.IsMatch(normalized, @"\b(?:no|nunca|sin)\b")) return null;
        string? best = null;
        double bestScore = 0.74;
        foreach (var reflex in All)
            foreach (string phrase in reflex.Voice)
            {
                double score = Similarity(normalized, Normalize(phrase));
                if (score > bestScore) (best, bestScore) = (reflex.Id, score);
            }
        return best;
    }

    /// <summary>Parecido entre dos frases normalizadas, de 0 a 1 (trigramas de letras en común).</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        var ta = Trigrams(a);
        var tb = Trigrams(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        return 2.0 * ta.Count(tb.Contains) / (ta.Count + tb.Count);

        static HashSet<string> Trigrams(string s)
        {
            string padded = $" {s} ";
            var set = new HashSet<string>();
            for (int i = 0; i + 3 <= padded.Length; i++) set.Add(padded.Substring(i, 3));
            return set;
        }
    }

    /// <summary>Quita el saludo y su nombre del principio, conservando mayúsculas, acentos y signos del resto.</summary>
    public static string Unaddressed(string text, string name) =>
        Regex.Replace(text.Trim(), $@"^(?:(?:oye|hola|hey)[\s,]+)?{Regex.Escape(name)}\b[\s,:.!]*", "", RegexOptions.IgnoreCase).Trim();

    /// <summary>"Recuerda que …": devuelve el dato a guardar, con sus mayúsculas y acentos originales.</summary>
    public static string? RememberRequest(string text, string name)
    {
        var m = remember.Match(Unaddressed(text, name));
        return m.Success ? m.Groups[1].Value.Trim().TrimEnd('.') : null;
    }

    /// <summary>Minúsculas, sin acentos ni signos; quita el saludo, el nombre y el "por favor".</summary>
    public static string Normalize(string text, string name = "")
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        string s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        if (name.Length > 0)
            s = Regex.Replace(s, $@"^(?:(?:oye|hola|hey) )?{Regex.Escape(Normalize(name))}\b ?", "");
        return Regex.Replace(s, @"^por favor | por favor$", "").Trim();
    }
}
