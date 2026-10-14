using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;
using D365FO.Core.Bridge;

namespace D365FO.Cli.Commands.Get;

public sealed class GetTableCommand : Command<GetTableCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--include <PARTS>")]
        [System.ComponentModel.Description("Comma list: fields,indexes,relations,methods,deleteactions,fieldgroups,mappings (default: all)")]
        public string? Include { get; init; }

        [CommandOption("--with-source")]
        [System.ComponentModel.Description("Return full X++ method bodies. Default: each method carries its declaration (signature) only — the bodies of a large table run to hundreds of KB.")]
        public bool WithSource { get; init; }

        [CommandOption("--full")]
        [System.ComponentModel.Description("Bridge output only: keep every property, including those at their platform default and the data-classification tags. Default: compact.")]
        public bool Full { get; init; }

        [CommandOption("--merged")]
        [System.ComponentModel.Description("Return the EFFECTIVE shape: the base table with every TableExtension folded in, each member labelled with the object that contributes it. This is what the AOS sees; the plain output is the base table alone.")]
        public bool Merged { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        // The merged view is assembled from the index plus each extension's own XML, so it
        // cannot come from the bridge's single-object read — resolve it before that gate.
        if (settings.Merged)
        {
            // Through the shared answer, so `get table --merged` and the MCP
            // extension_info(mode=table-merge) return the same document rather than two that
            // agree by inspection.
            return RenderHelpers.Render(kind,
                D365FO.Core.Analysis.ExtensionAnswers.TableMerge(RepoFactory.Create(), settings.Name));
        }

        HashSet<string>? include;
        try
        {
            include = TableParts.Parse(settings.Include);
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", ex.Message));
        }

        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadTable(settings.Name);
            if (bridged is not null)
            {
                if (bridged is System.Text.Json.Nodes.JsonObject obj)
                    TableParts.ShapeBridged(obj, include, settings.WithSource, compact: !settings.Full);
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }

        var repo = RepoFactory.Create();
        var details = repo.GetTableDetails(settings.Name);

        if (details is null)
        {
            var hint = NameSuggester.HintFor(repo, NameSuggester.Kind.Table, settings.Name)
                       ?? "Run 'd365fo index build' after extracting metadata.";
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail("TABLE_NOT_FOUND", $"Table '{settings.Name}' not found in index.", hint));
        }

        var payload = new Dictionary<string, object?> { ["table"] = details.Table };
        if (TableParts.Wants(include, "fields")) payload["fields"] = details.Fields;
        if (TableParts.Wants(include, "relations")) payload["relations"] = details.Relations;
        if (TableParts.Wants(include, "methods")) payload["methods"] = details.Methods;
        if (TableParts.Wants(include, "indexes")) payload["indexes"] = details.Indexes;
        if (TableParts.Wants(include, "deleteactions")) payload["deleteActions"] = details.DeleteActions;
        var result = ToolResult<object>.Success(payload);

        return RenderHelpers.Render(kind, result, _ =>
        {
            AnsiConsole.MarkupLine($"[bold]{RenderHelpers.Escape(details.Table.Name)}[/] — {RenderHelpers.Escape(details.Table.Label) ?? "(no label)"}  [grey]({details.Table.Model})[/]");
            var table = new Table().AddColumn("Field").AddColumn("Type/EDT").AddColumn("Label").AddColumn("Mand.");
            foreach (var f in details.Fields)
                table.AddRow(f.Name, f.EdtName ?? f.Type ?? "-", RenderHelpers.Escape(f.Label) ?? "-", f.Mandatory ? "yes" : "");
            AnsiConsole.Write(table);
            if (details.Indexes.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Indexes[/]");
                var ix = new Table().AddColumn("Name").AddColumn("Fields").AddColumn("AllowDup").AddColumn("AltKey");
                foreach (var i in details.Indexes)
                    ix.AddRow(i.Name, i.FieldsCsv ?? "-", i.AllowDuplicates ? "yes" : "", i.AlternateKey ? "yes" : "");
                AnsiConsole.Write(ix);
            }
            if (details.Relations.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Relations[/]");
                var rel = new Table().AddColumn("From").AddColumn("To").AddColumn("Cardinality").AddColumn("Name");
                foreach (var r in details.Relations)
                    rel.AddRow(r.FromTable, r.ToTable, r.Cardinality ?? "-", r.RelationName ?? "-");
                AnsiConsole.Write(rel);
            }
            if (details.DeleteActions.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Delete actions[/]");
                var da = new Table().AddColumn("Name").AddColumn("Related").AddColumn("Action");
                foreach (var d in details.DeleteActions)
                    da.AddRow(d.Name ?? "-", d.RelatedTable, d.DeleteAction ?? "-");
                AnsiConsole.Write(da);
            }
            if (details.Methods.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Methods[/]");
                var mt = new Table().AddColumn("Name").AddColumn("Return").AddColumn("Static").AddColumn("Signature");
                foreach (var m in details.Methods)
                    mt.AddRow(m.Name, m.ReturnType ?? "-", m.IsStatic ? "yes" : "", RenderHelpers.Escape(m.Signature) ?? "-");
                AnsiConsole.Write(mt);
            }
        });
    }
}

