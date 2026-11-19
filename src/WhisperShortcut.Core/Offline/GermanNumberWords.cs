using System.Globalization;
using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// German cardinal number words (0–999) written as digits where the text marks them as a quantity.
/// Port of the Mac app's GermanNumberWords.swift (Mac #462).
///
/// Parakeet writes the same spoken number sometimes as digits, sometimes as a word ("Blutdruck 140
/// zu neunzig", "Einzel GdB Psyche zwanzig"). A report wants digits for measured values, but prose
/// keeps small numbers as words ("seit drei Tagen", "um zehn Uhr", "drei Punkte erreicht"). The rule
/// follows the usual German writing convention (DIN 5008): numbers with a unit of measure and
/// assessment values are digits, other numbers up to twelve stay words.
///
/// A number word becomes digits only when one of these holds:
/// <list type="bullet">
/// <item>a unit of measure follows ("fünfhundert Meter", "zehn mg", "neunzig Prozent", "zwanzig %");</item>
/// <item>a time unit follows and the number is above twelve ("dreizehn Uhr", "zwanzig Jahre"), so
/// "um zehn Uhr" and "seit drei Tagen" stay words;</item>
/// <item>it is a GdB value: right after "GdB", after "GdB von", or after "GdB" and one capitalised
/// word ("Einzel GdB Psyche zwanzig");</item>
/// <item>it stands on one side of "zu" and the other side is digits, or both sides are number words
/// and one of them is above nine ("140 zu neunzig", "hundertvierzig zu neunzig"; "eins zu eins" stays);</item>
/// <item>digits stand right next to it, separated only by spaces or "/" ("140 neunzig", "4/fünf").</item>
/// </list>
/// "ein" / "eine" are articles and are never read as a number; "eins" only in one of the contexts
/// above. Pure and rule-based: no model, no setting.
/// </summary>
public static class GermanNumberWords
{
    private enum Kind { Word, Digits, Other }

    private readonly record struct Token(string Text, Kind Kind);

