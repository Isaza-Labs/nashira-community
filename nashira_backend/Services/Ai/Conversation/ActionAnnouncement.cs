using System.Text.RegularExpressions;

namespace nashira_backend.Services.Ai.Conversation;

// A round that says it is about to act ("Verifico el estado real…", "Creating the 8
// interfaces.") and then ends without calling a tool. The loop reads a round with no
// tool calls as the final answer, so such a turn used to be recorded as completed
// while nothing had been read or changed; in one long NetBox load, 55 turns ended
// this way, some of them reporting objects that did not exist.
//
// The check is deliberately narrow: a first-person "about to act" phrase at the start
// of a sentence. Where it fires wrongly the cost is one extra model round and a
// notice that is true anyway (no tool ran for it).
public static partial class ActionAnnouncement
{
    // Sent as a user-role message, labelled as the server's: Gemini lifts system
    // messages out of the conversation, and a conversation that ends on the model's
    // turn is not one every provider accepts.
    public const string Nudge =
        "[Nashira server note — not from the user] Your last reply announced an action but called no tool, "
        + "so nothing was read or changed. If the action is still needed, call the tool now. If you cannot "
        + "or should not, say so plainly. Never report the result of an action that no tool ran.";

    // Appended to the answer the user sees (and that is persisted) when the model still
    // calls nothing after the nudge.
    public const string UnrunNotice =
        "\n\n[Nashira: the action announced above was not carried out — no tool was called for it in this turn.]";

    [GeneratedRegex(
        @"(?:^|[.!?:;…\n]|\*\*)\s*(?:"
        // Spanish, first person present or gerund. "Creo"/"Cargo" only with an object,
        // so "Creo que…" (I think) does not count.
        + @"voy\s+a|vamos\s+a|procedo|procediendo|"
        + @"ahora\s+(?:mismo\s+)?(?:creo|marco|verifico|reviso|releo|leo|consulto|ejecuto|cargo|actualizo|asigno|elimino|borro|configuro|aplico|compruebo|valido)|"
        + @"creo\s+(?:ahora|los|las|el|la|un|una|\d)|creando|"
        + @"cargo\s+(?:ahora|los|las|el|la|\d)|cargando|"
        // "Las creo ahora", "Los marco como primarios".
        + @"(?:las|los|la|lo)\s+(?:creo|marco|cargo|verifico|reviso|ejecuto|asigno|elimino)|"
        // Bare "Actualizo:" is how a model acknowledges corrections, so only the gerund.
        + @"verifico|verificando|reviso|revisando|releo|releyendo|leyendo|consulto|consultando|"
        + @"ejecuto|ejecutando|marco|marcando|asigno|asignando|actualizando|cierro|cerrando|"
        + @"compruebo|comprobando|valido|validando|contin[uú]o|sigo\s+con|intentando|"
        // English.
        + @"let\s+me|i['’]ll|i\s+will|i['’]m\s+going\s+to|i\s+am\s+going\s+to|now\s+i\b|"
        + @"creating|checking|verifying|running|fetching|loading|reading|updating|querying|looking\s+up|retrying"
        + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnnouncePattern();

    // Whether a round that called no tool announced one. When the turn has already run
    // tools, only the round's closing paragraph counts ("…Now I mark the 9 primaries."
    // and stop); when it has run none, its opening paragraph counts too, which is where
    // an answer that only pretends to work usually says what it is doing.
    public static bool Unrun(string roundText, bool turnRanTools)
    {
        if (string.IsNullOrWhiteSpace(roundText)) return false;

        var paragraphs = roundText
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
        if (paragraphs.Count == 0) return false;

        if (AnnouncePattern().IsMatch(paragraphs[^1])) return true;
        return !turnRanTools && AnnouncePattern().IsMatch(paragraphs[0]);
    }
}
