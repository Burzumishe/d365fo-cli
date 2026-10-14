using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

public sealed class XppMethodSourceTests
{
    private const string FormMethod =
        "\n    /// <summary>\n    /// Fills the distribution lines.\n    /// </summary>\n" +
        "    public void initTmpLines()\n    {\n        counter++;\n    }\n\n";

    [Theory]
    [InlineData("counter++;", false)]
    [InlineData("if (this.initTmpLines())\n{\n    x = 1;\n}", false)]
    [InlineData("public void initTmpLines()\n{\n    counter++;\n}", true)]
    [InlineData("/// <summary>a {b}</summary>\n[SysObsolete('x')]\nvoid initTmpLines()\n{\n}", true)]
    [InlineData("public void otherMethod()\n{\n}", false)]
    public void IsFullMethod_tells_a_declaration_from_statements(string text, bool expected)
        => Assert.Equal(expected, XppMethodSource.IsFullMethod(text, "initTmpLines"));

    [Fact]
    public void ReplaceStatements_keeps_doc_comment_signature_and_brace_indentation()
    {
        var result = XppMethodSource.ReplaceStatements(FormMethod, "initTmpLines", "if (! x)\n{\n    y = 1;\n}");

        Assert.Equal(
            "\n    /// <summary>\n    /// Fills the distribution lines.\n    /// </summary>\n" +
            "    public void initTmpLines()\n    {\n        if (! x)\n        {\n            y = 1;\n        }\n    }\n\n",
            result);
    }

    [Fact]
    public void ReplaceStatements_handles_parameters_with_parentheses_and_deeper_indentation()
    {
        const string controlMethod = "\n        public boolean validate(str _value = strFmt('%1', 1))\n        {\n            return true;\n        }\n";

        var result = XppMethodSource.ReplaceStatements(controlMethod, "validate", "return false;");

        Assert.Equal("\n        public boolean validate(str _value = strFmt('%1', 1))\n        {\n            return false;\n        }\n", result);
    }

    [Fact]
    public void ReplaceStatements_returns_null_when_the_signature_is_missing()
        => Assert.Null(XppMethodSource.ReplaceStatements("    {\n        x();\n    }", "initTmpLines", "y();"));

    [Fact]
    public void IndentBlock_does_not_indent_already_indented_text_twice()
        => Assert.Equal("        a;\n        if (b)\n            c;",
            XppMethodSource.IndentBlock("\n            a;\n            if (b)\n                c;\n", "        "));

    [Fact]
    public void SplitLeadingDocComment_separates_doc_lines_from_statements()
    {
        var (doc, rest) = XppMethodSource.SplitLeadingDocComment("  /// <summary>\n  /// x\n  /// </summary>\n  super();");

        Assert.Equal(new[] { "/// <summary>", "/// x", "/// </summary>" }, doc);
        Assert.Equal("  super();", rest);
    }

    [Fact]
    public void BuildMethodSource_places_doc_comment_above_signature_and_reindents_body()
    {
        var sig = new FormMethodSignature("lookup", "void");

        var source = FormMethodScaffolder.BuildMethodSource(sig, "            /// <summary>\n            /// Vendor agreements.\n            /// </summary>\n            super();");

        Assert.Equal("\n    /// <summary>\n    /// Vendor agreements.\n    /// </summary>\n    public void lookup()\n    {\n        super();\n    }\n", source);
    }
}