    /// <summary>
    /// <paramref name="text"/> with quantity number words as digits. Text without such a word comes back unchanged.
    /// </summary>
    public static string DigitizingQuantities(string text)
    {
        var tokens = Tokenize(text);
        if (!tokens.Exists(t => t.Kind == Kind.Word && Value(t.Text) != null)) return text;
        var changed = false;
        // A conversion can give a neighbour its digit context ("hundertvierzig zu neunzig"), so a
        // second pass picks that up. Every pass only turns words into digits, so it ends.
        for (var pass = 0; pass < 3; pass++)
        {
            var passChanged = false;
            for (var index = 0; index < tokens.Count; index++)
            {
                if (tokens[index].Kind != Kind.Word) continue;
                if (Value(tokens[index].Text) is not int number) continue;
                if (!ShouldConvert(index, number, tokens)) continue;
                tokens[index] = new Token(number.ToString(CultureInfo.InvariantCulture), Kind.Digits);
                passChanged = true;
            }
            if (!passChanged) break;
            changed = true;
        }
        if (!changed) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var token in tokens) sb.Append(token.Text);
        return sb.ToString();
    }

    /// <summary>
    /// The value of one German cardinal word from 0 to 999, or null. Case-insensitive, "ß" or "ss",
    /// "ü" or "ue". "ein" alone is null (an article); inside a compound it counts ("einundzwanzig").
    /// </summary>
    public static int? Value(string word)
    {
        var folded = word.ToLowerInvariant().Replace("ue", "ü").Replace("ß", "ss");
        if (folded == "null") return 0;
        return BelowThousand(folded);
    }

    /// <summary>Measured quantities: always digits.</summary>
    private static readonly HashSet<string> MeasureUnits = new(StringComparer.Ordinal)
    {
        "meter", "metern", "m", "km", "kilometer", "kilometern", "cm", "zentimeter", "mm", "millimeter",
        "mg", "milligramm", "µg", "mikrogramm", "gramm", "kg", "kilo", "kilogramm", "ml", "milliliter",
        "liter", "prozent", "grad", "mmhg", "mmol", "ie", "euro", "schläge",
    };

    /// <summary>Time units: digits only above twelve.</summary>
    private static readonly HashSet<string> TimeUnits = new(StringComparer.Ordinal)
    {
        "uhr", "sekunde", "sekunden", "minute", "minuten", "stunde", "stunden", "tag", "tage", "tagen",
        "woche", "wochen", "monat", "monate", "monaten", "jahr", "jahre", "jahren", "jahres",
        "lebensjahr", "lebensjahre", "lebensjahren",
    };

    private static readonly HashSet<string> EllipsisWords = new(StringComparer.Ordinal)
    {
        "pünktchen", "puenktchen", "punktchen",
    };

    private static bool ShouldConvert(int index, int number, List<Token> tokens)
    {
        string Lower(int i) => tokens[i].Text.ToLowerInvariant();
        var next = Neighbor(index, 1, tokens);
        var previous = Neighbor(index, -1, tokens);

        // "drei Pünktchen" is the spoken ellipsis; only "Only when spoken" turns it into "…" first.
        if (next is int ellipsis && EllipsisWords.Contains(Lower(ellipsis))) return false;

        if (next is int unit && tokens[unit].Kind == Kind.Word)
        {
            var name = Lower(unit);
            if (MeasureUnits.Contains(name)) return true;
            if (TimeUnits.Contains(name) && number > 12) return true;
        }
        if (index + 1 < tokens.Count && tokens[index + 1].Kind == Kind.Other)
        {
            var rest = tokens[index + 1].Text.TrimStart(' ', '\t');
            if (rest.Length > 0 && (rest[0] == '%' || rest[0] == '°')) return true;
        }

        if (previous is int prev)
        {
            if (Lower(prev) == "gdb") return true;
            if (tokens[prev].Kind == Kind.Word && Value(tokens[prev].Text) == null
                && (Lower(prev) == "von" || (tokens[prev].Text.Length > 0 && char.IsUpper(tokens[prev].Text[0])))
                && Neighbor(prev, -1, tokens) is int before
                && Lower(before) == "gdb")
                return true;
        }

        // "140 zu neunzig", "hundertvierzig zu neunzig".
        foreach (var (link, step) in new[] { (next, 1), (previous, -1) })
        {
            if (link is not int linkIndex || Lower(linkIndex) != "zu") continue;
            if (Neighbor(linkIndex, step, tokens) is not int other) continue;
            if (tokens[other].Kind == Kind.Digits) return true;
            if (tokens[other].Kind == Kind.Word && Value(tokens[other].Text) is int otherValue
                && Math.Max(number, otherValue) >= 10)
                return true;
        }

        foreach (var step in new[] { 1, -1 })
        {
            if (Neighbor(index, step, tokens, slash: true) is int other && tokens[other].Kind == Kind.Digits)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The word or digit token next to <paramref name="index"/> in direction <paramref name="step"/>,
    /// when only spaces (or, with <paramref name="slash"/>, one "/") stand between them. A mark in between ends the context.
    /// </summary>
    private static int? Neighbor(int index, int step, List<Token> tokens, bool slash = false)
    {
        var gap = index + step;
        if ((uint)gap >= (uint)tokens.Count) return null;
        if (tokens[gap].Kind != Kind.Other) return gap;
        var gapText = tokens[gap].Text;
        var separator = gapText.Trim(' ', '\t');
        if (!(separator.Length == 0 || (slash && separator == "/")) || ContainsNewline(gapText)) return null;
        var target = gap + step;
        if ((uint)target >= (uint)tokens.Count || tokens[target].Kind == Kind.Other) return null;
        return target;
    }

    private static bool ContainsNewline(string text)
    {
        foreach (var ch in text)
            if (ch is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029') return true;
        return false;
    }

    private static readonly Dictionary<string, int> Ones = new(StringComparer.Ordinal)
    {
        ["eins"] = 1, ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["fünf"] = 5, ["sechs"] = 6,
        ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9,
    };

    /// <summary>The first part of a compound: "ein" counts here ("einundzwanzig", "einhundert").</summary>
    private static readonly Dictionary<string, int> CompoundOnes = new(StringComparer.Ordinal)
    {
        ["ein"] = 1, ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["fünf"] = 5, ["sechs"] = 6,
        ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9,
    };

    private static readonly Dictionary<string, int> Teens = new(StringComparer.Ordinal)
    {
        ["zehn"] = 10, ["elf"] = 11, ["zwölf"] = 12, ["dreizehn"] = 13, ["vierzehn"] = 14,
        ["fünfzehn"] = 15, ["sechzehn"] = 16, ["siebzehn"] = 17, ["achtzehn"] = 18, ["neunzehn"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["zwanzig"] = 20, ["dreissig"] = 30, ["vierzig"] = 40, ["fünfzig"] = 50, ["sechzig"] = 60,
        ["siebzig"] = 70, ["achtzig"] = 80, ["neunzig"] = 90,
    };

    private static int? BelowThousand(string word)
    {
        var hundred = word.IndexOf("hundert", StringComparison.Ordinal);
        if (hundred < 0) return BelowHundred(word);
        var head = word.Substring(0, hundred);
        var tail = word.Substring(hundred + "hundert".Length);
        int hundreds;
        if (head.Length == 0) hundreds = 1;
        else if (CompoundOnes.TryGetValue(head, out var headValue)) hundreds = headValue;
        else return null;
        if (tail.StartsWith("und", StringComparison.Ordinal))
        {
            tail = tail.Substring(3);
            if (tail.Length == 0) return null;
        }
        if (tail.Length == 0) return hundreds * 100;
        if (BelowHundred(tail) is not int rest) return null;
        return hundreds * 100 + rest;
    }

    private static int? BelowHundred(string word)
    {
        if (Ones.TryGetValue(word, out var one)) return one;
        if (Teens.TryGetValue(word, out var teen)) return teen;
        if (Tens.TryGetValue(word, out var ten)) return ten;
        var link = word.IndexOf("und", StringComparison.Ordinal);
        if (link < 0) return null;
        if (!CompoundOnes.TryGetValue(word.Substring(0, link), out var ones)) return null;
        if (!Tens.TryGetValue(word.Substring(link + 3), out var tens)) return null;
        return ones + tens;
    }

    /// <summary>
    /// Letters, digit runs (with a "," or "." between digits, "2,5"), and everything else in between.
    /// Joining the tokens gives the text back unchanged.
    /// </summary>
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var character = text[index];
            Kind kind;
            if (char.IsLetter(character))
            {
                kind = Kind.Word;
                while (index < text.Length && char.IsLetter(text[index])) index++;
            }
            else if (char.IsNumber(character))
            {
                kind = Kind.Digits;
                while (index < text.Length)
                {
                    if (char.IsNumber(text[index])) index++;
                    else if ((text[index] == ',' || text[index] == '.')
                        && index + 1 < text.Length && char.IsNumber(text[index + 1]))
                        index++;
                    else break;
                }
            }
            else
            {
                kind = Kind.Other;
                while (index < text.Length && !char.IsLetter(text[index]) && !char.IsNumber(text[index]))
                    index++;
            }
            tokens.Add(new Token(text.Substring(start, index - start), kind));
        }
        return tokens;
    }
}