/// <summary>
/// Shapes a `get table` payload. The bridge hands back the full AxTable — every field
/// property, every method body — which for CustTable is ~530 KB: more than a whole agent
/// session's budget for one lookup. `--include` picks the collections, method bodies are
/// reduced to their declarations unless `--with-source`, and empty properties are dropped.
/// </summary>
internal static class TableParts
{
    // Lower-case part name → the bridge's collection key.
    private static readonly Dictionary<string, string> BridgeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fields"] = "Fields",
        ["indexes"] = "Indexes",
        ["relations"] = "Relations",
        ["methods"] = "Methods",
        ["deleteactions"] = "DeleteActions",
        ["fieldgroups"] = "FieldGroups",
        ["mappings"] = "Mappings",
    };

    public static HashSet<string>? Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!BridgeKeys.ContainsKey(raw))
                throw new ArgumentException($"Unknown --include part '{raw}'. Valid: {string.Join(",", BridgeKeys.Keys)}.");
            parts.Add(raw);
        }
        return parts;
    }

    public static bool Wants(HashSet<string>? include, string part) => include is null || include.Contains(part);

    // Property values the platform applies when a member does not set them. Seen on every
    // one of CustTable's 193 fields / 75 relations, so they carry no information per member.
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["IgnoreEDTRelation"] = "No", ["Null"] = "No", ["AosAuthorization"] = "No",
        ["MinReadAccess"] = "Auto", ["IsObsolete"] = "No", ["IsSystemGenerated"] = "No",
        ["SysSharingType"] = "Always", ["SaveContents"] = "Yes", ["IsManuallyUpdated"] = "No",
        ["IsNullable"] = "False", ["Mandatory"] = "No", ["Visible"] = "Yes",
        ["AllowEditOnCreate"] = "Yes", ["AllowEdit"] = "Yes", ["Adjustment"] = "Left",
        ["FieldUpdate"] = "Absolute", ["CurrencyDate"] = "Auto", ["CurrencyCode"] = "Auto",
        ["Validate"] = "Yes", ["UseDefaultRoleNames"] = "Yes", ["CreateNavigationPropertyMethods"] = "No",
        ["EDTRelation"] = "No", ["OnDelete"] = "None",
        // The metadata class's own default, not the field's length: AccountNum reports 10
        // while its EDT CustAccount is 20. The real size comes from the EDT.
        ["StringSize"] = "10",
    };

    // Data-classification tags: needed for GDPR reviews, not for writing code.
    private static readonly string[] ClassificationKeys = ["GeneralDataProtectionRegulation", "AssetClassification"];

    // What a method's declaration already says, once it is in Signature.
    internal static readonly string[] MethodKeysInSignature =
        ["Visibility", "IsAbstract", "IsDelegate", "IsStatic", "IsFinal", "IsDisplay", "IsEdit", "ReturnType", "Parameters"];

    public static void ShapeBridged(System.Text.Json.Nodes.JsonObject table, HashSet<string>? include, bool withSource, bool compact)
    {
        foreach (var (part, key) in BridgeKeys)
        {
            if (!Wants(include, part)) table.Remove(key);
        }

        if (table["Methods"] is System.Text.Json.Nodes.JsonArray methods)
        {
            foreach (var m in methods.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if (m["Source"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var src))
                {
                    m["Signature"] = Declaration(src);
                    if (!withSource) m.Remove("Source");
                }
                if (compact)
                {
                    foreach (var k in MethodKeysInSignature) m.Remove(k);
                }
            }
        }

        if (compact)
        {
            foreach (var key in new[] { "Fields", "Relations", "FieldGroups", "Indexes", "DeleteActions" })
            {
                if (table[key] is System.Text.Json.Nodes.JsonArray arr)
                    foreach (var member in arr.OfType<System.Text.Json.Nodes.JsonObject>()) DropDefaults(member);
            }
        }

        DropEmpty(table);
    }

    private static void DropDefaults(System.Text.Json.Nodes.JsonObject member)
    {
        foreach (var k in ClassificationKeys) member.Remove(k);
        foreach (var key in member.Select(p => p.Key).ToList())
        {
            var value = member[key];
            if (value is System.Text.Json.Nodes.JsonValue v)
            {
                var text = v.TryGetValue<string>(out var s) ? s
                         : v.TryGetValue<bool>(out var b) ? (b ? "True" : "False")
                         : null;
                if (text is not null && Defaults.TryGetValue(key, out var def) && def == text)
                    member.Remove(key);
            }
            else if (value is System.Text.Json.Nodes.JsonArray children)
            {
                // Relation constraints repeat the field as their name; nested members share the defaults.
                foreach (var child in children.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    if ((string?)child["Name"] is { } n && n == (string?)child["Field"]) child.Remove("Name");
                    DropDefaults(child);
                }
            }
        }
    }

    /// <summary>The text before the method body's opening brace, whitespace collapsed.</summary>
    internal static string Declaration(string source)
    {
        var brace = source.IndexOf('{');
        var head = brace >= 0 ? source[..brace] : source;
        // Drop leading XML doc comments and attributes' line breaks; keep attributes themselves.
        var lines = head.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("///") && !l.StartsWith("//"));
        return System.Text.RegularExpressions.Regex.Replace(string.Join(" ", lines), @"\s+", " ").Trim();
    }

    internal static void DropEmpty(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var child = o[key];
                    DropEmpty(child);
                    if (IsEmpty(child)) o.Remove(key);
                }
                break;
            case System.Text.Json.Nodes.JsonArray a:
                foreach (var child in a) DropEmpty(child);
                break;
        }
    }

    private static bool IsEmpty(System.Text.Json.Nodes.JsonNode? n) => n switch
    {
        null => true,
        System.Text.Json.Nodes.JsonValue v => v.TryGetValue<string>(out var s) && s.Length == 0,
        System.Text.Json.Nodes.JsonArray a => a.Count == 0,
        System.Text.Json.Nodes.JsonObject o => o.Count == 0,
        _ => false,
    };
}

