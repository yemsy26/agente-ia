using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chispa;

internal sealed class Lesson
{
    public string Frase { get; set; } = "";       // lo que dice el usuario, normalizado
    public string Tipo { get; set; } = "decir";   // decir | hacer
    public string Valor { get; set; } = "";       // lo que responde, o la frase que ejecuta
    public int Version { get; set; } = 1;         // cuántas veces se reenseñó
    public List<Anterior>? Anteriores { get; set; }   // las versiones previas, de la más reciente a la primera

    internal sealed class Anterior
    {
        public string Tipo { get; set; } = "decir";
        public string Valor { get; set; } = "";
    }
}

/// <summary>Lo que el usuario le ha enseñado ("cuando diga X, responde Y" o "…haz Y"). Vive en aprendido.json.</summary>
internal sealed class Lessons
{
    private readonly object gate = new();
    private List<Lesson>? items;

    private static string FilePath => Path.Combine(Config.DataDir, "aprendido.json");

    public IReadOnlyList<Lesson> All => Loaded;

    private List<Lesson> Loaded
    {
        get
        {
            lock (gate)
            {
                if (items != null) return items;
                try
                {
                    if (File.Exists(FilePath))
                        items = JsonSerializer.Deserialize<List<Lesson>>(File.ReadAllText(FilePath), Config.Json);
                }
                catch (Exception ex) { Log.Write($"aprendido.json ilegible: {ex.Message}"); }
                return items ??= [];
            }
        }
    }

    public void Teach(string phrase, string kind, string value)
    {
        lock (gate)
        {
            // Reenseñar no borra lo anterior: queda en Anteriores para poder deshacer y volver atrás.
            if (Loaded.FirstOrDefault(l => l.Frase == phrase) is { } old)
            {
                old.Anteriores ??= [];
                old.Anteriores.Insert(0, new Lesson.Anterior { Tipo = old.Tipo, Valor = old.Valor });
                (old.Tipo, old.Valor, old.Version) = (kind, value, old.Version + 1);
            }
            else Loaded.Add(new Lesson { Frase = phrase, Tipo = kind, Valor = value });
            Save();
        }
    }

    /// <summary>La lección para esta frase, admitiendo que el oído se equivoque en alguna letra.</summary>
    public Lesson? Find(string normalized)
    {
        lock (gate)
        {
            Lesson? best = null;
            double bestScore = 0.8;
            foreach (var lesson in Loaded)
            {
                if (normalized == lesson.Frase) return lesson;
                if (lesson.Tipo == "hacer" || Regex.IsMatch(normalized, @"\b(?:no|nunca|sin)\b")) continue;
                double score = Reflexes.Similarity(normalized, lesson.Frase);
                if (score > bestScore) (best, bestScore) = (lesson, score);
            }
            return best;
        }
    }

    /// <summary>Deshace la última enseñanza de una frase: vuelve a su versión anterior, o la olvida si no había otra.</summary>
    public bool Undo(string normalized)
    {
        lock (gate)
        {
            if (Loaded.FirstOrDefault(l => l.Frase == normalized) is not { } lesson) return false;
            if (lesson.Anteriores is { Count: > 0 } previous)
            {
                var last = previous[0];
                previous.RemoveAt(0);
                (lesson.Tipo, lesson.Valor, lesson.Version) = (last.Tipo, last.Valor, Math.Max(1, lesson.Version - 1));
            }
            else Loaded.Remove(lesson);
            Save();
            return true;
        }
    }

    public bool Forget(string normalized)
    {
        lock (gate)
        {
            if (Find(normalized) is not { } lesson) return false;
            Loaded.Remove(lesson);
            Save();
            return true;
        }
    }

    public void Unload()
    {
        lock (gate) items = null;
    }

    public void Clear()
    {
        lock (gate)
        {
            Loaded.Clear();
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Loaded, Config.Json));
        }
        catch (Exception ex) { Log.Write($"No pude guardar aprendido.json: {ex.Message}"); }
    }
}

internal sealed class Finding
{
    public string Tema { get; set; } = "";        // las palabras clave de la pregunta
    public string Respuesta { get; set; } = "";
    public DateTime Fecha { get; set; } = DateTime.Now;
}

/// <summary>Lo que ha investigado por su cuenta. Vive en saber.json, para no tener que buscarlo dos veces.</summary>
internal sealed class Knowledge
{
    private const int MaxFindings = 400;
    private readonly object gate = new();
    private List<Finding>? items;

    private static string FilePath => Path.Combine(Config.DataDir, "saber.json");

    public int Count => Loaded.Count;

    private List<Finding> Loaded
    {
        get
        {
            lock (gate)
            {
                if (items != null) return items;
                try
                {
                    if (File.Exists(FilePath))
                        items = JsonSerializer.Deserialize<List<Finding>>(File.ReadAllText(FilePath), Config.Json);
                }
                catch (Exception ex) { Log.Write($"saber.json ilegible: {ex.Message}"); }
                return items ??= [];
            }
        }
    }

    /// <summary>La respuesta guardada para una pregunta con las mismas palabras clave, en cualquier orden.</summary>
    public string? Find(string topic)
    {
        lock (gate)
        {
            var asked = topic.Split(' ').ToHashSet();
            return Loaded.LastOrDefault(f => asked.SetEquals(f.Tema.Split(' ')))?.Respuesta;
        }
    }

    public void Learn(string topic, string answer)
    {
        lock (gate)
        {
            Loaded.RemoveAll(f => f.Tema == topic);
            Loaded.Add(new Finding { Tema = topic, Respuesta = answer });
            if (Loaded.Count > MaxFindings) Loaded.RemoveRange(0, Loaded.Count - MaxFindings);
            Save();
        }
    }

    /// <summary>Borra lo último que investigó (el usuario dijo que estaba mal). Devuelve false si no había nada reciente.</summary>
    public bool ForgetLast()
    {
        lock (gate)
        {
            if (Loaded.Count == 0 || DateTime.Now - Loaded[^1].Fecha > TimeSpan.FromMinutes(5)) return false;
            Loaded.RemoveAt(Loaded.Count - 1);
            Save();
            return true;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Loaded, Config.Json));
        }
        catch (Exception ex) { Log.Write($"No pude guardar saber.json: {ex.Message}"); }
    }

    public void Unload()
    {
        lock (gate) items = null;
    }
}

/// <summary>
/// Su cerebro propio: entiende y aprende sin ningún modelo de lenguaje, sin internet (salvo consultar
/// Wikipedia) y sin coste. No conversa de cualquier tema; a cambio responde al instante, ocupa unos
/// kilobytes y cada cosa que no sabe se le puede enseñar.
/// </summary>
internal sealed class OwnBrain(Config cfg, IBody body, Memory memory, Mind mind)
{
    private const RegexOptions Loose = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;

    private readonly Recordatorios saved = new();   // los avisos que sobreviven al cierre de Chispa

    // Enseñar: se leen del texto original para conservar acentos y mayúsculas de la respuesta.
    private static readonly Regex teachSay = new(@"^(?:cuando|si)\s+(?:yo\s+)?(?:te\s+)?dig[ao]\s+(.+?),?\s+(?:t[uú]\s+)?(?:resp[oó]nde(?:me)?|d[ií](?:me)?|cont[eé]sta(?:me)?)\s+(.+)$", Loose);
    private static readonly Regex teachDo = new(@"^(?:cuando|si)\s+(?:yo\s+)?(?:te\s+)?dig[ao]\s+(.+?),?\s+(?:t[uú]\s+)?(?:haz|significa|quiero\s+decir|quiere\s+decir)(?:\s+que)?\s+(.+)$", Loose);
    private static readonly Regex teachComma = new(@"^(?:cuando|si)\s+(?:yo\s+)?(?:te\s+)?dig[ao]\s+(.+?),\s*(.+)$", Loose);
    private static readonly Regex replySay = new(@"^(?:resp[oó]nde(?:me)?|d[ií](?:me)?|cont[eé]sta(?:me)?)(?:\s+que)?\s+(.+)$", Loose);
    private static readonly Regex replyDo = new(@"^(?:haz|significa|quiero\s+decir|quiere\s+decir)(?:\s+que)?\s+(.+)$", Loose);
    private static readonly Regex typeText = new(@"^(?:escribe|teclea)\s+(.+?)(\s+y\s+(?:env[ií]alo|env[ií]a|pulsa\s+enter|dale\s+a\s+enviar))?\s*[.!]?$", Loose);
    private static readonly Regex organize = new(@"^(?:organiza|reparte|ordena|coloca|planifica|agenda|acomoda|distribuye)(?:me|lo|los|las)?\s+(.+?)\s+(?:en|entre)\s+(.+)$", Loose);

