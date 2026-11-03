using System.Text.RegularExpressions;

namespace DeskPanel.Feeds;

/// <summary>
/// A quick, local guess of the language of a text (name in Spanish), from common words and writing systems. It exists
/// because the translation model may be a specialised translator that answers any other question badly; asking it
/// "which language is this?" is not reliable, while counting stop words is.
/// </summary>
public static partial class LanguageGuess
{
    private static readonly Dictionary<string, HashSet<string>> StopWords = new()
    {
        ["inglés"] = Words("the of and to in is that for it with as was on are by this be from at or an have not you but they his her which their"),
        ["español"] = Words("el la los las de del que y en un una es se no por con para su al lo como más pero sus le ya o este si porque esta entre cuando muy sin sobre también me hasta hay donde quien desde todo nos"),
        ["francés"] = Words("le la les des du de un une et en est que qui dans pour pas sur au avec ce il elle ne se sont plus par mais ou nous vous leur cette ces aux été"),
        ["alemán"] = Words("der die das und ist nicht ein eine zu den dem mit von für auf sich auch es im des dass werden wird sie er wie aber oder wir bei noch nach aus"),
        ["italiano"] = Words("il lo la le gli di del della che e è un una per con non sono in da come più ma anche si al dei delle nel questo suo loro essere"),
        ["portugués"] = Words("o a os as de do da dos das que e em um uma é não se para com por mais como mas foi ao ele ela seu sua são também já muito nos"),
        ["neerlandés"] = Words("de het een en van in is dat op te zijn met voor niet aan er ook als bij maar om uit door ze hij wordt naar nog dan"),
        ["catalán"] = Words("el la els les de del que i en un una és es no per amb per al com més però seu seva són també ja molt aquest aquesta"),
    };

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Letters();

    private static HashSet<string> Words(string list) => new(list.Split(' '), StringComparer.Ordinal);

    /// <returns>The language name in Spanish, or null when the text is too short or ambiguous to say.</returns>
    public static string? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Writing systems first: no stop-word list is needed to tell Cyrillic from Latin.
        var scripts = ScriptName(text);
        if (scripts is not null)
        {
            return scripts;
        }

        var tokens = Letters().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
        if (tokens.Count < 25)
        {
            return null;
        }

        var scores = StopWords.ToDictionary(kv => kv.Key, kv => tokens.Count(kv.Value.Contains) / (double)tokens.Count);
        var ranked = scores.OrderByDescending(kv => kv.Value).ToList();

        // Needs a real share of common words and a clear lead over the runner-up (Spanish vs Catalan, Spanish vs Portuguese).
        return ranked[0].Value >= 0.08 && ranked[0].Value > ranked[1].Value * 1.25 ? ranked[0].Key : null;
    }

    private static string? ScriptName(string text)
    {
        int cyrillic = 0, han = 0, kana = 0, hangul = 0, arabic = 0, hebrew = 0, greek = 0, letters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;
            switch (c)
            {
                case >= 'Ѐ' and <= 'ӿ': cyrillic++; break;
                case >= '一' and <= '鿿': han++; break;
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '가' and <= '힯': hangul++; break;
                case >= '؀' and <= 'ۿ': arabic++; break;
                case >= '֐' and <= '׿': hebrew++; break;
                case >= 'Ͱ' and <= 'Ͽ': greek++; break;
            }
        }

        if (letters < 20)
        {
            return null;
        }

        bool Mostly(int count) => count > letters * 0.4;
        if (Mostly(cyrillic)) return "ruso";
        if (Mostly(kana + han)) return kana > han / 10 ? "japonés" : "chino";
        if (Mostly(hangul)) return "coreano";
        if (Mostly(arabic)) return "árabe";
        if (Mostly(hebrew)) return "hebreo";
        if (Mostly(greek)) return "griego";
        return null;
    }
}
