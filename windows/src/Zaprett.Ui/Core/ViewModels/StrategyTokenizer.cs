namespace Zaprett.Ui.Core.ViewModels;

public enum TokenKind
{
    Text,
    Option,
    Value,
    Placeholder,
    Separator,
    Comment,
    Filter,
}

public sealed record StrategyToken(string Text, TokenKind Kind);

/// <summary>
/// Splits a strategy line into highlighted pieces: "--new" (profile separator), filters (--filter-*, --wf-*),
/// options, their values, placeholders ${…}, and comments ("#…" lines and --comment). Whitespace is kept as Text,
/// so joining all tokens gives the original line back.
/// </summary>
public static class StrategyTokenizer
{
    public static IReadOnlyList<StrategyToken> Tokenize(string line)
    {
        var tokens = new List<StrategyToken>();
        if (line.TrimStart().StartsWith('#'))
        {
            tokens.Add(new StrategyToken(line, TokenKind.Comment));
            return tokens;
        }
        var i = 0;
        while (i < line.Length)
        {
            var start = i;
            if (char.IsWhiteSpace(line[i]))
            {
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                    i++;
                tokens.Add(new StrategyToken(line[start..i], TokenKind.Text));
                continue;
            }
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
                i++;
            AddWord(tokens, line[start..i]);
        }
        return tokens;
    }

    private static void AddWord(List<StrategyToken> tokens, string word)
    {
        if (!word.StartsWith("--", StringComparison.Ordinal))
        {
            AddValue(tokens, word);
            return;
        }
        var eq = word.IndexOf('=', StringComparison.Ordinal);
        var name = eq < 0 ? word : word[..eq];
        var kind = name == "--new" ? TokenKind.Separator
            : name == "--comment" ? TokenKind.Comment
            : name.StartsWith("--filter-", StringComparison.Ordinal) || name.StartsWith("--wf-", StringComparison.Ordinal) ? TokenKind.Filter
            : TokenKind.Option;
        if (eq < 0)
        {
            tokens.Add(new StrategyToken(word, kind));
            return;
        }
        tokens.Add(new StrategyToken(word[..(eq + 1)], kind));
        if (kind == TokenKind.Comment)
            tokens.Add(new StrategyToken(word[(eq + 1)..], TokenKind.Comment));
        else
            AddValue(tokens, word[(eq + 1)..]);
    }

    /// <summary>A value with ${…} placeholders inside it.</summary>
    private static void AddValue(List<StrategyToken> tokens, string value)
    {
        var i = 0;
        while (i < value.Length)
        {
            var p = value.IndexOf("${", i, StringComparison.Ordinal);
            if (p < 0)
            {
                tokens.Add(new StrategyToken(value[i..], TokenKind.Value));
                return;
            }
            if (p > i)
                tokens.Add(new StrategyToken(value[i..p], TokenKind.Value));
            var end = value.IndexOf('}', p);
            if (end < 0)
            {
                tokens.Add(new StrategyToken(value[p..], TokenKind.Value));
                return;
            }
            tokens.Add(new StrategyToken(value[p..(end + 1)], TokenKind.Placeholder));
            i = end + 1;
        }
    }
}