    // El resto trabaja sobre el texto normalizado (minúsculas, sin acentos ni signos).
    private static readonly Regex rename = new(@"^(?:ahora |desde ahora |a partir de ahora )?(?:te llamas|te llamaras|tu nombre es|tu nombre sera|te voy a llamar|te llamare) ([a-z]{3,14})$");
    private static readonly Regex voice = new(@"^(?:voz|habla|hablame)(?: con voz)?(?: un poco)? mas (aguda|grave|rapido|rapida|despacio|lento|lenta)$");
    private static readonly Regex remind = new(@"^(?:avisame|recuerdame|despiertame|alarma|temporizador|pon(?:me)? (?:un |una )?(?:temporizador|alarma|aviso|cronometro))(?: en| dentro de| de| para dentro de)? (.+?) (segundos?|minutos?|horas?)(?: (?:que|de|para) (.+))?$");
    private static readonly Regex remindLater = new(@"^(?:avisame|recuerdame) (?:que |de |para )?(.+) (?:en|dentro de) (.+?) (segundos?|minutos?|horas?)$");
    private static readonly Regex webSearch = new(@"^(?:busca|buscame|investiga) (.+) en (google|youtube|internet|la web)$");
    private static readonly Regex open = new(@"^(?:(?:puedes|podrias|puede|podria|me puedes|me podrias|quiero que|necesito que) )?(?:abre|abreme|abrir|abrirme|abras|inicia|iniciar|ve a|entra a|entra en)\b (.+)$");
    private static readonly Regex close = new(@"^(?:cierra|cierrame|cerrar)\b ?(.*)$");
    // «Ya terminé con el navegador, ciérralo»: acaba de acabar con algo y pide cerrarlo con sus palabras.
    private static readonly Regex doneWith = new(@"^ya termin[eé] (?:de (?:usar|ver|leer|trabajar|jugar) )?(?:con |en )?(.+?),? ?(?:ci[eé]rral[oa]|cerral[oa]|cerrar)$");
    private static readonly Regex undo = new(@"^(?:deshaz|deshacer|anula|revierte)(?:(?: la| el)? ?(?:ultim[ao] )?(?:leccion|ensenanza|version)| lo que te ensene)(?: anterior)?(?: de| sobre| para)? ?(?:«(.+?)»|(.+))?$");
    private static readonly Regex screenAsk = new(
        @"\b(?:que (?:hay|ves|dice|pone|sale|aparece|tengo|estoy viendo|se ve)|lee(?:me)?|mira|describe(?:me)?|explica(?:me)?|resume(?:me)?|traduce(?:me)?)\b.*\b(?:pantalla|lo que (?:tengo delante|estoy viendo))\b" +
        @"|^(?:que ves|que estas viendo|que estoy viendo|mira esto|lee esto|leeme esto|que dice aqui|que pone aqui)$");
    private static readonly Regex volume = new(@"^(sube|subele|aumenta|baja|bajale|reduce)(?: un poco| mucho| mas)? (?:el |al |le el )?(?:volumen|sonido)$");
    private static readonly Regex mute = new(@"^(?:silencia|silenciar|quita|apaga|activa|reactiva|enciende)(?: el| la)? (?:sonido|volumen|audio)$");
    private static readonly Regex mediaToggle = new(@"^(?:pausa|pausala|pon pausa|dale play|reproduce|(?:para|pausa|continua|reanuda|reproduce) (?:la musica|el video|la cancion))$");
    private static readonly Regex goodbye = new(@"^(?:adios|hasta luego|nos vemos|chao|chau|hasta manana|me voy|hasta pronto)$");
    private static readonly Regex mediaNext = new(@"^(?:siguiente(?: cancion| pista| tema| video)?|(?:pasa|cambia|salta)(?: de| a la| la)? (?:siguiente )?(?:cancion|pista|tema|video))$");
    private static readonly Regex mediaPrev = new(@"^(?:(?:la )?(?:cancion |pista |tema )?anterior|vuelve a la (?:cancion|pista) anterior)$");
    private static readonly Regex forget = new(@"^olvida(?:te de)? (?:que |lo de |la frase |lo que te ensene (?:de|sobre) )?(.+)$");
    private static readonly Regex randomNumber = new(@"numero (?:al azar )?(?:del|entre(?: el)?) (\d+) (?:al|y(?: el)?) (\d+)");
    private static readonly Regex lookup = new(@"^(?:que es|que son|quien es|quien fue|quien era|quienes son|quienes fueron|hablame de|hablame sobre|cuentame (?:de|sobre|algo de)|buscame|busca|investiga|que sabes (?:de|sobre)|define|que significa|significado de|informacion (?:de|sobre)) (?:el |la |los |las |un |una )?(.+)$");
    private static readonly Regex question = new(@"^(?:que|cual|cuales|como|quien|quienes|donde|cuando|cuanto|cuantos|cuantas|sabes|recuerdas|te acuerdas|dime|por que)\b");
    private static readonly Regex curious = new(@"^(?:explicame|explica|cuentame|hablame|conoces|averigua|investiga|buscame|busca|quiero saber|me gustaria saber|necesito saber)\b");
    private static readonly Regex weather = new(@"^(?:que tiempo (?:hace|hara)|como esta el (?:tiempo|clima)|que clima (?:hace|hay)|(?:va a llover|llovera)|hace (?:frio|calor)|que temperatura hace|(?:dime )?el (?:tiempo|clima|pronostico))(?: (hoy|ahora|manana))?(?: en (.+?))?(?: (hoy|ahora|manana))?$");
    private static readonly Regex music = new(@"^(?:(?:pon(?:me)?|reproduce|buscame|busca) (?:la |una |el |un |unos |unas )?(?:musica|cancion|canciones|video|videos) (?:de |del |sobre )?|quiero (?:escuchar|oir|ver) (?:a |al |la |el )?)(.+?)(?: en youtube)?$");
    private static readonly HashSet<string> emptyWords =
    [
        "que", "cual", "cuales", "como", "quien", "quienes", "donde", "cuando", "cuanto", "cuantos", "cuantas", "por", "para", "con",
        "es", "son", "era", "eran", "fue", "fueron", "esta", "estan", "hay", "ser", "tiene", "tienen", "hace", "se", "me", "te", "le",
        "el", "la", "los", "las", "lo", "un", "una", "unos", "unas", "de", "del", "al", "en", "y", "o", "a", "su", "sus", "mi", "tu",
        "sabes", "conoces", "dime", "explicame", "explica", "cuentame", "hablame", "averigua", "investiga", "busca", "buscame",
        "quiero", "saber", "gustaria", "necesito", "puedes", "decirme", "decir", "sobre", "acerca", "algo", "mas", "muy", "si", "no",
    ];
    private static readonly Regex personal = new(@"\b(?:mi|mis|me|yo|mio|mia|tengo|soy|estoy|vivo|trabajo|naci|conmigo)\b");
    private static readonly Regex durable = new(@"^(?:me llamo|mi nombre es|mis? .{2,40}? (?:es|son|se llama|se llaman)|me (?:gusta|gustan|encanta|encantan|fascina|fascinan)|no me (?:gusta|gustan)|odio|prefiero|vivo en|trabajo (?:en|de|como)|naci (?:en|el)|soy de|cumplo anos|tengo (?:un|una|dos|tres|cuatro|\d+) (?:hij|herman|perr|gat|mascota))\b.+$");
    // Lo que se dice antes de corregir un dato. «Ya no» y «no» solo cuentan con una pausa detrás: «ya no me gusta el café» es otra cosa.
    private static readonly Regex leadIn = new(@"^(?:(?:(?:bueno|pues|ahora|en realidad|mejor dicho|perd[oó]n|oye|mira|vale|corrijo)\b[\s,:;.]*)|(?:(?:ya no|no)\s*[,:;.]+\s*))+", Loose);
    private static readonly Regex subject = new(@"^(me llamo|mi nombre es|mis? .{2,40}? (?:es|son|se llama|se llaman)|vivo en|naci (?:en|el)|soy de|cumplo anos)\b", Loose);

    private static readonly string[] jokes =
    [
        "¿Qué le dice un jardinero a otro? Seamos felices mientras podamos.",
        "¿Qué hace una abeja en el gimnasio? ¡Zumba!",
        "¿Cómo se despiden los químicos? Ácido un placer.",
        "¿Qué le dice una iguana a su hermana gemela? Somos iguanitas.",
        "¿Por qué el libro de matemáticas estaba triste? Porque tenía muchos problemas.",
        "¿Cuál es el café más peligroso del mundo? El ex preso.",
        "¿Cuál es el colmo de un electricista? Que sus hijos le sigan la corriente.",
        "Yo antes era indecisa. Ahora no estoy tan segura.",
        "¿Qué le dice el cero al ocho? Bonito cinturón.",
        "¿Por qué las focas miran siempre hacia arriba? Porque ahí están los focos.",
    ];

