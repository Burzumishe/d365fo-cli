using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Label where-used (<c>find refs @File:Key</c>, <c>labels search --where-used</c>). A label is
/// used in metadata properties, so the search reads every AOT object's XML — but only in the
/// model that ships the label file and the custom models that depend on it, which is what keeps
/// it from reading the whole of PackagesLocalDirectory.
/// </summary>
public class LabelReferenceSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-lblref-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public LabelReferenceSearchTests()
    {
        _dbPath = _root + ".sqlite";

        // Owner ships the label file and uses the label in a form caption and a menu item.
        WriteDescriptor("Owner");
        Write("Owner/Owner/AxLabelFile/LabelResources/en-US/Own.en-US.label.txt", "﻿PODistribution=Purchase order distribution\n");
        Write("Owner/Owner/AxForm/OwnForm.xml", Form("OwnForm", "@Own:PODistribution"));
        Write("Owner/Owner/AxMenuItemDisplay/OwnMenu.xml", "<AxMenuItemDisplay>\n  <Name>OwnMenu</Name>\n  <Label>@Own:PODistribution</Label>\n</AxMenuItemDisplay>\n");
        // A longer key with the same prefix is a different label.
        Write("Owner/Owner/AxForm/OtherForm.xml", Form("OtherForm", "@Own:PODistributionLines"));
        Write("Owner/Owner/AxClass/OwnClass.xml", Class("OwnClass", "info(\"@Own:PODistribution\");"));

        // Dependent references Owner, so it can use the label.
        WriteDescriptor("Dependent", "Owner");
        Write("Dependent/Dependent/AxForm/DepForm.xml", Form("DepForm", "@Own:PODistribution"));

        // Unrelated does not depend on Owner: it cannot use the label, so it is not read.
        WriteDescriptor("Unrelated");
        Write("Unrelated/Unrelated/AxForm/UnrForm.xml", Form("UnrForm", "@Own:PODistribution"));

        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        foreach (var batch in new MetadataExtractor().ExtractAll(_root, customModelPatterns: ["Owner", "Dependent", "Unrelated"]))
            _repo.ApplyExtract(batch);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + ext); } catch (IOException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("@Own:PODistribution", true)]
    [InlineData("@SYS12345", true)]
    [InlineData("CustTable", false)]
    [InlineData("SalesTable.initFromSalesQuotationTable", false)]
    [InlineData("@", false)]
    public void Recognises_label_tokens(string needle, bool isLabel)
        => Assert.Equal(isLabel, LabelReferenceSearch.IsLabelToken(needle));

    [Fact]
    public void Finds_metadata_and_source_usages_in_owner_and_dependent_models_only()
    {
        var result = LabelReferenceSearch.Find(_repo, "@Own:PODistribution", kind: null, model: null, limit: 50);

        Assert.Equal("Own", result.LabelFile);
        Assert.Equal("Owner", result.OwnerModel);
        Assert.Equal(["Dependent", "Owner"], result.ModelsScanned.Order());
        Assert.Null(result.Caveat);
        Assert.Equal(
            ["Class:OwnClass", "Form:DepForm", "Form:OwnForm", "MenuItemDisplay:OwnMenu"],
            result.Hits.Select(h => $"{h.Kind}:{h.Name}").Order());
        var caption = Assert.Single(result.Hits.Single(h => h.Name == "OwnForm").Matches);
        Assert.StartsWith("<Caption", caption.Text);
        Assert.EndsWith(">@Own:PODistribution</Caption>", caption.Text);
    }

    [Fact]
    public void Kind_filter_is_an_aot_folder_prefix()
    {
        var result = LabelReferenceSearch.Find(_repo, "@Own:PODistribution", kind: "menuitem", model: null, limit: 50);

        Assert.Equal(["OwnMenu"], result.Hits.Select(h => h.Name));
    }

    [Fact]
    public void Explicit_model_scans_only_that_model()
    {
        var result = LabelReferenceSearch.Find(_repo, "@Own:PODistribution", kind: null, model: "Unrelated", limit: 50);

        Assert.Equal(["Unrelated"], result.ModelsScanned);
        Assert.Equal(["UnrForm"], result.Hits.Select(h => h.Name));
    }

    [Fact]
    public void Label_of_an_unknown_file_scans_custom_models_and_says_so()
    {
        var result = LabelReferenceSearch.Find(_repo, "@SYS12345", kind: null, model: null, limit: 50);

        Assert.Null(result.OwnerModel);
        Assert.Equal(["Dependent", "Owner", "Unrelated"], result.ModelsScanned.Order());
        Assert.Empty(result.Hits);
        Assert.Contains("only the custom models were scanned", result.Caveat);
    }

    [Fact]
    public void Limit_truncates()
    {
        var result = LabelReferenceSearch.Find(_repo, "@Own:PODistribution", kind: null, model: null, limit: 1);

        Assert.Single(result.Hits);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Find_references_routes_label_tokens_to_the_label_scan()
    {
        var result = new D365FO.Mcp.ToolHandlers(_repo).FindReferences("@Own:PODistribution", kind: "form", model: null);

        Assert.True(result.Ok);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("\"via\":\"label-scan\"", json);
        Assert.Contains("\"DepForm\"", json);
        Assert.Contains("\"OwnForm\"", json);
        Assert.DoesNotContain("\"OtherForm\"", json);
    }

    private void WriteDescriptor(string model, params string[] references)
        => Write($"{model}/Descriptor/{model}.xml",
            $"<AxModelInfo>\n  <Name>{model}</Name>\n  <Layer>usr</Layer>\n  <ModuleReferences>\n"
            + string.Concat(references.Select(r => $"    <string>{r}</string>\n"))
            + "  </ModuleReferences>\n</AxModelInfo>\n");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Form(string name, string caption)
        => $"<AxForm xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">\n  <Name>{name}</Name>\n"
           + $"  <Design>\n    <Caption xmlns=\"\">{caption}</Caption>\n  </Design>\n</AxForm>\n";

    private static string Class(string name, string statement)
        => $"<AxClass>\n  <Name>{name}</Name>\n  <SourceCode>\n    <Declaration><![CDATA[\nclass {name}\n{{\n}}\n]]></Declaration>\n"
           + $"    <Methods>\n      <Method>\n        <Name>run</Name>\n        <Source><![CDATA[\n    public void run()\n    {{\n        {statement}\n    }}\n]]></Source>\n"
           + "      </Method>\n    </Methods>\n  </SourceCode>\n</AxClass>\n";
}
