using System.Text.Json.Nodes;
using D365FO.Cli.Commands.Get;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// `get table` on the bridge returned the full AxTable — CustTable came to ~530 KB, method
/// bodies included — and ignored --include. These pin the shaping that replaced it.
/// </summary>
public class GetTableShapingTests
{
    private static JsonObject Table() => JsonNode.Parse("""
        {
          "Name": "CustTable",
          "Fields": [
            { "Name": "AccountNum", "ExtendedDataType": "CustAccount", "StringSize": "10", "Mandatory": "Yes",
              "AllowEdit": "Yes", "Null": "No", "Tags": "", "GeneralDataProtectionRegulation": "CustomerContent" }
          ],
          "Relations": [
            { "Name": "Bank", "RelatedTable": "CustBankAccount", "Validate": "Yes",
              "Constraints": [ { "Field": "AccountNum", "RelatedField": "CustAccount", "Name": "AccountNum" } ] }
          ],
          "Methods": [
            { "Name": "find", "IsStatic": true, "Visibility": "Public",
              "Source": "    /// <summary>Find.</summary>\n    public static CustTable find(CustAccount _custAccount,\n        boolean _forUpdate = false)\n    {\n        return null;\n    }" }
          ],
          "FieldGroups": [ { "Name": "Overview" } ]
        }
        """)!.AsObject();

    [Fact]
    public void Compact_drops_defaults_classification_and_method_bodies()
    {
        var t = Table();
        TableParts.ShapeBridged(t, include: null, withSource: false, compact: true);

        var field = t["Fields"]![0]!.AsObject();
        Assert.Equal("Yes", (string?)field["Mandatory"]);           // non-default kept
        Assert.False(field.ContainsKey("AllowEdit"));               // platform default
        Assert.False(field.ContainsKey("StringSize"));              // class default, not the field's length
        Assert.False(field.ContainsKey("Tags"));                    // empty
        Assert.False(field.ContainsKey("GeneralDataProtectionRegulation"));

        var constraint = t["Relations"]![0]!["Constraints"]![0]!.AsObject();
        Assert.False(constraint.ContainsKey("Name"));               // repeats Field
        Assert.False(t["Relations"]![0]!.AsObject().ContainsKey("Validate"));

        var method = t["Methods"]![0]!.AsObject();
        Assert.False(method.ContainsKey("Source"));
        Assert.False(method.ContainsKey("IsStatic"));
        Assert.Equal("public static CustTable find(CustAccount _custAccount, boolean _forUpdate = false)",
            (string?)method["Signature"]);
    }

    [Fact]
    public void Include_keeps_only_the_named_collections()
    {
        var t = Table();
        TableParts.ShapeBridged(t, TableParts.Parse("fields"), withSource: false, compact: true);
        Assert.True(t.ContainsKey("Fields"));
        Assert.False(t.ContainsKey("Methods"));
        Assert.False(t.ContainsKey("Relations"));
        Assert.False(t.ContainsKey("FieldGroups"));
        Assert.Equal("CustTable", (string?)t["Name"]);              // table-level properties stay
    }

    [Fact]
    public void Full_and_with_source_keep_everything()
    {
        var t = Table();
        TableParts.ShapeBridged(t, include: null, withSource: true, compact: false);
        var method = t["Methods"]![0]!.AsObject();
        Assert.True(method.ContainsKey("Source"));
        Assert.True(method.ContainsKey("Signature"));
        Assert.Equal("Yes", (string?)t["Fields"]![0]!["AllowEdit"]);
    }

    [Fact]
    public void Unknown_include_part_is_rejected()
        => Assert.Throws<ArgumentException>(() => TableParts.Parse("fields,bogus"));
}
