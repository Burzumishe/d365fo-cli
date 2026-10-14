using System.Xml.Linq;
using D365FO.Core.Journal;
using Xunit;

namespace D365FO.Core.Tests;

public sealed class VsProjectItemsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"d365fo-proj-{Guid.NewGuid():N}.rnrproj");

    // As Visual Studio writes it: BOM, CRLF, two-space indentation, a CR entity in <Company>.
    private const string VsProject =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<Project ToolsVersion=\"14.0\" DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
        "  <PropertyGroup>\r\n" +
        "    <Model>MyModel</Model>\r\n" +
        "    <Company>&#xD;\r\n    </Company>\r\n" +
        "  </PropertyGroup>\r\n" +
        "  <ItemGroup>\r\n" +
        "    <Folder Include=\"Classes\\\" />\r\n" +
        "  </ItemGroup>\r\n" +
        "  <ItemGroup>\r\n" +
        "    <Content Include=\"AxClass\\Zeta\">\r\n" +
        "      <SubType>Content</SubType>\r\n" +
        "      <Name>Zeta</Name>\r\n" +
        "      <Link>Classes\\Zeta</Link>\r\n" +
        "    </Content>\r\n" +
        "  </ItemGroup>\r\n" +
        "  <Import Project=\"$(MSBuildBinPath)\\Microsoft.Common.targets\" />\r\n" +
        "</Project>";

    public VsProjectItemsTests() => File.WriteAllText(_path, VsProject, new System.Text.UTF8Encoding(true));

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static VsProjectItems.Item Item(string spec) => VsProjectItems.Parse(spec)!;

    [Fact]
    public void Saving_without_changes_reproduces_the_file_byte_for_byte()
    {
        var before = File.ReadAllBytes(_path);
        var (project, format) = VsProjectItems.Load(_path);

        VsProjectItems.Save(project, _path, format);

        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Added_objects_are_listed_like_visual_studio_lists_them()
    {
        var (project, format) = VsProjectItems.Load(_path);

        var results = VsProjectItems.Add(project, [Item("class:Alpha"), Item("form:MyForm")]);
        VsProjectItems.Save(project, _path, format);

        Assert.All(results, r => Assert.Equal("added", r.Status));
        var text = File.ReadAllText(_path);
        Assert.Contains(
            "    <Content Include=\"AxClass\\Alpha\">\r\n      <SubType>Content</SubType>\r\n      <Name>Alpha</Name>\r\n      <Link>Classes\\Alpha</Link>\r\n    </Content>\r\n" +
            "    <Content Include=\"AxClass\\Zeta\">", text);
        Assert.Contains("<Link>Forms\\MyForm</Link>", text);
        Assert.Contains("    <Folder Include=\"Classes\\\" />\r\n    <Folder Include=\"Forms\\\" />\r\n", text);
        Assert.Contains("<Company>&#xD;\r\n    </Company>", text);
    }

    [Fact]
    public void An_object_already_listed_is_left_alone()
    {
        var (project, _) = VsProjectItems.Load(_path);

        var result = Assert.Single(VsProjectItems.Add(project, [Item("AxClass/Zeta")]));

        Assert.Equal("already-listed", result.Status);
        Assert.Single(project.Root!.Descendants(XName.Get("Content", "http://schemas.microsoft.com/developer/msbuild/2003")));
    }

    [Theory]
    [InlineData("class:MyClass_TKM", "AxClass", "MyClass_TKM")]
    [InlineData("table-extension:CustTable.Extension_TKM", "AxTableExtension", "CustTable.Extension_TKM")]
    [InlineData("AxForm\\MyForm", "AxForm", "MyForm")]
    public void Parse_accepts_kind_name_and_aot_root_name(string spec, string root, string name)
        => Assert.Equal(new VsProjectItems.Item(root, name), VsProjectItems.Parse(spec));

    [Fact]
    public void Parse_rejects_a_bare_name()
        => Assert.Null(VsProjectItems.Parse("MyClass"));
}
