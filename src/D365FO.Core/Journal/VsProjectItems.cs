// <copyright file="VsProjectItems.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Xml.Linq;
using D365FO.Core.ObjectTypes;

namespace D365FO.Core.Journal;

/// <summary>
/// Adds existing AOT objects to a Visual Studio D365FO project (<c>.rnrproj</c>) the way Visual
/// Studio writes them: <c>&lt;Content Include="AxClass\Name"&gt;</c> with <c>SubType</c>,
/// <c>Name</c> and a <c>Link</c> into the Solution Explorer folder, plus that folder's
/// <c>&lt;Folder Include="Classes\"/&gt;</c> entry.
/// </summary>
/// <remarks>
/// This is not <see cref="RnrProjRegistry"/>'s <c>&lt;Compile Include="AxClass\Name.xml"/&gt;</c>:
/// that one registers a file a scaffold just created next to the model. VS projects live in a
/// separate projects folder and list objects without the <c>.xml</c> extension.
/// </remarks>
public static class VsProjectItems
{
    private static readonly XNamespace MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";

    // Solution Explorer folder per AOT root, as Visual Studio names them.
    private static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AxClass"] = "Classes",
        ["AxTable"] = "Tables",
        ["AxForm"] = "Forms",
        ["AxEdt"] = "Extended Data Types",
        ["AxEnum"] = "Base Enums",
        ["AxView"] = "Views",
        ["AxMap"] = "Maps",
        ["AxQuery"] = "Simple Queries",
        ["AxDataEntityView"] = "Data Entities",
        ["AxTableExtension"] = "Table Extensions",
        ["AxFormExtension"] = "Form Extensions",
        ["AxEdtExtension"] = "EDT Extensions",
        ["AxEnumExtension"] = "Base Enum Extensions",
        ["AxViewExtension"] = "View Extensions",
        ["AxQuerySimpleExtension"] = "Simple Query Extensions",
        ["AxDataEntityViewExtension"] = "Data Entity Extensions",
        ["AxMenuExtension"] = "Menu Extensions",
        ["AxSecurityDutyExtension"] = "Security Duty Extensions",
        ["AxSecurityRoleExtension"] = "Security Role Extensions",
        ["AxMenuItemDisplay"] = "Display Menu Items",
        ["AxMenuItemAction"] = "Action Menu Items",
        ["AxMenuItemOutput"] = "Output Menu Items",
        ["AxReport"] = "Reports",
        ["AxService"] = "Services",
        ["AxServiceGroup"] = "Service Groups",
        ["AxSecurityRole"] = "Security Roles",
        ["AxSecurityDuty"] = "Security Duties",
        ["AxSecurityPrivilege"] = "Security Privileges",
        ["AxSecurityPolicy"] = "Security Policies",
        ["AxConfigurationKey"] = "Configuration Keys",
        ["AxLabelFile"] = "Label Files",
        ["AxTile"] = "Tiles",
        ["AxMenu"] = "Menus",
    };

    /// <summary>One object to list in the project.</summary>
    public sealed record Item(string AotRoot, string Name);

    /// <summary>What happened to one requested object.</summary>
    public sealed record ItemResult(string Include, string Status);

    /// <summary>
    /// Parse <c>kind:Name</c> (e.g. <c>class:MyClass</c>, <c>form:MyForm</c>,
    /// <c>table-extension:CustTable.Extension_X</c>) or <c>AxClass/MyClass</c>.
    /// </summary>
    public static Item? Parse(string spec)
    {
        var sep = spec.IndexOfAny([':', '/', '\\']);
        if (sep <= 0 || sep == spec.Length - 1) return null;
        var type = ObjectTypeRegistry.Find(spec[..sep]);
        return type is null ? null : new Item(type.RootElement, spec[(sep + 1)..]);
    }

    /// <summary>How the project file was written, so saving it changes only the added lines.</summary>
    public sealed record FileFormat(bool Bom, bool Crlf);

    /// <summary>Load a project file keeping its whitespace.</summary>
    public static (XDocument Project, FileFormat Format) Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new System.Text.UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return (XDocument.Parse(text, LoadOptions.PreserveWhitespace), new FileFormat(bom, text.Contains("\r\n")));
    }

    /// <summary>
    /// Save in the file's own format. Carriage returns inside values (VS writes
    /// <c>&lt;Company&gt;&amp;#xD;</c>) stay entities, and line breaks are restored to CRLF when
    /// the file used them.
    /// </summary>
    public static void Save(XDocument project, string path, FileFormat format)
    {
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(false),
            NewLineHandling = System.Xml.NewLineHandling.Entitize,
            OmitXmlDeclaration = project.Declaration is null,
        };
        using var buffer = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(buffer, settings)) project.Save(writer);

        var text = new System.Text.UTF8Encoding(false).GetString(buffer.ToArray());
        if (format.Crlf) text = text.Replace("\n", "\r\n");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(format.Bom));
    }

    /// <summary>The model a project builds (its <c>&lt;Model&gt;</c> property).</summary>
    public static string? ModelOf(XDocument project)
        => project.Root?.Descendants(MsBuild + "Model").FirstOrDefault()?.Value.Trim();

    /// <summary>
    /// Add <paramref name="items"/> to <paramref name="project"/>. Objects already listed are left
    /// alone, so the call can be repeated.
    /// </summary>
    public static IReadOnlyList<ItemResult> Add(XDocument project, IEnumerable<Item> items)
    {
        var root = project.Root ?? throw new InvalidOperationException("The project file has no root element.");
        var results = new List<ItemResult>();

        foreach (var item in items)
        {
            var include = $"{item.AotRoot}\\{item.Name}";
            var listed = root.Descendants(MsBuild + "Content")
                .Any(c => string.Equals((string?)c.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase));
            if (listed)
            {
                results.Add(new ItemResult(include, "already-listed"));
                continue;
            }

            var folder = FolderFor(item.AotRoot);
            EnsureFolder(root, folder);
            var group = ItemGroupWith(root, "Content");
            var indent = ChildIndent(group);
            var inner = indent + "  ";
            AddToGroup(group, indent, new XElement(MsBuild + "Content",
                new XAttribute("Include", include),
                new XText(inner), new XElement(MsBuild + "SubType", "Content"),
                new XText(inner), new XElement(MsBuild + "Name", item.Name),
                new XText(inner), new XElement(MsBuild + "Link", $"{folder}\\{item.Name}"),
                new XText(indent)));
            results.Add(new ItemResult(include, "added"));
        }
        return results;
    }

    private static string FolderFor(string aotRoot)
    {
        if (Folders.TryGetValue(aotRoot, out var folder)) return folder;
        // unmapped kinds: the provider collection with its words split ("WorkflowTasks" → "Workflow Tasks")
        var collection = ObjectTypeRegistry.Find(aotRoot)?.ProviderCollection ?? aotRoot;
        return System.Text.RegularExpressions.Regex.Replace(collection, "(?<=[a-z])(?=[A-Z])", " ");
    }

    private static void EnsureFolder(XElement root, string folder)
    {
        var include = folder + "\\";
        var exists = root.Descendants(MsBuild + "Folder")
            .Any(f => string.Equals((string?)f.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            var group = ItemGroupWith(root, "Folder");
            AddToGroup(group, ChildIndent(group), new XElement(MsBuild + "Folder", new XAttribute("Include", include)));
        }
    }

    // The ItemGroup that already holds this kind of item, or a new one before the first <Import>.
    private static XElement ItemGroupWith(XElement root, string itemName)
    {
        var group = root.Elements(MsBuild + "ItemGroup").FirstOrDefault(g => g.Elements(MsBuild + itemName).Any());
        if (group is not null) return group;

        var groupIndent = IndentBefore(root.Elements().FirstOrDefault()) ?? "\n  ";
        group = new XElement(MsBuild + "ItemGroup", new XText(groupIndent));
        var firstImport = root.Elements(MsBuild + "Import").FirstOrDefault();
        if (firstImport is not null) firstImport.AddBeforeSelf(group, new XText(groupIndent));
        else root.Add(new XText(groupIndent), group);
        return group;
    }

    // Insert keeping the file's indentation and the alphabetical order Visual Studio maintains.
    private static void AddToGroup(XElement group, string indent, XElement item)
    {
        var include = (string?)item.Attribute("Include") ?? string.Empty;
        var next = group.Elements(item.Name)
            .FirstOrDefault(e => string.Compare((string?)e.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase) > 0);
        if (next is not null)
        {
            next.AddBeforeSelf(item, new XText(indent));
            return;
        }
        // after the last element, before the whitespace that indents the closing tag
        if (group.LastNode is XText closingIndent) closingIndent.AddBeforeSelf(new XText(indent), item);
        else group.Add(new XText(indent), item);
    }

    private static string ChildIndent(XElement group)
        => IndentBefore(group.Elements().FirstOrDefault()) ?? (IndentBefore(group) ?? "\n  ") + "  ";

    private static string? IndentBefore(XNode? node)
        => node?.PreviousNode is XText text && text.Value.Trim().Length == 0 ? text.Value : null;
}