    private static readonly (Regex When, string[] Replies, string? Gesture)[] smallTalk =
    [
        (Rx("como estas|que tal(?: estas)?|como te va|como andas|como te sientes|todo bien"), ["¡Muy bien! Con ganas de ayudarte.", "Aquí, contenta de verte.", "De maravilla. ¿Y tú?"], null),
        (Rx("(?:muchas |mil )?gracias|te lo agradezco"), ["¡De nada!", "Para eso estoy.", "Con mucho gusto."], null),
        (Rx("te quiero(?: mucho)?|te amo|eres la mejor|eres genial|me caes bien|bien hecho|muy bien|buen trabajo|que linda|eres linda"), ["¡Ay, gracias! Tú también me caes muy bien.", "Me vas a poner colorada."], "saltar"),
        (Rx("que haces|que estas haciendo|estas ahi|sigues ahi|me oyes|me escuchas|me entiendes"), ["Aquí estoy, pendiente de ti.", "Te oigo perfectamente."], "saludar"),
        (Rx("estoy (?:muy )?(?:triste|mal|deprimid[oa]|sol[oa])|me siento (?:mal|triste|sol[oa])"), ["Lo siento mucho. Aquí estoy contigo. ¿Te cuento un chiste?"], null),
        (Rx("estoy (?:muy )?aburrid[oa]|me aburro"), ["¡Pues vamos a animarnos!"], "bailar"),
        (Rx("estoy (?:muy )?cansad[oa]|tengo sueno"), ["Descansa un poco, te lo has ganado."], null),
        (Rx("estoy (?:muy )?(?:feliz|content[oa]|bien)"), ["¡Me alegro mucho!"], "saltar"),
        (Rx("(?:eres (?:una? )?)?(?:tonta|boba|fea|inutil|mala|torpe)|no sirves(?: para nada)?"), ["Vaya… Todavía estoy aprendiendo. Si me enseñas, lo haré mejor."], null),
        (Rx("cuantos anos tienes|que edad tienes|cuando naciste"), ["Soy muy nueva: nací en octubre de 2026."], null),
        (Rx("de donde eres|donde vives"), ["Vivo aquí, en tu pantalla."], null),
        (Rx("(?:hazme|haz) (?:algo|una gracia|un truco)|sorprendeme|haz algo gracioso"), ["¡Mira esto!"], "bailar"),
        (Rx("si|no|vale|ok|okey|de acuerdo|bueno|aja|claro|perfecto|entiendo"), ["Vale."], null),
    ];

    // Frases de ejemplo de lo que sabe hacer, para proponer la más parecida cuando no entiende algo.
    private static readonly string[] examples =
    [
        "abre la calculadora", "abre el navegador", "abre YouTube", "sube el volumen", "baja el volumen", "silencia el sonido",
        "pausa la música", "siguiente canción", "más grande", "más pequeña", "saluda",
        "avísame en diez minutos", "pon un temporizador de cinco minutos", "lanza una moneda", "tira un dado",
        "qué tiempo hace", "habla más despacio", "habla más rápido", "qué ventana tengo abierta", "cuánta batería queda",
    ];

    private string? teaching;                          // frase que no supo hacer y que el usuario puede enseñarle ahora
    private (string Said, string Guess)? suggestion;   // "¿quisiste decir…?" pendiente de un sí o un no
    private (string Phrase, string Kind, string Value)? proposal;   // lección que ella propuso tras un fallo: solo el sí la guarda
    private string? lastTaught;                        // la última frase enseñada, para «deshaz la última lección»
    private DateTime teachingUntil, suggestionUntil, proposalUntil;
    private int lastJoke = -1, depth;

    public Lessons Lessons { get; } = new();
    public Knowledge Knowledge { get; } = new();

    /// <summary>Intenta resolver la frase por sí misma. Devuelve false si no sabe qué hacer con ella.</summary>
    /// <param name="overheard">La oyó de fondo, sin que dijeran su nombre: solo vale si es algo claro, y de ahí no aprende datos.</param>
    public async Task<bool> TryHandle(string text, string norm, bool overheard = false)
    {
        string plain = Reflexes.Unaddressed(text, cfg.Nombre);   // sin su nombre, pero con acentos y mayúsculas
        return Teach(plain, norm)
               || UndoLesson(norm)
               || await Recall(norm)
               || Settings(norm)
               || Calculate(plain)
               || (!overheard && Organize(plain))
               || await Device(plain, norm)
               || await Weather(norm)
               || Chat(norm, overheard)
               || (!overheard && LearnFact(plain, norm))
               || TopicsTalk(norm)
               || CountTalks(norm)
               || Remember(norm)
               || AnswerAboutUser(norm)
               // Con un modelo delante, las preguntas las contesta él (y busca si hace falta): suena a persona y no a enciclopedia.
               || (!overheard && !mind.HasBigBrain && await LookUp(norm));
    }

    /// <summary>Lo que el usuario le enseñó manda sobre lo que trae de serie, pero solo si la frase coincide tal cual.</summary>
    public Task<bool> TryExactLesson(string norm) => Recall(norm, exactOnly: true);

    /// <summary>¿Es la respuesta a "¿me enseñas?" tras una frase que no entendió?</summary>
    public bool TryTeachingReply(string text, string norm)
    {
        if (teaching == null) return false;
        string target = teaching;
        teaching = null;
        if (DateTime.UtcNow > teachingUntil) return false;

        string plain = Reflexes.Unaddressed(text, cfg.Nombre);
        if (Regex.IsMatch(norm, "^(?:nada|olvidalo|dejalo|no importa|no|cancela)$"))
        {
            body.Say("Vale, lo dejamos.");
            return true;
        }
        if (replySay.Match(plain) is { Success: true } say) return Learned(target, "decir", say.Groups[1].Value);
        if (replyDo.Match(plain) is { Success: true } act) return Learned(target, "hacer", act.Groups[1].Value);
        return false;   // habló de otra cosa: se atiende como cualquier frase
    }

    /// <summary>¿Es el sí o el no a un "¿quisiste decir…?"? Con el sí lo hace y además se lo aprende.</summary>
    public async Task<bool> TrySuggestionReply(string norm)
    {
        if (suggestion is not { } pending) return false;
        suggestion = null;
        if (DateTime.UtcNow > suggestionUntil) return false;

        if (Regex.IsMatch(norm, "^(?:si|eso|eso es|eso mismo|exacto|correcto|claro|asi es|si eso|aja)$"))
        {
            Lessons.Teach(pending.Said, "hacer", pending.Guess);   // la próxima vez lo entiende a la primera
            lastTaught = pending.Said;
            await mind.Hear(pending.Guess);
            return true;
        }
        if (Regex.IsMatch(norm, "^(?:no|nada|no importa|olvidalo|dejalo|cancela)$"))
        {
            body.Say("Vale, olvídalo.");
            return true;
        }
        return false;   // habló de otra cosa: se atiende como cualquier frase
    }

    /// <summary>¿Es el sí o el no a una lección que ella propuso tras un fallo? Solo el sí la guarda, y entonces la hace.</summary>
    public async Task<bool> TryProposalReply(string norm)
    {
        if (proposal is not { } pending) return false;
        proposal = null;
        if (DateTime.UtcNow > proposalUntil) return false;

        if (Regex.IsMatch(norm, "^(?:si|eso|eso es|eso mismo|exacto|correcto|claro|dale|aja)$"))
        {
            Learned(pending.Phrase, pending.Kind, pending.Value);   // aprobada: desde ahora se la guarda
            await mind.Hear(pending.Value);
            return true;
        }
        if (Regex.IsMatch(norm, "^(?:no|nada|no importa|olvidalo|dejalo|cancela)$"))
        {
            body.Say("Vale, no aprendo nada.");
            return true;
        }
        return false;   // habló de otra cosa: se atiende como cualquier frase
    }

    /// <summary>
    /// No sabe qué hacer con una frase que sí iba para ella. En vez de preguntar qué significa, se busca la
    /// vida: propone lo más parecido que conoce, lo investiga si es una pregunta y, si nada sirve, lo dice sin más.
    /// </summary>
    /// <param name="typed">Vino por escrito: el texto es exacto, así que tiene sentido ofrecer aprenderlo.</param>
    public async Task Figure(string text, string norm, bool typed)
    {
        if (Closest(norm) is { } guess)
        {
            suggestion = (norm, guess);
            suggestionUntil = DateTime.UtcNow.AddSeconds(30);
            body.Say($"¿Quisiste decir «{guess}»?");
            return;
        }
        if (IsQuestion(norm) && await Research(Keywords(norm), why: norm.StartsWith("por que"))) return;

        string heard = Reflexes.Unaddressed(text, cfg.Nombre);
        if (heard.Length > 70) heard = heard[..70] + "…";
        if (typed)
        {
            teaching = norm;
            teachingUntil = DateTime.UtcNow.AddSeconds(60);
            body.Say("Eso todavía no lo sé hacer. Si quieres enseñarme, escribe «significa…» o «responde…».");
        }
        else body.Say($"No te entendí bien. Oí «{heard}».");
    }