/// <summary>
/// Shapes a `get form` payload. The bridge hands back the full AxForm with every method body —
/// form, datasource, datasource-field and control methods, nested at any depth — plus the
/// compiler's metadata: 64k characters for an ordinary custom form. Method bodies are reduced to
/// their declarations unless `--with-source`.
/// </summary>
internal static class FormParts
{
    // Whole-object source blobs and compiler output: `read form` serves the source when needed.
    private static readonly string[] SourceKeys = ["SourceCode", "UnparsableSource"];
    private static readonly string[] CompilerKeys = ["CompilerMetadata"];

    public static void ShapeBridged(System.Text.Json.Nodes.JsonObject form, bool withSource, bool compact)
    {
        if (!withSource)
            foreach (var key in SourceKeys) form.Remove(key);
        if (compact)
            foreach (var key in CompilerKeys) form.Remove(key);

        ShapeMethods(form, withSource, compact);
        if (compact)
        {
            if (form["DataSources"] is System.Text.Json.Nodes.JsonArray dataSources)
                foreach (var ds in dataSources.OfType<System.Text.Json.Nodes.JsonObject>())
                    if (ds["Fields"] is System.Text.Json.Nodes.JsonArray fields)
                        foreach (var field in fields.OfType<System.Text.Json.Nodes.JsonObject>()) DropFieldDefaults(field);
            TableParts.DropEmpty(form);
        }
    }

