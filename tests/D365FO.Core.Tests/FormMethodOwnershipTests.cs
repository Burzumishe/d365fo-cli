using System.Text.Json;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Form methods are not in the symbol index, and two controls can each override `lookup`.
/// `prepare change` used to report a real form method as missing, and `read form --method`
/// returned whichever control method came first without saying whose it was.
/// </summary>
public sealed class FormMethodOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-fmo-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _formPath;

    private const string FormXml =
        "<AxForm xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"Microsoft.Dynamics.AX.Metadata.V6\">" +
        "<Name>MyDistribution</Name><SourceCode>" +
        "<Methods xmlns=\"\"><Method><Name>initTmpLines</Name><Source><![CDATA[\n    public void initTmpLines()\n    {\n    }\n]]></Source></Method></Methods>" +
        "<DataSources xmlns=\"\"><DataSource><Name>MyLine</Name><Methods><Method><Name>active</Name><Source><![CDATA[\n    public int active()\n    {\n        return super();\n    }\n]]></Source></Method></Methods></DataSource></DataSources>" +
        "<DataControls xmlns=\"\">" +
        "<Control><Name>Grid_Agreement</Name><Type>String</Type><Methods><Method><Name>lookup</Name><Source><![CDATA[\n    public void lookup()\n    {\n    }\n]]></Source></Method></Methods></Control>" +
        "<Control><Name>Grid_Vendor</Name><Type>String</Type><Methods><Method><Name>lookup</Name><Source><![CDATA[\n    public void lookup()\n    {\n        super();\n    }\n]]></Source></Method></Methods></Control>" +
        "</DataControls></SourceCode></AxForm>";

    public FormMethodOwnershipTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "index.sqlite");
        _formPath = Path.Combine(_dir, "MyDistribution.xml");
        File.WriteAllText(_formPath, FormXml);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private MetadataRepository Repo(bool isCustom)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty(isCustom ? "MyModel" : "Suite") with
        {
            IsCustom = isCustom,
            Forms = new[] { new ExtractedForm("MyDistribution", _formPath, Array.Empty<ExtractedFormDataSource>()) },
        });
        return repo;
    }

    private static JsonElement Prepare(MetadataRepository repo, string method)
    {
        var result = new ToolHandlers(repo).PrepareChange("MyDistribution", null, method, "form", null, null);
        Assert.True(result.Ok);
        return JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement.Clone();
    }

    [Fact]
    public void Reader_records_which_datasource_or_control_owns_a_method()
    {
        var src = XppSourceReader.Read(_formPath)!;

        Assert.Null(src.Methods.Single(m => m.Name == "initTmpLines").Owner);
        Assert.Equal("datasource:MyLine", src.Methods.Single(m => m.Name == "active").Owner);
        Assert.Equal(new[] { "control:Grid_Agreement", "control:Grid_Vendor" },
            src.Methods.Where(m => m.Name == "lookup").Select(m => m.Owner));
    }

    [Fact]
    public void Owner_qualified_name_picks_that_controls_method()
    {
        var src = XppSourceReader.Read(_formPath)!;

        Assert.Equal(2, XppSourceReader.FindMethods(src, "lookup").Count);
        var vendor = Assert.Single(XppSourceReader.FindMethods(src, "Grid_Vendor.lookup"));
        Assert.Contains("super();", vendor.Body);
        Assert.Equal("Grid_Vendor.lookup", XppSourceReader.QualifiedName(vendor));
    }

    [Fact]
    public void Prepare_finds_a_form_method_and_recommends_editing_an_owned_form_directly()
    {
        var data = Prepare(Repo(isCustom: true), "initTmpLines");

        var method = data.GetProperty("method");
        Assert.True(method.GetProperty("found").GetBoolean());
        Assert.Equal("public void initTmpLines()", method.GetProperty("signature").GetString());
        Assert.Contains("modify method form MyDistribution initTmpLines", method.GetProperty("eligibility").GetString());
        Assert.StartsWith("Edit directly", data.GetProperty("recommendedStrategies")[0].GetString());
    }

    [Fact]
    public void Prepare_points_a_control_method_at_generate_control_method()
    {
        var method = Prepare(Repo(isCustom: true), "Grid_Agreement.lookup").GetProperty("method");

        Assert.Equal("control:Grid_Agreement", method.GetProperty("owner").GetString());
        Assert.Contains("generate control-method MyDistribution --method lookup --overwrite", method.GetProperty("eligibility").GetString());
    }

    [Fact]
    public void Prepare_still_recommends_extensions_for_a_form_we_do_not_own()
    {
        var data = Prepare(Repo(isCustom: false), "initTmpLines");

        Assert.Contains("CoC", data.GetProperty("method").GetProperty("eligibility").GetString());
        Assert.StartsWith("Form extension", data.GetProperty("recommendedStrategies")[0].GetString());
    }

    [Fact]
    public void Prepare_names_a_cli_command_for_a_missing_method()
    {
        var eligibility = Prepare(Repo(isCustom: true), "doesNotExist").GetProperty("method").GetProperty("eligibility").GetString();

        Assert.Contains("d365fo read form MyDistribution", eligibility);
        Assert.DoesNotContain("get_object_info", eligibility);
    }
}