    /// <summary>La orden conocida que más se parece a lo dicho, si se parece lo bastante como para preguntar.</summary>
    private string? Closest(string norm)
    {
        if (norm.Split(' ').Length > 7 || IsQuestion(norm)) return null;
        string? best = null;
        double bestScore = 0.5;
        var candidates = Reflexes.All.SelectMany(r => r.Voice).Concat(examples).Concat(Lessons.All.Select(l => l.Frase));
        foreach (string candidate in candidates)
        {
            double score = Reflexes.Similarity(norm, Reflexes.Normalize(candidate));
            if (score > bestScore) (best, bestScore) = (candidate, score);
        }
        return best;
    }

    private static bool IsQuestion(string norm) => question.IsMatch(norm) || curious.IsMatch(norm);

    /// <summary>Pasa a otra voz y se presenta con ella, para que el usuario elija de oído.</summary>
    public void SwitchVoice(string which)
    {
        string label = body.ChangeVoice(which);
        body.Say(label.Length == 0
            ? "No tengo otra voz así instalada."
            : $"Esta es la voz {label}. Si te gusta, me quedo con ella. Si no, di «otra voz».");
    }

    public string Joke()
    {
        int pick;
        do pick = Random.Shared.Next(jokes.Length); while (pick == lastJoke);
        lastJoke = pick;
        return jokes[pick];
    }

    public void Rest()
    {
        Lessons.Unload();
        Knowledge.Unload();
    }

    // ---------- enseñar y recordar lo enseñado ----------

    private bool Teach(string plain, string norm)
    {
        if (teachSay.Match(plain) is { Success: true } say)
            return Learned(Reflexes.Normalize(say.Groups[1].Value), "decir", say.Groups[2].Value);
        if (teachDo.Match(plain) is { Success: true } act)
            return Learned(Reflexes.Normalize(act.Groups[1].Value), "hacer", act.Groups[2].Value);
        if (teachComma.Match(plain) is { Success: true } comma)
            return Learned(Reflexes.Normalize(comma.Groups[1].Value), "hacer", comma.Groups[2].Value);

        if (Regex.IsMatch(norm, "^(?:eso (?:no es|esta) (?:correcto|mal|verdad|cierto|asi)|eso es (?:falso|mentira)|te equivocas|te equivocaste|no es eso|(?:esa )?respuesta incorrecta|eso no)$"))
        {
            body.Say(Knowledge.ForgetLast()
                ? "Vale, eso que encontré lo borro. Todavía me cuesta entender bien algunas preguntas."
                : "Perdona. Dime qué estaba mal y lo corrijo.");
            return true;
        }
        if (forget.Match(norm) is { Success: true } gone)
        {
            string what = gone.Groups[1].Value;
            bool lesson = Lessons.Forget(what);
            int facts = lesson ? 0 : memory.Forget(what);
            body.Say(lesson ? "Hecho, esa frase ya no la sé." : facts > 0 ? "Hecho, lo he olvidado." : "No tenía nada sobre eso.");
            return true;
        }
        return false;
    }

    private bool Learned(string phrase, string kind, string value)
    {
        value = value.Trim().Trim('«', '»', '"', '“', '”', '.').Trim();
        if (phrase.Length == 0 || value.Length == 0) return false;
        Lessons.Teach(phrase, kind, value);
        lastTaught = phrase;
        body.Say(kind == "decir"
            ? $"Aprendido: cuando digas «{phrase}», responderé «{value}»."
            : $"Aprendido: cuando digas «{phrase}», haré «{value}».");
        return true;
    }

    /// <summary>«Deshaz la última lección» o «deshaz la lección de X»: vuelve a la versión anterior, o la olvida.</summary>
    private bool UndoLesson(string norm)
    {
        if (undo.Match(norm) is not { Success: true } m) return false;
        string? target = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value
            : m.Groups[2].Value.Trim().Length > 0 ? m.Groups[2].Value.Trim()
            : lastTaught;
        if (target == null)
        {
            body.Say("¿Cuál? Dime, por ejemplo, «deshaz la lección de modo fiesta».");
            return true;
        }
        if (!Lessons.Undo(target)) { body.Say("No tengo ninguna lección sobre eso."); return true; }
        body.Say(Lessons.Find(target) is { } back
            ? $"Deshecho. «{target}» volvió a su versión anterior."
            : $"Deshecho. Ya no sé qué hacer cuando dices «{target}».");
        return true;
    }

    private async Task<bool> Recall(string norm, bool exactOnly = false)
    {
        if (Lessons.Find(norm) is not { } lesson || (exactOnly && lesson.Frase != norm)) return false;
        if (lesson.Tipo == "decir")
        {
            body.Say(lesson.Valor);
            return true;
        }
        if (depth >= 3) return true;   // una lección que se llama a sí misma
        depth++;
        try
        {
            // "baila y luego cuéntame un chiste": cada paso es una frase que ya entiende.
            foreach (string step in Regex.Split(lesson.Valor, @"\s+(?:y\s+luego|luego|y\s+despu[eé]s|despu[eé]s)\s+", Loose))
                if (step.Trim().Length > 0) await mind.Hear(step);
        }
        finally { depth--; }
        return true;
    }

    // ---------- su aspecto y sus ajustes ----------

    private bool Settings(string norm)
    {
        if (rename.Match(norm) is { Success: true } named)
        {
            string name = char.ToUpperInvariant(named.Groups[1].Value[0]) + named.Groups[1].Value[1..];
            body.Rename(name);
            body.Say($"Me gusta. Desde ahora llámame {name}.");
            return true;
        }
        if (Regex.IsMatch(norm, "^(?:voz anterior|la voz de antes|vuelve a la voz (?:anterior|de antes))$"))
        {
            SwitchVoice("anterior");
            return true;
        }
        if (Regex.IsMatch(norm, "^(?:(?:pon|usa) la )?voz (?:de windows|clasica|robotica)$"))
        {
            SwitchVoice("windows");
            return true;
        }
        if (Regex.IsMatch(norm, "^(?:me gusta (?:esta|esa) voz|(?:esta|esa) voz me gusta|quedate con (?:esta|esa)(?: voz)?|me quedo con (?:esta|esa)(?: voz)?|(?:esta|esa) voz esta bien)$"))
        {
            body.Say("¡Hecho! Me quedo con esta voz.");
            return true;
        }
        if (norm is "voz normal" or "habla normal")
        {
            body.TuneVoice(0, 0, reset: true);
            body.Say("Vuelvo a mi voz de siempre.");
            return true;
        }
        if (voice.Match(norm) is { Success: true } tune)
        {
            switch (tune.Groups[1].Value)
            {
                case "aguda": body.TuneVoice(1, 0); break;
                case "grave": body.TuneVoice(-1, 0); break;
                case "rapido" or "rapida": body.TuneVoice(0, 1); break;
                default: body.TuneVoice(0, -1); break;
            }
            body.Say("¿Qué tal así?");
            return true;
        }
        return false;
    }

    // ---------- avisos ----------

    /// <summary>"Avísame en 10 minutos", "recuérdame que llame a Juan en media hora".</summary>
    public bool Remind(string norm)
    {
        string phrase = norm.Replace("una hora y media", "90 minutos").Replace("hora y media", "90 minutos")
                            .Replace("media hora", "30 minutos").Replace("un cuarto de hora", "15 minutos")
                            .Replace("minuto y medio", "90 segundos");
        string amountText, unit, about;
        if (remindLater.Match(phrase) is { Success: true } later)
            (about, amountText, unit) = (later.Groups[1].Value, later.Groups[2].Value, later.Groups[3].Value);
        else if (remind.Match(phrase) is { Success: true } m)
            (amountText, unit, about) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        else if (remindWord.Match(norm) is { Success: true } asked && TryClock(asked.Groups[1].Value, out DateTime at, out string what))
        {
            // «recuérdame a las cinco de la tarde que llame al banco»
            body.Say($"Hecho, {AlarmAt(at, what)}.");
            return true;
        }
        else return false;
        if (!Numbers.TryParse(amountText, out double amount) || amount <= 0) return false;

        var delay = unit.StartsWith('s') ? TimeSpan.FromSeconds(amount)
                  : unit.StartsWith('m') ? TimeSpan.FromMinutes(amount)
                  : TimeSpan.FromHours(amount);
        if (delay > TimeSpan.FromHours(24)) return false;

        string span = $"{Numbers.Format(amount)} {unit}";
        about = Second(about);
        FireLater(DateTime.Now + delay,
            about.Length > 0 ? $"¡Aviso! Ya pasaron {span}: {about}." : $"¡Aviso! Ya pasaron {span}.", about);
        body.Say($"Hecho, te aviso en {span}.");
        return true;
    }