    // A datasource field the form does not customise carries exactly these values; listing them
    // on every field of every datasource was a third of the compact payload.
    private static readonly Dictionary<string, string> FieldDefaults = new(StringComparer.Ordinal)
    {
        ["AllowEdit"] = "Yes", ["Enabled"] = "Yes", ["Skip"] = "No", ["Visible"] = "Yes",
        ["AllowAdd"] = "Restricted", ["Mandatory"] = "No",
    };

    private static void DropFieldDefaults(System.Text.Json.Nodes.JsonObject field)
    {
        foreach (var (key, value) in FieldDefaults)
            if ((string?)field[key] == value) field.Remove(key);
        if ((string?)field["Name"] is { } name && name == (string?)field["DataField"]) field.Remove("Name");
    }

    private static void ShapeMethods(System.Text.Json.Nodes.JsonNode? node, bool withSource, bool compact)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
                // a method object: Name + Source
                if (o["Source"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var src) && o["Name"] is not null)
                {
                    o["Signature"] = TableParts.Declaration(src);
                    if (!withSource) o.Remove("Source");
                    if (compact) foreach (var k in TableParts.MethodKeysInSignature) o.Remove(k);
                }
                foreach (var (_, child) in o.ToList()) ShapeMethods(child, withSource, compact);
                break;
            case System.Text.Json.Nodes.JsonArray a:
                foreach (var child in a) ShapeMethods(child, withSource, compact);
                break;
        }
    }
}

public sealed class GetEdtCommand : Command<GetEdtCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadEdt(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }
        var repo = RepoFactory.Create();
        var resolved = repo.GetEdtResolved(settings.Name);
        var result = resolved is null
            ? ToolResult<object>.Fail("EDT_NOT_FOUND", $"EDT '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Edt, settings.Name))
            : ToolResult<object>.Success(
                (object)resolved.Value.Edt,
                resolved.Value.StringSizeInheritedFrom is null
                    ? null
                    : new List<string>
                    {
                        $"StringSize {resolved.Value.Edt.StringSize} is inherited from {resolved.Value.StringSizeInheritedFrom} — this EDT declares none of its own.",
                    });
        return RenderHelpers.Render(kind, result);
    }
}

public sealed class GetClassCommand : Command<GetClassCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        // Bridge-primary path (opt-in via D365FO_BRIDGE_ENABLED=1).
        // Silently falls back to the SQLite index when the bridge is missing,
        // disabled, or returns NOT_IMPLEMENTED (POC stub). Errors from a
        // running bridge are surfaced via --verbose only; the final result
        // is still produced from the index so the user never sees a hole.
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadClass(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }

        var repo = RepoFactory.Create();
        var details = repo.GetClassDetails(settings.Name);
        var result = details is null
            ? ToolResult<object>.Fail("CLASS_NOT_FOUND", $"Class '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Class, settings.Name))
            : ToolResult<object>.Success(details);
        return RenderHelpers.Render(kind, result);
    }
}

public sealed class GetMenuItemCommand : Command<GetMenuItemCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var mi = repo.GetMenuItem(settings.Name);
        var result = mi is null
            ? ToolResult<object>.Fail("MENU_ITEM_NOT_FOUND", $"Menu item '{settings.Name}' not found.")
            : ToolResult<object>.Success(mi);
        return RenderHelpers.Render(kind, result);
    }
}

public sealed class GetSecurityCommand : Command<GetSecurityCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<OBJECT>")]
        public string Object { get; init; } = "";

        [CommandOption("--type <TYPE>")]
        [System.ComponentModel.Description("Required. Matched literally against the indexed AOT ObjectType — e.g. MenuItemDisplay|MenuItemAction|MenuItemOutput|Table|Form|Report|Class. There is no usable default: entry points are always indexed as one of the specific MenuItem* kinds, never bare \"Menuitem\".")]
        public string? Type { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Type))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--type is required. Use the specific indexed ObjectType, e.g. MenuItemDisplay|MenuItemAction|MenuItemOutput|Table|Form|Report|Class."));
        var repo = RepoFactory.Create();
        var coverage = repo.GetSecurityCoverage(settings.Object, settings.Type);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(coverage));
    }
}

