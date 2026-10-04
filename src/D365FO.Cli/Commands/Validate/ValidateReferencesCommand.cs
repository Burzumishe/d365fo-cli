using D365FO.Core;
using D365FO.Core.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Validate;

/// <summary>
/// Semantic reference resolver (&lt;200 ms, index-only) — port of the upstream
/// MCP <c>resolve_references</c> anti-hallucination gate. Verifies that every
/// type, table field, method (incl. arity), enum, label and intrinsic target
/// (tableStr, fieldStr, classStr, …) in generated X++ code EXISTS in the
/// indexed codebase. Catches hallucinated symbols BEFORE the compiler does.
/// Exit codes: 0 = clean (or warnings only), 1 = command failure, 2 = errors found.
/// </summary>
public sealed class ValidateReferencesCommand : Command<ValidateReferencesCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[FILE]")]
        [System.ComponentModel.Description("Path to the X++ file to resolve. Omit to read from stdin.")]
        public string? File { get; init; }

        [CommandOption("--context <NAME>")]
        [System.ComponentModel.Description("Owning class/table name, used in diagnostic messages.")]
        public string? Context { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Model the code will be compiled in. Every type it names must live in a package that model references (Descriptor ModuleReferences). Default: the single model in D365FO_CUSTOM_MODELS; skipped when there are several.")]
        public string? Model { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var (code, error) = ValidateInput.ReadCode(settings.File);
        if (error is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("INPUT_NOT_FOUND", error,
                "Pass a file path or pipe code via stdin: `cat MyClass.xpp | d365fo validate references`."));

        D365FO.Core.Index.MetadataRepository repo;
        try
        {
            repo = RepoFactory.Create();
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_INDEX",
                $"Reference resolution requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first."));
        }

        var result = ReferenceResolver.Resolve(code!, repo);
        var model = settings.Model ?? ModelVisibility.DefaultModel(D365FoSettings.FromEnvironment());
        if (model is not null)
            result = result with { Violations = [.. ModelVisibility.Check(result, repo, model), .. result.Violations] };
        var errors = result.Violations.Count(v => v.Severity == "error");
        var warnings = result.Violations.Count(v => v.Severity == "warning");

        var envelope = ToolResult<object>.Success(new
        {
            context = settings.Context,
            model,
            verifiedCount = result.VerifiedCount,
            errors,
            warnings,
            violations = result.Violations.Select(v => new
            {
                kind = v.Kind,
                severity = v.Severity,
                line = v.Line,
                identifier = v.Identifier,
                detail = v.Detail,
            }),
            verdict = errors > 0
                ? "Fix all errors before writing — these identifiers do not exist in the indexed codebase."
                : warnings > 0
                    ? "Warnings are informational (kernel classes and new labels are not indexable). Review, then proceed."
                    : $"All {result.VerifiedCount} reference(s) verified against the index. No hallucinated symbols detected.",
        });

        var rc = RenderHelpers.Render(kind, envelope, _ =>
        {
            foreach (var v in result.Violations)
            {
                var colour = v.Severity == "error" ? "red" : "yellow";
                var line = v.Line > 0 ? $" (line {v.Line})" : "";
                AnsiConsole.MarkupLine($"[{colour}]{v.Kind}[/]{line} {RenderHelpers.Escape(v.Identifier)}");
                AnsiConsole.MarkupLine($"  [grey]{RenderHelpers.Escape(v.Detail)}[/]");
            }
            AnsiConsole.MarkupLine(errors > 0
                ? $"[red]{errors} error(s)[/], [yellow]{warnings} warning(s)[/] — {result.VerifiedCount} verified"
                : $"[green]{result.VerifiedCount} reference(s) verified[/] ({warnings} warning(s))");
        });
        return rc != 0 ? rc : errors > 0 ? 2 : 0;
    }
}