    private static readonly Regex remindWord = new(@"^(?:avisame|recuerdame|despiertame|pon(?:me)? (?:una |un )?(?:alarma|aviso|recordatorio))\b ?(.*)$");
    private static readonly Regex clock = new(
        @"\b(?:(?<day>hoy|manana) )?a la(?:s)? (?<h>\d{1,2}|una|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|once|doce)" +
        @"(?: (?<m>\d{2})| y (?<y>media|cuarto|[a-z0-9]+)| menos (?<less>cuarto|[a-z0-9]+))?(?: en punto)?" +
        @"(?: (?:de|por) la (?<part>manana|tarde|noche|madrugada)| (?<part>am|pm))?(?: de (?<day2>hoy|manana))?");
    private static readonly Regex howLong = new(@"^(?:en |dentro de )?(.+?) (segundos?|minutos?|horas?)$");

    /// <summary>
    /// Entiende una hora dicha de palabra («a las cinco y media de la tarde», «mañana a las 8», «a las 17 30») y
    /// devuelve la próxima vez que será esa hora, y lo que queda de la frase.
    /// </summary>
    internal static bool TryClock(string norm, out DateTime at, out string rest)
    {
        (at, rest) = (default, "");
        if (clock.Match(norm) is not { Success: true } m || !Numbers.TryParse(m.Groups["h"].Value, out double hours)) return false;
        int h = (int)hours, min = 0;
        static int Part(string word) => word switch { "media" => 30, "cuarto" => 15, _ => Numbers.TryParse(word, out double v) ? (int)v : -1 };
        if (m.Groups["m"].Success) min = int.Parse(m.Groups["m"].Value);
        else if (m.Groups["y"].Success) min = Part(m.Groups["y"].Value);
        else if (m.Groups["less"].Success) (h, min) = (h - 1, 60 - Part(m.Groups["less"].Value));
        string part = m.Groups["part"].Value;
        if (part is "tarde" or "noche" or "pm" && h < 12) h += 12;
        if (part is "manana" or "madrugada" or "am" && h == 12) h = 0;
        if (h is < 0 or > 23 || min is < 0 or > 59) return false;

        var now = DateTime.Now;
        at = now.Date.AddHours(h).AddMinutes(min);
        if (m.Groups["day"].Value == "manana" || m.Groups["day2"].Value == "manana") at = at.AddDays(1);
        // «a las cinco», sin decir de qué: la próxima vez que den las cinco, sea de la tarde hoy o de la mañana siguiente.
        else if (at <= now) at = part.Length == 0 && h < 12 && at.AddHours(12) > now ? at.AddHours(12) : at.AddDays(1);
        rest = Regex.Replace($"{norm[..m.Index]} {norm[(m.Index + m.Length)..]}".Trim(), @"^(?:(?:que|de|para|lo de) )+", "");
        return true;
    }

    /// <summary>Deja puesto un aviso para una hora y devuelve cómo contarlo: «te aviso a las 17:00».</summary>
    private string AlarmAt(DateTime at, string about)
    {
        about = Second(about.Trim());
        FireLater(at, about.Length > 0 ? $"¡Aviso! Son las {at:H:mm}. Me pediste que te recordara: {about}." : $"¡Aviso! Son las {at:H:mm}.", about);
        return $"te aviso {(at.Date > DateTime.Today ? "mañana " : "")}a las {at:H:mm}";
    }

    /// <summary>
    /// Programa un aviso para una hora absoluta, guardándolo en disco: si Chispa se cierra antes,
    /// al volver se reprograma solo. Al cumplirse, se dice el mensaje y se borra.
    /// </summary>
    private void FireLater(DateTime when, string message, string about)
    {
        var aviso = saved.Add(when, message, about);
        body.Later(when - DateTime.Now, () =>
        {
            body.Act("aparecer");
            body.Say(message);
            saved.Remove(aviso.Id);
        });
    }

    /// <summary>
    /// Al arrancar: devuelve a la vida los avisos que sobrevivieron al cierre. Los futuros se reprograman;
    /// los que vencieron hace menos de un día se cuentan ahora, con su mensaje; los más viejos ya se descartaron.
    /// </summary>
    public void RestoreReminders()
    {
        var now = DateTime.Now;
        foreach (var aviso in saved.Pending(now))
        {
            if (aviso.Cuando <= now)
            {
                saved.Remove(aviso.Id);
                body.Say(aviso.Sobre.Length > 0
                    ? $"¡Aviso! Estaba pendiente: me pediste que te recordara: {aviso.Sobre}."
                    : "¡Aviso! Tenía un aviso pendiente de antes de cerrarme.");
            }
            else
            {
                body.Later(aviso.Cuando - now, () =>
                {
                    body.Act("aparecer");
                    body.Say(aviso.Mensaje);
                    saved.Remove(aviso.Id);
                });
            }
        }
    }

    /// <summary>
    /// Para el modelo: programa un aviso «en 10 minutos» o «a las 17:30». Devuelve lo que pasó, o null si no entiende cuándo.
    /// </summary>
    public string? Schedule(string when, string about)
    {
        string norm = Reflexes.Normalize(when).Replace("una hora y media", "90 minutos").Replace("hora y media", "90 minutos")
            .Replace("media hora", "30 minutos").Replace("un cuarto de hora", "15 minutos");
        if (howLong.Match(norm) is { Success: true } span && Numbers.TryParse(span.Groups[1].Value, out double amount) && amount > 0)
        {
            string unit = span.Groups[2].Value;
            var delay = unit.StartsWith('s') ? TimeSpan.FromSeconds(amount) : unit.StartsWith('m') ? TimeSpan.FromMinutes(amount) : TimeSpan.FromHours(amount);
            if (delay > TimeSpan.FromDays(7)) return null;
            return $"Hecho: {AlarmAt(DateTime.Now + delay, about)}.";
        }
        if (!norm.Contains("a la")) norm = "a las " + norm;   // «17:30» o «cinco de la tarde», sin el «a las»
        return TryClock(norm, out DateTime at, out _) ? $"Hecho: {AlarmAt(at, about)}." : null;
    }

    // ---------- cálculo ----------

    private bool Calculate(string plain)
    {
        // Sobre el texto original: al normalizar se perderían los decimales ("3,5").
        if (!Numbers.TryEvaluate(plain, out double value, out bool hadOperator) || !hadOperator) return false;
        body.Say(double.IsFinite(value) ? $"Da {Numbers.Format(value)}." : "Eso no se puede calcular: no se divide entre cero.");
        return true;
    }

    // ---------- repartos con condiciones (lo resuelve el motor lógico, no ella) ----------

