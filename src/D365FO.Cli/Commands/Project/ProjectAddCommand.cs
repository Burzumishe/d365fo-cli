using D365FO.Core;
using D365FO.Core.Journal;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Project;

/// <summary>
/// <c>d365fo project add</c> — list existing objects in a Visual Studio project. An object the
/// project does not list is never compiled; until now only Visual Studio or the MCP server could add
/// one, so a CLI-only session ended with objects nobody would build.
/// </summary>
public sealed class ProjectAddCommand : Command<ProjectAddCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [System.ComponentModel.Description("Path to the .rnrproj (or its folder), or a project name looked up under --root.")]
        public string Project { get; init; } = "";

        [CommandArgument(1, "<OBJECTS>")]
        [System.ComponentModel.Description("Objects as kind:Name (class:MyClass, form:MyForm, table-extension:CustTable.Extension_X) or AxClass/MyClass.")]
        public string[] Objects { get; init; } = Array.Empty<string>();

        [CommandOption("--root <DIR>")]
        [System.ComponentModel.Description("Folder searched for <PROJECT>.rnrproj when PROJECT is a name. Default: the current directory.")]
        public string? Root { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var projectPath = ResolveProject(settings.Project, settings.Root);
        if (projectPath is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("PROJECT_NOT_FOUND",
                $"No .rnrproj found for '{settings.Project}'.",
                "Pass the path to the .rnrproj, or the project name with --root <folder that contains the projects>."));

        var items = new List<VsProjectItems.Item>();
        foreach (var spec in settings.Objects)
        {
            var item = VsProjectItems.Parse(spec);
            if (item is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"'{spec}' is not kind:Name.", "Examples: class:MyClass_TKM, form:MyForm, table-extension:CustTable.Extension_TKM."));
            items.Add(item);
        }
        if (items.Count == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "At least one object is required."));

        var (project, format) = VsProjectItems.Load(projectPath);
        var model = VsProjectItems.ModelOf(project);

        // A project entry for a file that does not exist breaks the build of the whole project.
        var warnings = new List<string>();
        var modelFolder = model is null ? null : ResolveModelFolder(model);
        if (modelFolder is null)
        {
            warnings.Add($"Model folder for '{model ?? "(no <Model> in the project)"}' not found under the configured packages paths — the objects were not checked on disk.");
        }
        else
        {
            var missing = items.Where(i => !File.Exists(Path.Combine(modelFolder, i.AotRoot, i.Name + ".xml"))).ToList();
            if (missing.Count > 0)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("OBJECT_NOT_FOUND",
                    $"Not in model {model}: {string.Join(", ", missing.Select(m => $"{m.AotRoot}\\{m.Name}"))}.",
                    $"A project lists objects of its own model only ({modelFolder})."));
        }

        var results = VsProjectItems.Add(project, items);
        if (results.Any(r => r.Status == "added"))
            VsProjectItems.Save(project, projectPath, format);

        warnings.Add("Reload the project in Visual Studio if it is open.");
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            project = projectPath,
            model,
            items = results.Select(r => new { include = r.Include, status = r.Status }),
        }, warnings));
    }

    private static string? ResolveProject(string project, string? root)
    {
        if (File.Exists(project) && project.EndsWith(".rnrproj", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(project);
        if (Directory.Exists(project))
            return Directory.EnumerateFiles(project, "*.rnrproj", SearchOption.AllDirectories).FirstOrDefault();

        var searchRoot = root ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(searchRoot)) return null;
        var name = project.EndsWith(".rnrproj", StringComparison.OrdinalIgnoreCase) ? project : project + ".rnrproj";
        return Directory.EnumerateFiles(searchRoot, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? ResolveModelFolder(string model)
    {
        var cfg = D365FoSettings.FromEnvironment();
        return cfg.CustomPackagesPaths.Concat(new[] { cfg.PackagesPath })
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .SelectMany(r => D365FO.Core.Index.IndexSync.EnumerateModelDirs(r!, model))
            .FirstOrDefault();
    }
}
