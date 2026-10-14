// <copyright file="XppMethodSource.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.RegularExpressions;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Text operations on the X++ source of one method, as stored in an AOT <c>&lt;Source&gt;</c>
/// CDATA block: signature detection, statement replacement and re-indentation.
/// </summary>
/// <remarks>
/// A stored method source is the WHOLE method â€” doc comment, attributes, signature and braces.
/// Callers that accept "statements only" must put them back inside the existing braces; writing
/// the statements as the whole source silently drops the signature, which is how
/// <c>modify method</c> used to corrupt every method it touched.
/// </remarks>
public static class XppMethodSource
{
    private const string IndentUnit = "    ";

    private static readonly string Modifiers =
        @"(?:(?:public|private|protected|internal|static|final|abstract|server|client|display|edit|extends|override)\s+)*";

    /// <summary>
    /// True when <paramref name="text"/> is a complete method declaration of
    /// <paramref name="methodName"/> (optional doc comment and attributes, the signature, then
    /// the body in braces) rather than a list of statements.
    /// </summary>
    public static bool IsFullMethod(string text, string methodName)
    {
        var header = HeaderBeforeFirstBrace(text);
        // a statement list has a ';' before its first '{' (or no brace at all)
        if (header is null || header.Contains(';')) return false;
        return DeclarationRegex(methodName).IsMatch(header);
    }

    /// <summary>
    /// Replace the statements between the outer braces of the method in
    /// <paramref name="oldSource"/>, keeping everything before the opening brace (doc comment,
    /// attributes, signature) and the closing brace with its indentation. The new statements are
    /// re-indented one level deeper than the braces.
    /// </summary>
    /// <returns>The new source, or <c>null</c> when the signature of
    /// <paramref name="methodName"/> cannot be located in <paramref name="oldSource"/>.</returns>
    public static string? ReplaceStatements(string oldSource, string methodName, string newStatements)
    {
        var source = oldSource.Replace("\r\n", "\n");
        var declaration = DeclarationRegex(methodName).Match(source);
        if (!declaration.Success) return null;

        var parametersEnd = MatchingParenthesis(source, declaration.Index + declaration.Length - 1);
        if (parametersEnd < 0) return null;
        var openBrace = source.IndexOf('{', parametersEnd);
        var closeBrace = source.LastIndexOf('}');
        if (openBrace < 0 || closeBrace < openBrace) return null;

        var braceIndent = LineIndent(source, closeBrace);
        return source[..(openBrace + 1)] + "\n"
               + IndentBlock(newStatements, braceIndent + IndentUnit) + "\n"
               + braceIndent + source[closeBrace..];
    }

    /// <summary>
    /// Split leading <c>///</c> doc-comment lines off <paramref name="text"/>, so a caller that
    /// receives "statements" can still place a doc comment above the signature.
    /// </summary>
    public static (IReadOnlyList<string> DocLines, string Statements) SplitLeadingDocComment(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var doc = new List<string>();
        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        while (i < lines.Length && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
        {
            doc.Add(lines[i].Trim());
            i++;
        }
        return doc.Count == 0 ? (doc, text) : (doc, string.Join("\n", lines.Skip(i)));
    }

    /// <summary>
    /// Re-indent a block of lines to <paramref name="indent"/>: remove the indentation the lines
    /// have in common and prefix every non-blank line with <paramref name="indent"/>, so
    /// already-indented input is not indented twice. Text already at that level is returned
    /// unchanged apart from empty lines at its edges — rewriting a method with its own statements
    /// must not show up in a diff.
    /// </summary>
    public static string IndentBlock(string text, string indent)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var codeLines = lines.Where(l => l.Trim().Length > 0).ToList();
        var alreadyAtLevel = codeLines.Count > 0
            && codeLines.All(l => l.StartsWith(indent, StringComparison.Ordinal))
            && codeLines.Any(l => !char.IsWhiteSpace(l[indent.Length]));
        if (alreadyAtLevel) return string.Join("\n", lines);

        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        var common = codeLines
            .Select(l => l.Length - l.TrimStart(' ', '\t').Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join("\n", lines.Select(l => l.Trim().Length == 0 ? string.Empty : indent + l[common..]));
    }

    private static Regex DeclarationRegex(string methodName) => new(
        $@"(?m)^[ \t]*{Modifiers}[\w\[\]\.<>]+\s+{Regex.Escape(methodName)}\s*\(",
        RegexOptions.IgnoreCase);

    private static string? HeaderBeforeFirstBrace(string text)
    {
        var header = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            // doc comments and attributes may contain anything, braces included; they are never the signature
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                continue;

            var brace = line.IndexOf('{');
            if (brace >= 0)
            {
                header.Add(line[..brace]);
                return string.Join("\n", header);
            }
            header.Add(line);
        }
        return null;
    }

    private static int MatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static string LineIndent(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', index) + 1;
        var prefix = text[lineStart..index];
        return prefix.Trim().Length == 0 ? prefix : IndentUnit;
    }
}