public sealed class GetEnumCommand : Command<GetEnumCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadEnum(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }
        var repo = RepoFactory.Create();
        var details = repo.GetEnum(settings.Name);
        return RenderHelpers.Render(kind, details is null
            ? ToolResult<object>.Fail("ENUM_NOT_FOUND", $"Enum '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Enum, settings.Name))
            : ToolResult<object>.Success(details));
    }
}

public sealed class GetLabelCommand : Command<GetLabelCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<FILE_OR_KEY>")]
        [System.ComponentModel.Description("Label file name (e.g. SYS) when <KEY> is given, otherwise the bare key / @File+Id token to look up across all files.")]
        public string File { get; init; } = "";

        [CommandArgument(1, "[KEY]")]
        [System.ComponentModel.Description("Label key inside <FILE>. Omit to search every indexed file for <FILE_OR_KEY> as a key.")]
        public string? Key { get; init; }

        [CommandOption("--lang <LANG>")]
        public string Language { get; init; } = "en-us";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();

        // Single-argument form: treat as either an @File+Id token or a bare
        // key to look up across every indexed label file. Keeps muscle
        // memory from `d365fo resolve label` working in the `get` tree.
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            var needle = settings.File;
            if (string.IsNullOrWhiteSpace(needle))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "BAD_INPUT", "Label key required.",
                    "Pass `<FILE> <KEY>` or a single `@File+Id` / bare key argument."));
            }

            IReadOnlyList<D365FO.Core.Index.LabelMatch> hits;
            IReadOnlyList<D365FO.Core.Index.LabelMatch> likeHits = Array.Empty<D365FO.Core.Index.LabelMatch>();
            if (needle.StartsWith('@'))
            {
                hits = repo.ResolveLabel(needle, new[] { settings.Language });
            }
            else
            {
                // SearchLabels is case-insensitive on the LIKE side; keep a
                // copy for the fallback hint and post-filter to exact Key
                // (case-insensitive) for the primary answer so the result
                // stays focused.
                likeHits = repo.SearchLabels(needle, new[] { settings.Language });
                hits = likeHits
                    .Where(h => string.Equals(h.Key, needle, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (hits.Count == 0)
            {
                var hint = likeHits.Count > 0
                    ? $"No exact key match. `d365fo search label {needle}` returns {likeHits.Count} substring hit(s)."
                    : $"Try `d365fo search label {needle}` for substring matches, or pass `@File+Id` / `<FILE> <KEY>`.";
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "LABEL_NOT_FOUND", $"No label with key '{needle}' in language '{settings.Language}'.", hint));
            }

            var items = settings.RawText
                ? hits
                : hits.Select(h => h with { Value = D365FO.Core.StringSanitizer.Sanitize(h.Value) }).ToList();
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
        }

        var hit = repo.GetLabel(settings.File, settings.Language, settings.Key);
        if (hit is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("LABEL_NOT_FOUND", $"{settings.File}/{settings.Language}:{settings.Key} not found."));
        if (!settings.RawText)
            hit = hit with { Value = D365FO.Core.StringSanitizer.Sanitize(hit.Value) };
        return RenderHelpers.Render(kind, ToolResult<object>.Success(hit));
    }
}

public sealed class GetFormCommand : Command<GetFormCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--with-source")]
        [System.ComponentModel.Description("Return full X++ method bodies (form, datasource, field and control methods). Default: each method carries its declaration only.")]
        public bool WithSource { get; init; }

        [CommandOption("--full")]
        [System.ComponentModel.Description("Bridge output only: keep compiler metadata, unparsable source and empty properties. Default: compact.")]
        public bool Full { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadForm(settings.Name);
            if (bridged is not null)
            {
                if (bridged is System.Text.Json.Nodes.JsonObject obj)
                    FormParts.ShapeBridged(obj, settings.WithSource, compact: !settings.Full);
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }
        var repo = RepoFactory.Create();
        var f = repo.GetForm(settings.Name);
        return RenderHelpers.Render(kind, f is null
            ? ToolResult<object>.Fail("FORM_NOT_FOUND", $"Form '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Form, settings.Name))
            : ToolResult<object>.Success(f));
    }
}

