using System.Buffers;
using System.Text;

namespace Zaprett.Service.Platform;

/// <summary>
/// Builds a Windows command line from an argv array so that CommandLineToArgvW (and the MSVC CRT) give the same
/// argv back. Arguments are quoted only when needed; quoting is also forced for characters that the cygwin runtime
/// of winws treats specially in unquoted words (single quotes and glob characters).
/// </summary>
public static class WindowsCommandLine
{
    // whitespace and '"' matter to CommandLineToArgvW; the rest only to cygwin (quotes and globbing)
    private static readonly SearchValues<char> NeedsQuotes = SearchValues.Create(" \t\n\v\"'*?[]{}");

    public static string Build(string executable, IReadOnlyList<string> args)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        // argv[0] is parsed without backslash escapes: a quote always ends it, so it may not contain one
        if (executable.Contains('"', StringComparison.Ordinal) || executable.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("executable path must not contain '\"'", nameof(executable));
        var sb = new StringBuilder();
        sb.Append('"').Append(executable).Append('"');
        foreach (var a in args)
        {
            ArgumentNullException.ThrowIfNull(a);
            if (a.Contains('\0', StringComparison.Ordinal))
                throw new ArgumentException("argument must not contain NUL", nameof(args));
            sb.Append(' ');
            AppendArgument(sb, a);
        }
        return sb.ToString();
    }

    public static string QuoteArgument(string arg)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, arg);
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(NeedsQuotes) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length)
            {
                // doubled: the closing quote must not be escaped
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            else
                sb.Append('\\', backslashes).Append(arg[i]);
        }
        sb.Append('"');
    }
}
