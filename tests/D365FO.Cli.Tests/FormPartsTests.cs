using System.Text.Json.Nodes;
using D365FO.Cli.Commands.Get;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// `get form` returned the whole bridged AxForm — every method body at every depth plus the
/// compiler's metadata, 64k characters for one custom form.
/// </summary>
public sealed class FormPartsTests
{
    private static JsonObject Form() => JsonNode.Parse("""
        {
          "Name": "MyDistribution",
          "CompilerMetadata": { "Hash": "abc" },
          "UnparsableSource": "",
          "Methods": [ { "Name": "initTmpLines", "IsStatic": "False", "Source": "    /// <summary>x</summary>\n    public void initTmpLines()\n    {\n        counter++;\n    }\n" } ],
          "DataSources": [ { "Name": "MyLine", "Table": "MyLine",
              "Methods": [ { "Name": "active", "Source": "    public int active()\n    {\n        return super();\n    }\n" } ],
              "Fields": [ { "DataField": "ItemId", "Methods": [ { "Name": "modified", "Source": "    public void modified()\n    {\n        super();\n    }\n" } ] } ] } ]
        }
        """)!.AsObject();

    [Fact]
    public void Compact_output_keeps_signatures_and_drops_bodies_and_compiler_metadata()
    {
        var form = Form();

        FormParts.ShapeBridged(form, withSource: false, compact: true);

        var json = form.ToJsonString();
        Assert.DoesNotContain("counter++", json);
        Assert.DoesNotContain("super()", json);
        Assert.DoesNotContain("CompilerMetadata", json);
        Assert.DoesNotContain("IsStatic", json);
        Assert.Equal("public void initTmpLines()", (string?)form["Methods"]![0]!["Signature"]);
        Assert.Equal("public void modified()", (string?)form["DataSources"]![0]!["Fields"]![0]!["Methods"]![0]!["Signature"]);
    }

    [Fact]
    public void Compact_output_drops_datasource_field_defaults_but_keeps_customised_values()
    {
        var form = JsonNode.Parse("""
            { "DataSources": [ { "Name": "MyLine", "Fields": [
                { "DataField": "ItemId", "Name": "ItemId", "AllowEdit": "Yes", "Enabled": "Yes", "Skip": "No", "Visible": "Yes", "AllowAdd": "Restricted", "Mandatory": "No" },
                { "DataField": "Qty", "Name": "Qty", "AllowEdit": "No", "Mandatory": "Yes" } ] } ] }
            """)!.AsObject();

        FormParts.ShapeBridged(form, withSource: false, compact: true);

        var fields = form["DataSources"]![0]!["Fields"]!.AsArray();
        Assert.Equal("""{"DataField":"ItemId"}""", fields[0]!.ToJsonString());
        Assert.Equal("""{"DataField":"Qty","AllowEdit":"No","Mandatory":"Yes"}""", fields[1]!.ToJsonString());
    }

    [Fact]
    public void With_source_keeps_method_bodies()
    {
        var form = Form();

        FormParts.ShapeBridged(form, withSource: true, compact: true);

        Assert.Contains("counter++", (string?)form["Methods"]![0]!["Source"]);
    }
}
