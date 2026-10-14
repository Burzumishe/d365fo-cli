using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Where-used for a bare object name (<c>find refs IntEDIDistribution_TKM</c>). An object only
/// custom models define can only be used by them, so the whole XML of the owner and its dependent
/// custom models is read — which also finds metadata uses such as a button's
/// <c>MenuItemName</c>. A standard object goes to DYNAMICSXREFDB by its exact AOT path.
/// </summary>
public class ObjectReferenceSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-objref-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public ObjectReferenceSearchTests()
    {
        _dbPath = _root + ".sqlite";

        // Standard model: defines a table and a form of the same name; cannot reference custom objects.
        WriteDescriptor("Std");
        Write("Std/Std/AxTable/StdTable.xml", "<AxTable>\n  <Name>StdTable</Name>\n</AxTable>\n");
        Write("Std/Std/AxForm/StdTable.xml", Form("StdTable", ""));

        // Owner defines the menu item; a button on one of its forms opens it.
        WriteDescriptor("Owner", "Std");
        Write("Owner/Owner/AxMenuItemDisplay/OwnMenu.xml",
            "<AxMenuItemDisplay>\n  <Name>OwnMenu</Name>\n  <Object>OwnForm</Object>\n</AxMenuItemDisplay>\n");
        Write("Owner/Owner/AxForm/OwnForm.xml", Form("OwnForm", ""));
        Write("Owner/Owner/AxForm/CallerForm.xml", Form("CallerForm", "<MenuItemName>OwnMenu</MenuItemName>"));
        // A longer name with the same prefix is a different object.
        Write("Owner/Owner/AxForm/OtherForm.xml", Form("OtherForm", "<MenuItemName>OwnMenuLines</MenuItemName>"));

        // Dependent references Owner, so it can use the menu item from code.
        WriteDescriptor("Dependent", "Owner");
        Write("Dependent/Dependent/AxClass/DepClass.xml", Class("DepClass", "new MenuFunction(menuItemDisplayStr(OwnMenu), MenuItemType::Display).run();"));

        // Unrelated does not depend on Owner: it cannot use the menu item, so it is not read.
        WriteDescriptor("Unrelated");
        Write("Unrelated/Unrelated/AxForm/UnrForm.xml", Form("UnrForm", "<MenuItemName>OwnMenu</MenuItemName>"));

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

    [Fact]
    public void Custom_object_is_found_in_metadata_and_code_of_owner_and_dependent_models_only()
    {
        var result = ObjectReferenceSearch.FindInCustomModels(_repo, "OwnMenu", kind: null, limit: 50);

        Assert.NotNull(result);
        Assert.Equal(["MenuItemDisplay:Owner"], result.Owners);
        Assert.Equal(["Dependent", "Owner"], result.ModelsScanned.Order());
        Assert.Null(result.Caveat);
        // the menu item's own definition is not a use of it
        Assert.Equal(["Class:DepClass", "Form:CallerForm"], result.Hits.Select(h => $"{h.Kind}:{h.Name}").Order());
        var button = Assert.Single(result.Hits.Single(h => h.Name == "CallerForm").Matches);
        Assert.Equal("<MenuItemName>OwnMenu</MenuItemName>", button.Text);
    }

    [Fact]
    public void Standard_or_unknown_object_is_not_scanned()
    {
        Assert.Null(ObjectReferenceSearch.FindInCustomModels(_repo, "StdTable", kind: null, limit: 50));
        Assert.Null(ObjectReferenceSearch.FindInCustomModels(_repo, "NoSuchObject", kind: null, limit: 50));
        Assert.True(ObjectReferenceSearch.IsStandardObject(_repo, "StdTable"));
        Assert.False(ObjectReferenceSearch.IsStandardObject(_repo, "OwnMenu"));
    }

    [Fact]
    public void Xref_paths_are_the_compiler_folders_of_every_object_with_the_name()
    {
        Assert.Equal(["/Forms/StdTable", "/Tables/StdTable"], ObjectReferenceSearch.XrefPaths(_repo, "StdTable").Order());
        Assert.Equal(["/MenuItemDisplays/OwnMenu"], ObjectReferenceSearch.XrefPaths(_repo, "OwnMenu"));
        Assert.Equal(["/Classes/DepClass"], ObjectReferenceSearch.XrefPaths(_repo, "DepClass"));
        Assert.Empty(ObjectReferenceSearch.XrefPaths(_repo, "NoSuchObject"));
    }

    [Fact]
    public void Find_references_routes_custom_objects_to_the_object_scan()
    {
        var result = new D365FO.Mcp.ToolHandlers(_repo).FindReferences("OwnMenu", kind: null, model: null);

        Assert.True(result.Ok);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("\"via\":\"object-scan\"", json);
        Assert.Contains("\"CallerForm\"", json);
        Assert.DoesNotContain("\"UnrForm\"", json);
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

    private static string Form(string name, string control)
        => $"<AxForm xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">\n  <Name>{name}</Name>\n"
           + $"  <Design>\n    <Controls>\n      {control}\n    </Controls>\n  </Design>\n</AxForm>\n";

    private static string Class(string name, string statement)
        => $"<AxClass>\n  <Name>{name}</Name>\n  <SourceCode>\n    <Declaration><![CDATA[\nclass {name}\n{{\n}}\n]]></Declaration>\n"
           + $"    <Methods>\n      <Method>\n        <Name>run</Name>\n        <Source><![CDATA[\n    public void run()\n    {{\n        {statement}\n    }}\n]]></Source>\n"
           + "      </Method>\n    </Methods>\n  </SourceCode>\n</AxClass>\n";
}