public sealed class GetRoleCommand : Command<GetRoleCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var r = repo.GetSecurityRole(settings.Name);
        return RenderHelpers.Render(kind, r is null
            ? ToolResult<object>.Fail("ROLE_NOT_FOUND", $"Role '{settings.Name}' not found.")
            : ToolResult<object>.Success(r));
    }
}

public sealed class GetDutyCommand : Command<GetDutyCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var d = repo.GetSecurityDuty(settings.Name);
        return RenderHelpers.Render(kind, d is null
            ? ToolResult<object>.Fail("DUTY_NOT_FOUND", $"Duty '{settings.Name}' not found.")
            : ToolResult<object>.Success(d));
    }
}

public sealed class GetPrivilegeCommand : Command<GetPrivilegeCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var p = repo.GetSecurityPrivilege(settings.Name);
        return RenderHelpers.Render(kind, p is null
            ? ToolResult<object>.Fail("PRIVILEGE_NOT_FOUND", $"Privilege '{settings.Name}' not found.")
            : ToolResult<object>.Success(p));
    }
}

public sealed class GetQueryCommand : Command<GetQueryCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var q = RepoFactory.Create().GetQuery(settings.Name);
        return RenderHelpers.Render(kind, q is null
            ? ToolResult<object>.Fail("QUERY_NOT_FOUND", $"Query '{settings.Name}' not found.")
            : ToolResult<object>.Success(q));
    }
}

public sealed class GetViewCommand : Command<GetViewCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var v = RepoFactory.Create().GetView(settings.Name);
        return RenderHelpers.Render(kind, v is null
            ? ToolResult<object>.Fail("VIEW_NOT_FOUND", $"View '{settings.Name}' not found.")
            : ToolResult<object>.Success(v));
    }
}

public sealed class GetEntityCommand : Command<GetEntityCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";

        [CommandOption("--odata-metadata")]
        [System.ComponentModel.Description("Emit OData $metadata fragment (EntityType declaration) instead of the standard JSON detail.")]
        public bool ODataMetadata { get; init; }
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var e = RepoFactory.Create().GetDataEntity(settings.Name);
        if (e is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("ENTITY_NOT_FOUND", $"Data entity '{settings.Name}' not found."));

        if (settings.ODataMetadata)
        {
            var metadata = BuildODataMetadata(e);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new { metadata }));
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(e));
    }

    private static string BuildODataMetadata(D365FO.Core.Index.DataEntityDetails e)
    {
        var entityName = e.Entity.PublicEntityName ?? e.Entity.Name;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<EntityType Name=\"{entityName}\">");

        // Key fields: mandatory non-readonly fields treated as key candidates.
        var keyFields = e.Fields.Where(f => f.IsMandatory && !f.IsReadOnly).ToList();
        if (keyFields.Count > 0)
        {
            sb.AppendLine("  <Key>");
            foreach (var kf in keyFields)
                sb.AppendLine($"    <PropertyRef Name=\"{kf.Name}\" />");
            sb.AppendLine("  </Key>");
        }

        foreach (var f in e.Fields)
        {
            var nullable = f.IsMandatory ? "" : " Nullable=\"true\"";
            sb.AppendLine($"  <Property Name=\"{f.Name}\" Type=\"Edm.String\"{nullable} />");
        }

        sb.AppendLine($"</EntityType>");
        sb.AppendLine($"<EntitySet Name=\"{e.Entity.PublicCollectionName ?? entityName + "s"}\" EntityType=\"{entityName}\" />");
        return sb.ToString();
    }
}

public sealed class GetReportCommand : Command<GetReportCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var r = RepoFactory.Create().GetReport(settings.Name);
        return RenderHelpers.Render(kind, r is null
            ? ToolResult<object>.Fail("REPORT_NOT_FOUND", $"Report '{settings.Name}' not found.")
            : ToolResult<object>.Success(r));
    }
}

