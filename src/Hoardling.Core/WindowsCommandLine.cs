using System.Text;

namespace Hoardling.Core;

public static class WindowsCommandLine
{
    /// <summary>
    /// Quotes one argument so <c>CommandLineToArgvW</c> (and the C runtime) read it back unchanged:
    /// backslashes are doubled only before a quote or the closing quote, and quotes are escaped.
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(" \t\n\v\""))
        {
            return arg;
        }
        var sb = new StringBuilder(arg.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
