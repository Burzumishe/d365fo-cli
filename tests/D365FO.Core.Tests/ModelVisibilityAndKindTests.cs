using System.Text.Json;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Validation;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

public class ModelVisibilityAndKindTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-vis-{Guid.NewGuid():N}.sqlite");
    private readonly string _pkgRoot = Path.Combine(Path.GetTempPath(), $"d365fo-pkgs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
        if (Directory.Exists(_pkgRoot)) Directory.Delete(_pkgRoot, recursive: true);
    }

    private static string Src(string package, string model, string folder, string name)
        => Path.Combine("C:", "pkgs", package, model, folder, name + ".xml");

    private MetadataRepository Repo(params ExtractBatch[] batches)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        foreach (var b in batches) repo.ApplyExtract(b);
        return repo;
    }

    private static ExtractBatch Model(string package, string model, string table, params string[] deps)
        => ExtractBatch.Empty(model) with
        {
            Tables = new[] { new ExtractedTable(table, null, Src(package, model, "AxTable", table), Array.Empty<ExtractedTableField>()) },
            Dependencies = deps,
        };

    [Fact]
    public void PackageFromSourcePath_is_the_model_folders_parent()
    {
        Assert.Equal("ApplicationSuite",
            MetadataRepository.PackageFromSourcePath(@"C:\AosService\PackagesLocalDirectory\ApplicationSuite\Foundation\AxTable\CustTable.xml"));
        Assert.Null(MetadataRepository.PackageFromSourcePath(null));
    }

    [Fact]
    public void Type_from_an_unreferenced_package_is_not_visible()
    {
        // CustTable lives in model Foundation of package Suite; Mine references only Platform.
        var repo = Repo(
            Model("Platform", "Platform", "PlatTable"),
            Model("Suite", "Foundation", "CustTable", "Platform"),
            Model("Mine", "Mine", "MyTable", "Platform"));

        var code = "void run()\n{\n    CustTable custTable = CustTable::find('1');\n    PlatTable p;\n}";
        var result = ReferenceResolver.Resolve(code, repo);
        var violations = ModelVisibility.Check(result, repo, "Mine");

        var v = Assert.Single(violations);
        Assert.Equal("not-visible-from-model", v.Kind);
        Assert.Equal("CustTable", v.Identifier);
        Assert.Equal(3, v.Line);
        Assert.Contains("Suite", v.Detail);
    }

    [Fact]
    public void Visibility_follows_module_references_transitively()
    {
        // Mine → Mid → Suite: CustTable is visible through Mid's reference.
        var repo = Repo(
            Model("Suite", "Foundation", "CustTable"),
            Model("Mid", "Mid", "MidTable", "Suite"),
            Model("Mine", "Mine", "MyTable", "Mid"));

        var result = ReferenceResolver.Resolve("void run()\n{\n    CustTable c;\n}", repo);
        Assert.Empty(ModelVisibility.Check(result, repo, "Mine"));
        Assert.Contains("Suite", repo.VisiblePackages("Mine")!);
    }

    [Fact]
    public void Own_package_is_always_visible()
    {
        var repo = Repo(Model("Mine", "Mine", "MyTable"));
        var result = ReferenceResolver.Resolve("void run()\n{\n    MyTable t;\n}", repo);
        Assert.Empty(ModelVisibility.Check(result, repo, "Mine"));
    }

    [Fact]
    public void PrepareChange_prefers_the_table_when_a_form_shares_its_name()
    {
        // SymbolKinds comes back alphabetically, so "form" used to win over "table".
        var repo = Repo(ExtractBatch.Empty("Suite") with
        {
            Tables = new[] { new ExtractedTable("SalesTable", null, "x", Array.Empty<ExtractedTableField>()) },
            Forms = new[] { new ExtractedForm("SalesTable", "y", Array.Empty<ExtractedFormDataSource>()) },
        });

        var result = new ToolHandlers(repo).PrepareChange("SalesTable", null, null, null, null, null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        Assert.Equal("table", doc.RootElement.GetProperty("objectType").GetString());
        Assert.Contains("form", doc.RootElement.GetProperty("ambiguity").GetString());

        var explicitForm = new ToolHandlers(repo).PrepareChange("SalesTable", null, null, "form", null, null);
        using var formDoc = JsonDocument.Parse(JsonSerializer.Serialize(explicitForm.Data));
        Assert.Equal("form", formDoc.RootElement.GetProperty("objectType").GetString());
        Assert.Equal(JsonValueKind.Null, formDoc.RootElement.GetProperty("ambiguity").ValueKind);
    }

    [Fact]
    public void ExtractModel_reads_the_package_descriptor()
    {
        // `index extract` calls ExtractModel directly; the descriptor used to be applied only
        // by a different caller, so every model was indexed without dependencies.
        var package = Path.Combine(_pkgRoot, "Mine");
        var modelDir = Path.Combine(package, "Mine");
        Directory.CreateDirectory(Path.Combine(modelDir, "AxClass"));
        Directory.CreateDirectory(Path.Combine(package, "Descriptor"));
        File.WriteAllText(Path.Combine(modelDir, "AxClass", "MyClass.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><AxClass><Name>MyClass</Name><SourceCode><Declaration>class MyClass {}</Declaration></SourceCode></AxClass>");
        File.WriteAllText(Path.Combine(package, "Descriptor", "Mine.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <AxModelInfo xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Layer>14</Layer>
              <ModuleReferences xmlns:d2p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
                <d2p1:string>ApplicationFoundation</d2p1:string>
                <d2p1:string>ApplicationPlatform</d2p1:string>
              </ModuleReferences>
              <Name>Mine</Name>
              <Publisher>Me</Publisher>
            </AxModelInfo>
            """);

        var batch = new MetadataExtractor().ExtractModel(modelDir, "Mine");

        Assert.Equal(new[] { "ApplicationFoundation", "ApplicationPlatform" }, batch.Dependencies);
        Assert.Equal("Me", batch.Publisher);
        Assert.Equal("14", batch.Layer);
    }
}
