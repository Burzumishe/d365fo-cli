using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

public class SuffixNamingAndCocTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-coc-{Guid.NewGuid():N}.sqlite");
    private readonly string _srcRoot = Path.Combine(Path.GetTempPath(), $"d365fo-coc-src-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
        if (Directory.Exists(_srcRoot)) Directory.Delete(_srcRoot, recursive: true);
    }

    private MetadataRepository Repo(ExtractBatch batch)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(batch);
        return repo;
    }

    private string ClassSource(string name, string methodName, string methodSource)
    {
        Directory.CreateDirectory(_srcRoot);
        var path = Path.Combine(_srcRoot, name + ".xml");
        File.WriteAllText(path,
            $"<AxClass><Name>{name}</Name><SourceCode><Declaration><![CDATA[class {name}\n{{\n}}]]></Declaration>" +
            $"<Methods><Method><Name>{methodName}</Name><Source><![CDATA[{methodSource}]]></Source></Method></Methods>" +
            "</SourceCode></AxClass>");
        return path;
    }

    // ---- wrapper from the base declaration ----

    [Fact]
    public void Wrapper_drops_default_values_and_passes_every_parameter_to_next()
    {
        var wrapper = CocWrapper.FromBaseSource("run",
            "public void run(boolean _post = false, str _list = 'a,b')\n{\n    super();\n}");

        Assert.NotNull(wrapper);
        Assert.StartsWith("public void run(boolean _post, str _list)\n", wrapper);
        Assert.Contains("    next run(_post, _list);\n", wrapper);
        Assert.DoesNotContain("return", wrapper);
    }

    [Fact]
    public void Wrapper_of_a_method_that_returns_a_value_returns_what_next_returned()
    {
        var wrapper = CocWrapper.FromBaseSource("validateWrite", "public boolean validateWrite()\n{\n    return true;\n}");

        Assert.Contains("    boolean ret = next validateWrite();\n", wrapper);
        Assert.Contains("    return ret;\n", wrapper);
    }

    [Fact]
    public void Wrapper_reads_a_multi_line_declaration_past_doc_comments_and_attributes()
    {
        var source =
            "/// <summary>\n/// Finds a customer.\n/// </summary>\n" +
            "[Hookable(true), SysObsolete('x, y', false)]\n" +
            "public static CustTable find(CustAccount   _custAccount,\n" +
            "                             boolean       _forUpdate = false)\n" +
            "{\n    CustTable custTable;\n    return custTable;\n}";

        var wrapper = CocWrapper.FromBaseSource("find", source);

        Assert.StartsWith("public static CustTable find(CustAccount _custAccount, boolean _forUpdate)\n", wrapper);
        Assert.Contains("    CustTable ret = next find(_custAccount, _forUpdate);\n", wrapper);
        Assert.DoesNotContain("Hookable", wrapper);
    }

    [Fact]
    public void Wrapper_of_a_declaration_without_an_access_modifier_is_public()
    {
        // The kernel table methods (TableDataMethods) are declared without one.
        var wrapper = CocWrapper.FromBaseSource("validateWrite", "boolean validateWrite()");

        Assert.StartsWith("public boolean validateWrite()\n", wrapper);
    }

    [Fact]
    public void Wrapper_is_null_when_the_source_declares_another_method()
    {
        Assert.Null(CocWrapper.FromBaseSource("run", "public void main(Args _args)\n{\n}"));
    }

    // ---- suffix naming ----

    [Fact]
    public void Coc_class_name_carries_the_suffix_before_Extension()
    {
        Assert.Equal("CustTable_TKM_Extension", ExtensionNaming.CocClassName("CustTable", "_TKM"));
        Assert.Equal("CustTable_Extension", ExtensionNaming.CocClassName("CustTable", null));
    }

    [Fact]
    public void Element_extension_token_prefers_the_suffix_then_the_model()
    {
        Assert.Equal("Extension_TKM", ExtensionNaming.ElementExtensionToken("_TKM", "TallinnaKaubamaja"));
        Assert.Equal("TallinnaKaubamaja", ExtensionNaming.ElementExtensionToken(null, "TallinnaKaubamaja"));
        Assert.Equal("Extension", ExtensionNaming.ElementExtensionToken(null, null));
    }

    [Fact]
    public void A_whole_suffix_style_coc_class_name_normalises_to_its_target()
    {
        Assert.Equal("CustTable", ObjectNamingRules.NormalizeExtensionTarget("CustTable_TKM_Extension", out var note, "_TKM"));
        Assert.NotNull(note);

        // Without a configured suffix the old behaviour stands: only _Extension is stripped.
        Assert.Equal("CustTable_TKM", ObjectNamingRules.NormalizeExtensionTarget("CustTable_TKM_Extension", out _));
    }

    [Theory]
    [InlineData("Class", "MyClass", "MyClass_TKM")]
    [InlineData("Coc", "CustTable_Extension", "CustTable_TKM_Extension")]
    [InlineData("TableExtension", "CustTable.TallinnaKaubamaja", "CustTable.Extension_TKM")]
    public void A_name_without_the_suffix_is_reported_with_the_expected_name(string kind, string name, string expected)
    {
        var v = Assert.Single(ObjectNamingRules.Validate(kind, name, suffix: "_TKM"), x => x.Code == "MISSING_SUFFIX");
        Assert.Contains(expected, v.Message);
    }

    [Theory]
    [InlineData("Class", "MyClass_TKM")]
    [InlineData("Coc", "CustTable_TKM_Extension")]
    [InlineData("TableExtension", "CustTable.Extension_TKM")]
    [InlineData("Field", "MyField")]
    public void A_name_with_the_suffix_or_a_member_name_passes(string kind, string name)
    {
        Assert.DoesNotContain(ObjectNamingRules.Validate(kind, name, suffix: "_TKM"), x => x.Code == "MISSING_SUFFIX");
    }

    [Fact]
    public void No_suffix_configured_means_no_suffix_rule()
    {
        Assert.DoesNotContain(ObjectNamingRules.Validate("Class", "MyClass"), x => x.Code == "MISSING_SUFFIX");
    }

    // ---- the CoC plan against an index ----

    [Fact]
    public void A_name_that_is_a_table_and_a_form_is_wrapped_as_the_table()
    {
        // The index lists kinds alphabetically, so "form" used to win and the wrapper said formStr.
        var repo = Repo(ExtractBatch.Empty("Suite") with
        {
            Tables = [new ExtractedTable("CustTable", null, null, [])],
            Forms = [new ExtractedForm("CustTable", null, [])],
        });

        var plan = CocScaffold.Build(repo, "CustTable", ["validateWrite"], targetKind: null, className: null, suffix: "_TKM");

        Assert.Equal("table", plan.TargetKind);
        Assert.Equal("CustTable_TKM_Extension", plan.ClassName);
        var xml = plan.Doc.ToString();
        Assert.Contains("[ExtensionOf(tableStr(CustTable))]", xml);
        Assert.Contains("final class CustTable_TKM_Extension", xml);
        // validateWrite is not declared on the table: the kernel signature is used.
        Assert.Contains("public boolean validateWrite()", xml);
        Assert.Contains(plan.Warnings, w => w.Contains("also a form"));
    }

    [Fact]
    public void An_explicit_target_kind_wins_over_the_index()
    {
        var repo = Repo(ExtractBatch.Empty("Suite") with
        {
            Tables = [new ExtractedTable("CustTable", null, null, [])],
            Forms = [new ExtractedForm("CustTable", null, [])],
        });

        var plan = CocScaffold.Build(repo, "CustTable", ["init"], targetKind: "form", className: null, suffix: null);

        Assert.Equal("form", plan.TargetKind);
        Assert.Contains("[ExtensionOf(formStr(CustTable))]", plan.Doc.ToString());
    }

    [Fact]
    public void The_signature_of_an_inherited_method_comes_from_the_base_class_source()
    {
        var basePath = ClassSource("SalesFormLetter", "run", "public void run(boolean _skip = false)\n{\n}");
        var repo = Repo(ExtractBatch.Empty("Suite") with
        {
            Classes =
            [
                new ExtractedClass("SalesFormLetter", null, false, false, basePath, []),
                new ExtractedClass("SalesFormLetter_Invoice", "SalesFormLetter", false, false, null, []),
            ],
        });

        var plan = CocScaffold.Build(repo, "SalesFormLetter_Invoice", ["run"], null, null, null);

        var xml = plan.Doc.ToString();
        Assert.Contains("[ExtensionOf(classStr(SalesFormLetter_Invoice))]", xml);
        Assert.Contains("public void run(boolean _skip)", xml);
        Assert.Contains("next run(_skip);", xml);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Signature of"));
    }

    [Fact]
    public void A_suffix_style_class_name_as_target_wraps_the_real_target()
    {
        var repo = Repo(ExtractBatch.Empty("Suite") with { Tables = [new ExtractedTable("CustTable", null, null, [])] });

        var plan = CocScaffold.Build(repo, "CustTable_TKM_Extension", ["validateWrite"], null, null, "_TKM");

        Assert.Equal("CustTable", plan.Target);
        Assert.Equal("CustTable_TKM_Extension", plan.ClassName);
        Assert.Contains("tableStr(CustTable)", plan.Doc.ToString());
    }

    [Fact]
    public void An_unknown_signature_falls_back_to_the_stub_and_says_so()
    {
        var repo = Repo(ExtractBatch.Empty("Suite") with
        {
            Classes = [new ExtractedClass("MyClass", null, false, false, null, [])],
        });

        var plan = CocScaffold.Build(repo, "MyClass", ["doWork"], null, "MyClass_Custom_Extension", null);

        Assert.Equal("MyClass_Custom_Extension", plan.ClassName);
        Assert.Contains("public void doWork()", plan.Doc.ToString());
        Assert.Contains(plan.Warnings, w => w.Contains("Signature of MyClass::doWork not found"));
    }
}