public sealed class GetServiceCommand : Command<GetServiceCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var s = RepoFactory.Create().GetService(settings.Name);
        return RenderHelpers.Render(kind, s is null
            ? ToolResult<object>.Fail("SERVICE_NOT_FOUND", $"Service '{settings.Name}' not found.")
            : ToolResult<object>.Success(s));
    }
}

public sealed class GetServiceGroupCommand : Command<GetServiceGroupCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var g = RepoFactory.Create().GetServiceGroup(settings.Name);
        return RenderHelpers.Render(kind, g is null
            ? ToolResult<object>.Fail("SERVICE_GROUP_NOT_FOUND", $"Service group '{settings.Name}' not found.")
            : ToolResult<object>.Success(g));
    }
}

public sealed class GetObjectCommand : Command<GetObjectCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("class|table|edt|enum|form|menu-item|query|view|entity|report|service|service-group|role|duty|privilege")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var output = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Kind) || string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT", "Kind and name are required."));

        var repo = RepoFactory.Create();
        var (data, code, message) = ObjectLookup.Fetch(repo, settings.Kind, settings.Name);
        return RenderHelpers.Render(output, data is null
            ? ToolResult<object>.Fail(code!, message!,
                code == "BAD_INPUT" ? ObjectLookup.SupportedKindsHint : null)
            : ToolResult<object>.Success(data));
    }
}

/// <summary>
/// Batch object lookup — port of the upstream MCP <c>batch_get_info</c> tool.
/// Fetches up to 10 objects in one CLI invocation; one failed lookup never
/// fails the batch (each item carries its own ok/error).
/// </summary>
public sealed class GetBatchCommand : Command<GetBatchCommand.Settings>
{
    public const int MaxItems = 10;

    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<SPEC>")]
        [System.ComponentModel.Description("Object specs, each as <kind>:<name> (e.g. table:CustTable class:SalesLineType). Max 10 per call.")]
        public string[] Specs { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var output = OutputMode.Resolve(settings.Output);
        if (settings.Specs.Length == 0)
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT",
                "At least one <kind>:<name> spec is required.",
                "Example: d365fo get batch table:CustTable class:CustTableType edt:CustAccount"));
        if (settings.Specs.Length > MaxItems)
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT",
                $"Too many objects: {settings.Specs.Length} (max {MaxItems} per call).",
                "Split the request into multiple `get batch` calls."));

        var repo = RepoFactory.Create();
        var items = new List<object>(settings.Specs.Length);
        var found = 0;
        foreach (var spec in settings.Specs)
        {
            var idx = spec.IndexOf(':');
            if (idx <= 0 || idx == spec.Length - 1)
            {
                items.Add(new { spec, ok = false, error = new { code = "BAD_INPUT", message = $"Spec '{spec}' is not <kind>:<name>." } });
                continue;
            }
            var kind = spec[..idx].Trim();
            var name = spec[(idx + 1)..].Trim();
            var (data, code, message) = ObjectLookup.Fetch(repo, kind, name);
            if (data is null)
            {
                items.Add(new { spec, kind, name, ok = false, error = new { code, message } });
            }
            else
            {
                found++;
                items.Add(new { spec, kind, name, ok = true, data });
            }
        }

        return RenderHelpers.Render(output, ToolResult<object>.Success(new
        {
            requested = settings.Specs.Length,
            found,
            items,
        }));
    }
}

public sealed class GetBusinessEventCommand : Command<GetBusinessEventCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var item = repo.GetBusinessEvent(settings.Name);
        return RenderHelpers.Render(kind, item is null
            ? ToolResult<object>.Fail("NOT_FOUND", $"Business event '{settings.Name}' not found.")
            : ToolResult<object>.Success(item));
    }
}

public sealed class GetSecurityPolicyCommand : Command<GetSecurityPolicyCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var item = repo.GetSecurityPolicy(settings.Name);
        return RenderHelpers.Render(kind, item is null
            ? ToolResult<object>.Fail("NOT_FOUND", $"Security policy '{settings.Name}' not found.")
            : ToolResult<object>.Success(item));
    }
}
