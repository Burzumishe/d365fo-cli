using System.Text;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Turns the base method's declaration into a Chain-of-Command wrapper. The scaffolder used to
/// write <c>public void m() { next m(); }</c> for every method, which does not compile for
/// anything that returns a value or takes parameters (<c>validateWrite</c> returns boolean,
/// <c>modifiedField</c> takes a FieldId) — the one thing a CoC wrapper must get right is the
/// signature.
/// </summary>
public static class CocWrapper
{
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "protected", "private", "internal", "static", "final", "abstract",
        "client", "server", "display", "edit", "override", "virtual",
    };

    private static readonly HashSet<string> AccessModifiers = new(StringComparer.Ordinal)
    {
        "public", "protected", "private", "internal",
    };

    /// <summary>
    /// The wrapper for a method whose source (or bare declaration) is <paramref name="baseSource"/>,
    /// or null when no declaration can be read from it.
    /// </summary>
    /// <remarks>
    /// The signature is copied with two changes the CoC rules require: default parameter values
    /// are dropped (the base method's defaults are already in effect when <c>next</c> runs, and
    /// repeating them is a compile error), and <c>abstract</c> is dropped. Attributes and doc
    /// comments of the base method are not copied — <c>[Hookable]</c>/<c>[Wrappable]</c> describe
    /// the base method, not the wrapper.
    /// </remarks>
    public static string? FromBaseSource(string methodName, string baseSource)
    {
        var header = DeclarationOf(baseSource);
        if (header is null) return null;

        var open = header.IndexOf('(');
        var close = MatchingParen(header, open);
        if (open < 0 || close < 0) return null;

        var head = header[..open].Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (head.Count == 0 || !string.Equals(head[^1], methodName, StringComparison.OrdinalIgnoreCase)) return null;
        var name = head[^1];
        head.RemoveAt(head.Count - 1);
        head.Remove("abstract");

        var returnType = head.LastOrDefault(t => !Modifiers.Contains(t)) ?? "void";
        if (!head.Any(AccessModifiers.Contains)) head.Insert(0, "public");
        if (!head.Any(t => !Modifiers.Contains(t))) head.Add("void");

        // Base declarations are often column-aligned (`CustAccount   _custAccount`); one space each.
        var parameters = SplitTopLevel(header[(open + 1)..close])
            .Select(p => string.Join(' ', StripDefault(p).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Length > 0)
            .ToList();
        var arguments = parameters.Select(p => p.Split(' ')[^1]);

        var call = $"next {name}({string.Join(", ", arguments)})";
        var sb = new StringBuilder();
        sb.Append(string.Join(' ', head)).Append(' ').Append(name)
          .Append('(').Append(string.Join(", ", parameters)).Append(")\n{\n");
        if (returnType == "void")
        {
            sb.Append("    ").Append(call).Append(";\n");
            sb.Append("    // extension logic here\n");
        }
        else
        {
            sb.Append("    ").Append(returnType).Append(" ret = ").Append(call).Append(";\n");
            sb.Append("    // extension logic here\n");
            sb.Append("    return ret;\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The wrapper used when no signature could be found: the old stub, which the caller must
    /// flag, because it compiles only for a parameterless void method.
    /// </summary>
    public static string Stub(string methodName)
        => $"public void {methodName}()\n{{\n    next {methodName}();\n    // extension logic here\n}}\n";

    /// <summary>
    /// The declaration part of a method source: comment lines and leading attribute blocks
    /// removed, cut at the opening brace of the body.
    /// </summary>
    private static string? DeclarationOf(string source)
    {
        var lines = source.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
        var text = string.Join(' ', lines);

        // Leading [Attribute(...)] blocks, which may hold brackets and strings themselves.
        while (text.StartsWith('['))
        {
            var depth = 0;
            var end = -1;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']' && --depth == 0) { end = i; break; }
            }
            if (end < 0) return null;
            text = text[(end + 1)..].TrimStart();
        }

        var brace = text.IndexOf('{');
        var header = (brace >= 0 ? text[..brace] : text).Trim();
        return header.Length == 0 ? null : header;
    }

    private static int MatchingParen(string text, int open)
    {
        if (open < 0) return -1;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Split a parameter list at commas that are not inside parentheses, brackets or strings.</summary>
    private static IEnumerable<string> SplitTopLevel(string list)
    {
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            var c = list[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            switch (c)
            {
                case '"' or '\'': quote = c; break;
                case '(' or '[': depth++; break;
                case ')' or ']': depth--; break;
                case ',' when depth == 0:
                    yield return list[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return list[start..];
    }

    private static string StripDefault(string parameter)
    {
        var eq = parameter.IndexOf('=');
        return eq >= 0 ? parameter[..eq] : parameter;
    }
}