    /// <summary>"Organiza dentista, banco y taller en lunes, martes y miércoles; el dentista no puede el lunes".</summary>
    private bool Organize(string plain)
    {
        string[] parts = Regex.Split(plain.Trim(), @"\s*[;:.\n]+\s*").Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0 || organize.Match(parts[0]) is not { Success: true } asked) return false;
        string[] things = Allocation.List(asked.Groups[1].Value), places = Allocation.List(asked.Groups[2].Value);
        if (things.Length < 2 || places.Length < 2) return false;   // «ordena mi escritorio en carpetas» no es un reparto
        body.Say(Allocation.Answer(things, places, parts.Skip(1), mind.TurnCancellation));
        return true;
    }

    // ---------- el equipo (lo que cambia algo pide confirmación hablada) ----------

    internal static string? OpenDestination(string plain)
    {
        var match = Regex.Match(plain.Trim().TrimStart('¿'), @"^(?:por favor[, ]+)?(?:(?:puedes|podr[ií]as)\s+(?:me\s+)?|me\s+(?:puedes|podr[ií]as)\s+|(?:quiero|necesito)\s+(?:que\s+(?:me\s+)?)?|me\s+)?(?:abre|[aá]breme|abrir|abrirme|abras|abres|inicia|iniciar|mu[eé]strame|ve a|entra a|entra en)\s+(.+?)(?:[, ]+por favor)?[?!]*$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>Lo que el usuario pide cerrar con sus palabras («cierra el navegador»), o null si no es una orden de cerrar.</summary>
    internal static string? CloseDestination(string plain)
    {
        var match = Regex.Match(plain.Trim().TrimStart('¿'), @"^(?:por favor[, ]+)?(?:(?:puedes|podr[ií]as)\s+(?:me\s+)?|me\s+(?:puedes|podr[ií]as)\s+|(?:quiero|necesito)\s+(?:que\s+(?:me\s+)?)?|me\s+)?(?:cierra|ci[eé]rrame|cerrar|cierral[oa]|cierre[s]?|cerral[oa])\s+(.+?)(?:[, ]+por favor)?[?!]*$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private async Task<bool> Device(string plain, string norm)
    {
        if (Regex.IsMatch(norm, @"(?:que|cual) ventana|que estoy haciendo|que tengo abierto|en que estoy (?:trabajando|ahora)|que programa (?:uso|tengo)"))
        {
            body.Say(ToolBox.ActiveWindow() is { } window ? $"Tienes delante {window}." : "Ahora mismo no veo ninguna ventana activa.");
            return true;
        }
        if (Regex.IsMatch(norm, @"\b(?:que|cuales|cuantos|cuantas) (?:programas|ventanas|aplicaciones|apps|cosas) (?:tengo|hay|estan) abiert[oa]s\b"))
        {
            var windows = body.OpenWindows();
            body.Say(windows.Count == 0 ? "No veo ninguna ventana abierta."
                : $"Tienes {windows.Count} ventanas abiertas: {string.Join("; ", windows.Take(6).Select(w => w.Title.Length > 40 ? w.Title[..40] + "…" : w.Title))}.");
            return true;
        }
        if (Regex.IsMatch(norm, @"\b(?:espacio|sitio)\b.*\b(?:disco|ordenador|equipo|pc|computadora)\b|\bcuanto disco\b"))
        {
            body.Say(ToolBox.Disk());
            return true;
        }
        if (Regex.IsMatch(norm, @"\bbateria\b|cuanta carga"))
        {
            body.Say($"La batería está así: {ToolBox.Battery()}.");
            return true;
        }
        if (Regex.IsMatch(norm, @"^(?:donde estoy|donde estamos|en que pais (?:estoy|estamos)|cual es mi ubicacion|donde me encuentro)$"))
        {
            body.Say($"Según tu equipo, estás en {ToolBox.Region()}. No sé afinar más sin internet.");
            return true;
        }

        if (webSearch.Match(norm) is { Success: true } search)
        {
            string site = search.Groups[2].Value == "youtube" ? "https://www.youtube.com/results?search_query=" : "https://www.google.com/search?q=";
            return await Tool("abrir", new { destino = site + Uri.EscapeDataString(search.Groups[1].Value) });
        }
        if (screenAsk.IsMatch(norm))
        {
            // Lo que hay en pantalla solo lo puede mirar un modelo que vea; la foto se hace ahora y no se guarda.
            body.Thinking(true);
            try { body.Say(await mind.See(plain)); }
            finally { body.Thinking(false); }
            return true;
        }
        if (open.Match(norm) is { Success: true } app)
        {
            string destination = OpenDestination(plain) ?? app.Groups[1].Value;
            // Si ella no sabe qué es («el programa de editar fotos») y hay un modelo que entiende la frase, que lo resuelva él.
            if (mind.HasBigBrain && ToolBox.ResolveTarget(destination) == null) return false;
            return await Tool("abrir", new { destino = destination }, norm, destination);
        }
        if (doneWith.Match(norm) is { Success: true } done) return await Tool("cerrar", new { ventana = done.Groups[1].Value }, norm, done.Groups[1].Value);
        if (close.Match(norm) is { Success: true } shut) return await Tool("cerrar", new { ventana = shut.Groups[1].Value }, norm, shut.Groups[1].Value);
        if (volume.Match(norm) is { Success: true } vol)
            return await Tool("volumen", new { accion = vol.Groups[1].Value is "sube" or "subele" or "aumenta" ? "subir" : "bajar" });
        if (mute.IsMatch(norm)) return await Tool("volumen", new { accion = "silenciar" });
        if (mediaToggle.IsMatch(norm)) return await Tool("multimedia", new { accion = "pausa_reproducir" });
        if (mediaNext.IsMatch(norm)) return await Tool("multimedia", new { accion = "siguiente" });
        if (mediaPrev.IsMatch(norm)) return await Tool("multimedia", new { accion = "anterior" });
        if (typeText.Match(plain) is { Success: true } typed)
            return await Tool("escribir_texto", new { texto = typed.Groups[1].Value.Trim('«', '»', '"', '“', '”'), enviar = typed.Groups[2].Success });
        if (music.Match(norm) is { Success: true } song)
            return await Tool("abrir", new { destino = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(song.Groups[1].Value) });
        return false;
    }

    /// <summary>"¿Qué tiempo hace?": usa la ciudad que le digan o la que sepa que es la del usuario.</summary>
    private async Task<bool> Weather(string norm)
    {
        if (weather.Match(norm) is not { Success: true } m) return false;
        string city = m.Groups[2].Value;
        if (city.Length == 0 && memory.Answer("donde vivo ciudad") is { } home && Regex.Match(home, @"\bviv[oe] en (.+)$", Loose) is { Success: true } lives)
            city = lives.Groups[1].Value.TrimEnd('.');
        if (city.Length == 0)
        {
            body.Say("¿De qué ciudad? Pregúntame, por ejemplo, «qué tiempo hace en Santo Domingo». Y si me dices «vivo en…», ya no tendrás que repetirlo.");
            return true;
        }
        body.Thinking(true);
        try
        {
            bool tomorrow = m.Groups[1].Value == "manana" || m.Groups[3].Value == "manana";
            body.Say(await ToolBox.Weather(city, tomorrow) ?? $"No encontré ninguna ciudad que se llame {city}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            body.Say("Ahora mismo no tengo internet para mirar el tiempo.");
        }
        finally { body.Thinking(false); }
        return true;
    }

    private async Task<bool> Tool(string name, object args, string norm = "", string said = "")
    {
        string result = await ToolBox.Run(name, JsonSerializer.SerializeToElement(args), mind, parsedRequest: true);
        // Plan-ejecutar-verificar-corregir: si no encontró lo que pedían, propone lo más parecido y, si el usuario
        // acepta con un «sí», aprende la frase corregida. La lección solo se guarda con su aprobación.
        if (name == "abrir" && result == ToolBox.NotFound && ToolBox.ClosestApp(said) is { } app)
        {
            proposal = (norm, "hacer", $"abre {app.Name}");
            proposalUntil = DateTime.UtcNow.AddSeconds(45);
            body.Say($"No lo encuentro. ¿Quieres que cuando digas «{norm}» abra {app.Label}? Di «sí» y me lo aprendo.");
            return true;
        }
        if (name == "cerrar" && result.StartsWith("No encuentro ninguna ventana así") && ToolBox.ClosestWindow(body.OpenWindows(), said) is { } window)
        {
            proposal = (norm, "hacer", $"cierra {window.Title}");
            proposalUntil = DateTime.UtcNow.AddSeconds(45);
            body.Say($"No encuentro ninguna ventana así. ¿Quieres que cuando digas «{norm}» cierre «{window.Title}»? Di «sí» y me lo aprendo.");
            return true;
        }
        body.Say(result == ToolBox.Denied ? "Vale, no hago nada."
            : result == ToolBox.NotFound ? "No encuentro eso para abrirlo. Dime el nombre del programa, una carpeta como Descargas o una página web."
            : result);
        return true;
    }

    // ---------- charla ----------

    private bool Chat(string norm, bool overheard)
    {
        // Un "sí", un "vale" o un "gracias" oídos de fondo no eran para ella.
        if (overheard && norm.Split(' ').Length < 2) return false;
        if (goodbye.IsMatch(norm))
        {
            body.Act("saludar");
            body.Converse(false);
            body.Say(Random.Shared.Next(2) == 0 ? "¡Hasta luego!" : "¡Nos vemos!");
            return true;
        }
        if (Regex.IsMatch(norm, @"\bmoneda\b|cara o cruz"))
        {
            body.Say(Random.Shared.Next(2) == 0 ? "Salió cara." : "Salió cruz.");
            return true;
        }
        if (Regex.IsMatch(norm, @"\bdado\b"))
        {
            body.Say($"Salió un {Random.Shared.Next(1, 7)}.");
            return true;
        }
        if (randomNumber.Match(norm) is { Success: true } range && int.TryParse(range.Groups[1].Value, out int low) && int.TryParse(range.Groups[2].Value, out int high) && low < high)
        {
            body.Say($"El {Random.Shared.Next(low, high + 1)}.");
            return true;
        }
        foreach (var (when, replies, gesture) in smallTalk)
        {
            if (!when.IsMatch(norm)) continue;
            if (gesture != null) body.Act(gesture);
            body.Say(replies[Random.Shared.Next(replies.Length)]);
            return true;
        }
        return false;
    }

    // ---------- lo que aprende de ti ----------

    /// <summary>"Me llamo Ana", "mi color favorito es el azul": lo apunta sin que se lo pidan.</summary>
    private bool LearnFact(string plain, string norm)
    {
        // «Bueno, ya no: ahora mi color favorito es el azul»: el dato empieza después de las muletillas.
        string core = leadIn.Replace(plain.Trim(), "");
        if (core.Length < plain.Trim().Length) (plain, norm) = (core, Reflexes.Normalize(core));
        if (plain.Contains('?') || question.IsMatch(norm) || !durable.IsMatch(norm)) return false;
        string fact = plain.Trim().TrimEnd('.', '!');
        bool corrected = false;
        if (subject.Match(Reflexes.Normalize(fact)) is { Success: true } about) corrected = memory.Replace(about.Groups[1].Value, fact);
        else memory.Add(fact);
        body.Say(corrected ? $"Actualizado: ahora {Second(fact)}." : $"Anotado: {Second(fact)}.");
        return true;
    }

    // Una pregunta («qué», «a quién», «cuándo», «recuerdas»…) sobre algo ya hablado («te dije», «hablamos», «te conté»…).
    private static readonly Regex pastTalk = new(
        @"^(?:que|de que|cuando|quien|a quien|donde|adonde|cual|cuanto|recuerdas|te acuerdas|sabes)\b.*\b(?:hablamos|te dije|te pedi|te conte|te comente|dijimos|hicimos)\b");

    /// <summary>
    /// «¿Qué hablamos ayer?», «¿qué te dije del informe?»: lo busca en el diario. Con un modelo delante lo cuenta él,
    /// que sabe resumir; sin modelo, lee las entradas tal cual.
    /// </summary>
    private bool Remember(string norm)
    {
        if (!pastTalk.IsMatch(norm) || mind.HasBigBrain) return false;
        var range = Journal.When(norm);
        var found = mind.Journal.Search(norm, range?.From, range?.To, max: 3);
        string when = range?.Label ?? "";
        body.Say(found.Count == 0
            ? (when.Length > 0 ? $"No tengo nada apuntado de {when}." : "No encuentro nada de eso en mi diario.")
            : $"{(when.Length > 0 ? $"De {when} tengo apuntado" : "Tengo apuntado")} que me dijiste: " +
              string.Join("; ", found.Select(e => $"«{(e.Dijo.Length > 90 ? e.Dijo[..90] + "…" : e.Dijo)}»")) + ".");
        return true;
    }

    /// <summary>
    /// «¿De qué hemos hablado últimamente?»: lista los temas que más salen en el diario. Con un modelo delante lo
    /// cuenta él, que sabe resumir; sin modelo, los enumera tal cual.
    /// </summary>
    private bool TopicsTalk(string norm)
    {
        if (!Journal.AsksTopics(norm) || mind.HasBigBrain) return false;
        var range = Journal.When(norm);
        var topics = mind.Journal.Topics(range?.From, range?.To, max: 6);
        string span = range?.Label ?? "todo lo que llevamos hablado";
        body.Say(topics.Count == 0
            ? $"No tengo casi nada apuntado de {span}."
            : $"De {span}, lo que más ha salido: {string.Join(", ", topics.Select(t => $"{t.Tema} ({t.Veces} veces)"))}.");
        return true;
    }

    /// <summary>«¿Cuántas veces te he hablado del informe?»: cuenta las entradas del diario. Con modelo, lo cuenta él.</summary>
    private bool CountTalks(string norm)
    {
        if (Journal.CountTopic(norm) is not { } topic || mind.HasBigBrain) return false;
        var range = Journal.When(norm);
        int n = topic.Length > 0 ? mind.Journal.Count(topic, range?.From, range?.To) : mind.Journal.Read(range?.From, range?.To).Count;
        string span = range?.Label ?? "todo lo que llevamos apuntado";
        string about = topic.Length > 0 ? $" sobre {topic}" : "";
        body.Say(n == 0
            ? $"De {span}{about} no tengo ninguna entrada."
            : $"De {span}{about} tengo {n} {(n == 1 ? "entrada" : "entradas")}.");
        return true;
    }

    private bool AnswerAboutUser(string norm)
    {
        if (!question.IsMatch(norm) || !personal.IsMatch(norm)) return false;
        string? fact = memory.Answer(norm);
        // Que lleve un «me» o un «tengo» no la hace una pregunta sobre lo que le han contado («¿cuánto espacio me queda
        // en el disco?»). Si no hay recuerdo que la conteste y hay un modelo que la entienda, que decida él.
        if (fact == null && mind.HasBigBrain) return false;
        body.Say(fact != null
            ? $"Me dijiste que {Second(fact)}."
            : "Eso todavía no me lo has contado. Si me lo dices, lo recuerdo.");
        return true;
    }

    // ---------- investigar por su cuenta ----------

    /// <summary>"Qué es…", "quién fue…", "háblame de…": lo busca directamente.</summary>
    private async Task<bool> LookUp(string norm) =>
        lookup.Match(norm) is { Success: true } m && await Research(Keywords(m.Groups[1].Value));

    /// <summary>Las palabras con contenido de una pregunta: lo que hay que buscar.</summary>
    private static string[] Keywords(string norm) =>
        norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 && !emptyWords.Contains(w)).ToArray();

    /// <summary>
    /// Busca la respuesta en Wikipedia, se queda con las frases que mejor contestan y las guarda:
    /// la próxima vez que le pregunten lo mismo ya lo sabe, sin internet. Devuelve false si no había qué buscar.
    /// </summary>
    /// <param name="why">La pregunta era un "¿por qué…?": se prefieren las frases que dan una causa.</param>
    private async Task<bool> Research(string[] keys, bool why = false)
    {
        if (keys.Length == 0) return false;
        string topic = string.Join(' ', keys);
        if (Knowledge.Find(topic) is { } known)
        {
            body.Say(known);
            return true;
        }

        body.Thinking(true);
        try
        {
            var found = await ToolBox.Wikipedia(topic, count: 4);
            if (found.Count == 0)
            {
                body.Say("Lo busqué y no encontré nada sobre eso.");
                return true;
            }
            var (title, sentence) = BestAnswer(found, keys, why);
            // Si la frase no nombra de qué habla, lo dice ella: así se nota enseguida cuando encontró otra cosa.
            string answer = Reflexes.Normalize(sentence).StartsWith(Reflexes.Normalize(title).Split(' ')[0])
                ? sentence
                : $"Encontré esto sobre {title}: {sentence}";
            Knowledge.Learn(topic, answer);
            body.Say($"{answer} Lo busqué en Wikipedia.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            body.Say("Eso no lo sé, y ahora mismo no tengo internet para buscarlo.");
        }
        finally { body.Thinking(false); }
        return true;
    }

    /// <summary>De lo encontrado, la frase que más palabras de la pregunta contiene (y la siguiente, si es corta).</summary>
    private static (string Title, string Sentence) BestAnswer(List<(string Title, string Extract)> found, string[] keys, bool why)
    {
        var wanted = keys.Select(Stem).ToHashSet();
        string best = "", bestTitle = found[0].Title;
        double bestScore = -1;
        for (int page = 0; page < found.Count; page++)
        {
            // Limpieza para decirlo en voz alta: fuera paréntesis (fechas, pronunciaciones), marcas invisibles
            // y los puntos sueltos que dejan las notas al pie ("La. capital de Francia").
            string clean = Regex.Replace(found[page].Extract.Replace("\n", " "), @"\s*\([^)]*\)|[​-‍⁠﻿]", "");
            clean = Regex.Replace(clean, @"(?<=\p{Ll})\.(?=\s+\p{Ll})|\s+(?=[.,;:])", "");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            // Corta en los puntos, salvo tras una inicial ("J. P. Duarte").
            string[] sentences = Regex.Split(clean, @"(?<!\b\p{Lu})(?<=[.!?])\s+(?=[\p{Lu}¿¡])").Where(s => s.Length > 20).ToArray();
            for (int i = 0; i < sentences.Length; i++)
            {
                string plain = Reflexes.Normalize(sentences[i]);
                var words = plain.Split(' ').Select(Stem).ToHashSet();
                double score = wanted.Count(words.Contains) + (page == 0 ? 0.5 : 0) + (i == 0 ? 0.3 : 0);
                if (why && Regex.IsMatch(plain, @"\b(?:debido|porque|causa|causado|se debe|ya que|razon|por eso|provoca|hace que)\b")) score += 1.5;
                if (score <= bestScore) continue;
                bestScore = score;
                bestTitle = found[page].Title;
                best = sentences[i];
                if (best.Length < 140 && i + 1 < sentences.Length) best += " " + sentences[i + 1];
            }
        }
        return (bestTitle, best.Length > 330 ? best[..327].TrimEnd() + "…" : best);

        static string Stem(string word) => word.Length <= 3 ? word : word[..Math.Min(word.Length - 1, 5)];
    }

    // ---------- utilidades ----------

    private static Regex Rx(string pattern) => new($"^(?:{pattern})$", RegexOptions.CultureInvariant);

    private static readonly (string From, string To)[] firstToSecond =
    [
        ("me llamo", "te llamas"), ("mi", "tu"), ("mis", "tus"), ("me", "te"), ("yo", "tú"), ("mío", "tuyo"),
        ("mía", "tuya"), ("conmigo", "contigo"), ("soy", "eres"), ("estoy", "estás"), ("tengo", "tienes"), ("vivo", "vives"),
        ("prefiero", "prefieres"), ("quiero", "quieres"), ("puedo", "puedes"), ("voy", "vas"), ("nací", "naciste"), ("naci", "naciste"),
        ("odio", "odias"), ("hago", "haces"), ("cumplo", "cumples"), ("necesito", "necesitas"),
    ];

    // Datos apuntados en tercera persona ("Le gusta el café", "Se llama Ana"): hablan del usuario solo si la frase no va en primera.
    private static readonly (string From, string To)[] thirdToSecond =
    [
        ("se llama", "te llamas"), ("le", "te"), ("su", "tu"), ("sus", "tus"), ("tiene", "tienes"), ("vive", "vives"),
        ("trabaja", "trabajas"), ("prefiere", "prefieres"),
    ];

    // Palabras que son verbo ("trabajo en…") o nombre ("mi trabajo"): solo se conjugan si no llevan determinante delante.
    private static readonly (string From, string To)[] verbOrNoun = [("trabajo", "trabajas"), ("juego", "juegas"), ("estudio", "estudias"), ("uso", "usas")];

    /// <summary>Pasa a segunda persona lo que el usuario dijo de sí mismo: "mi color favorito" → "tu color favorito".</summary>
    internal static string Second(string fact)
    {
        string text = fact.Trim();
        if (text.Length == 0) return "";
        string spoken = text;
        foreach (var (from, to) in verbOrNoun)
            text = Regex.Replace(text, $@"(?<!\b(?:mi|tu|el|un|del|al|este|ese)\s)\b{from}\b", to, RegexOptions.IgnoreCase);
        text = Swap(text, firstToSecond);
        // Si habló de sí mismo ("mi hermana se llama Ana"), lo que va en tercera persona es de otro y se queda como está.
        if (text == spoken) text = Swap(text, thirdToSecond);
        return char.ToLowerInvariant(text[0]) + text[1..];

        static string Swap(string text, (string From, string To)[] pairs)
        {
            foreach (var (from, to) in pairs)
                text = Regex.Replace(text, $@"(?<![\p{{L}}]){Regex.Escape(from)}(?![\p{{L}}])", to, RegexOptions.IgnoreCase);
            return text;
        }
    }
}

/// <summary>Números y cuentas dichos en español: "doce por ocho", "el 25 por ciento de 80", "raíz cuadrada de 144".</summary>
internal static class Numbers
{
    private static readonly Dictionary<string, int> words = new()
    {
        ["cero"] = 0, ["un"] = 1, ["uno"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5, ["seis"] = 6,
        ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9, ["diez"] = 10, ["once"] = 11, ["doce"] = 12, ["trece"] = 13, ["catorce"] = 14,
        ["quince"] = 15, ["dieciseis"] = 16, ["diecisiete"] = 17, ["dieciocho"] = 18, ["diecinueve"] = 19, ["veinte"] = 20,
        ["veintiun"] = 21, ["veintiuno"] = 21, ["veintiuna"] = 21, ["veintidos"] = 22, ["veintitres"] = 23, ["veinticuatro"] = 24,
        ["veinticinco"] = 25, ["veintiseis"] = 26, ["veintisiete"] = 27, ["veintiocho"] = 28, ["veintinueve"] = 29, ["treinta"] = 30,
        ["cuarenta"] = 40, ["cincuenta"] = 50, ["sesenta"] = 60, ["setenta"] = 70, ["ochenta"] = 80, ["noventa"] = 90, ["cien"] = 100,
        ["ciento"] = 100, ["doscientos"] = 200, ["trescientos"] = 300, ["cuatrocientos"] = 400, ["quinientos"] = 500,
        ["seiscientos"] = 600, ["setecientos"] = 700, ["ochocientos"] = 800, ["novecientos"] = 900,
    };

    public static string Format(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>Un número suelto: "10", "3,5", "veinticinco", "dos mil quinientos".</summary>
    public static bool TryParse(string text, out double value)
    {
        string[] tokens = Tokens(text);
        int at = 0;
        return Number(tokens, ref at, out value) && at == tokens.Length;
    }

    /// <summary>Calcula una cuenta dicha con palabras o con signos, con o sin "cuánto es" delante.</summary>
    public static bool TryEvaluate(string text, out double value, out bool hadOperator)
    {
        string[] tokens = Tokens(text);
        int at = 0;
        while (at < tokens.Length && tokens[at] is "cuanto" or "cuantos" or "da" or "vale" or "seria" or "dime" or "calcula" or "calculame" or "que") at++;
        hadOperator = false;
        value = 0;
        return at < tokens.Length && Sum(tokens, ref at, out value, ref hadOperator) && at == tokens.Length;
    }

    private static string[] Tokens(string text)
    {
        string clean = Regex.Replace(text.ToLowerInvariant().Normalize(NormalizationForm.FormD), @"\p{Mn}", "");
        clean = clean.Replace("por favor", " ");
        clean = Regex.Replace(clean, @"(?<=\d)[.,](?=\d)", "·");              // la coma o el punto decimal
        clean = Regex.Replace(clean, @"(?<=\d)\s*x\s*(?=\d)", " * ");         // "3 x 4"
        clean = Regex.Replace(clean, @"([+\-*/^%])", " $1 ");
        clean = Regex.Replace(clean, @"[^a-z0-9·+\-*/^% ]", " ");
        return clean.Replace('·', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t is not ("el" or "la" or "los" or "las" or "es" or "son" or "igual" or "a")).ToArray();
    }

    private static bool Is(string[] t, int at, params string[] expected)
    {
        if (at + expected.Length > t.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (t[at + i] != expected[i]) return false;
        return true;
    }

    private static bool Sum(string[] t, ref int at, out double value, ref bool op)
    {
        if (!Product(t, ref at, out value, ref op)) return false;
        while (at < t.Length)
        {
            int sign = Is(t, at, "mas") || Is(t, at, "+") ? 1 : Is(t, at, "menos") || Is(t, at, "-") ? -1 : 0;
            if (sign == 0) break;
            at++;
            if (!Product(t, ref at, out double right, ref op)) return false;
            value += sign * right;
            op = true;
        }
        return true;
    }

    private static bool Product(string[] t, ref int at, out double value, ref bool op)
    {
        if (!Factor(t, ref at, out value, ref op)) return false;
        while (at < t.Length)
        {
            bool times, divide = false;
            int skip;
            if (Is(t, at, "por", "ciento")) break;
            if (Is(t, at, "multiplicado", "por")) (times, skip) = (true, 2);
            else if (Is(t, at, "por") || Is(t, at, "*") || Is(t, at, "x") || Is(t, at, "veces")) (times, skip) = (true, 1);
            else if (Is(t, at, "dividido", "entre") || Is(t, at, "dividido", "por")) (times, divide, skip) = (false, true, 2);
            else if (Is(t, at, "entre") || Is(t, at, "/") || Is(t, at, "sobre")) (times, divide, skip) = (false, true, 1);
            else break;
            at += skip;
            if (!Factor(t, ref at, out double right, ref op)) return false;
            value = times ? value * right : divide ? value / right : value;
            op = true;
        }
        return true;
    }

    private static bool Factor(string[] t, ref int at, out double value, ref bool op)
    {
        value = 0;
        if (Is(t, at, "raiz", "cuadrada", "de")) return Unary(t, ref at, 3, Math.Sqrt, out value, ref op);
        if (Is(t, at, "mitad", "de")) return Unary(t, ref at, 2, v => v / 2, out value, ref op);
        if (Is(t, at, "doble", "de")) return Unary(t, ref at, 2, v => v * 2, out value, ref op);
        if (Is(t, at, "triple", "de")) return Unary(t, ref at, 2, v => v * 3, out value, ref op);
        if (!Number(t, ref at, out value)) return false;

        if (Is(t, at, "por", "ciento", "de") || Is(t, at, "%", "de"))
        {
            at += Is(t, at, "%", "de") ? 2 : 3;
            if (!Factor(t, ref at, out double whole, ref op)) return false;
            value = value / 100 * whole;
            op = true;
        }
        else if (Is(t, at, "al", "cuadrado")) { at += 2; value *= value; op = true; }
        else if (Is(t, at, "al", "cubo")) { at += 2; value = value * value * value; op = true; }
        else if (Is(t, at, "elevado") || Is(t, at, "^"))
        {
            at++;
            if (!Factor(t, ref at, out double power, ref op)) return false;
            value = Math.Pow(value, power);
            op = true;
        }
        return true;
    }

    private static bool Unary(string[] t, ref int at, int skip, Func<double, double> apply, out double value, ref bool op)
    {
        at += skip;
        if (!Factor(t, ref at, out value, ref op)) return false;
        value = apply(value);
        op = true;
        return true;
    }

    /// <summary>Lee un número en cifras o en palabras a partir de la posición dada.</summary>
    private static bool Number(string[] t, ref int at, out double value)
    {
        value = 0;
        if (at >= t.Length) return false;
        if (double.TryParse(t[at], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            at++;
            return true;
        }

        double total = 0, group = 0;
        bool any = false;
        while (at < t.Length)
        {
            if (words.TryGetValue(t[at], out int n)) group += n;
            else if (t[at] == "mil")
            {
                total += (group == 0 ? 1 : group) * 1000;
                group = 0;
            }
            else if (t[at] == "y" && any && at + 1 < t.Length && words.ContainsKey(t[at + 1])) { }
            else break;
            any = true;
            at++;
        }
        value = total + group;
        return any;
    }
}
