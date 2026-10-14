using System.Xml.Linq;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Knowledge;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Everything <c>generate coc</c> decides before it writes, shared by the CLI command and the
/// MCP <c>generate_object(objectType=coc)</c> handler so the two cannot drift: the target's
/// base name, its AOT kind (which picks the <c>[ExtensionOf]</c> intrinsic), the class name,
/// and a wrapper per method built from the base method's real declaration.
/// </summary>
public static class CocScaffold
{
    public sealed record Plan(
        XDocument Doc,
        string ClassName,
        string Target,
        string TargetKind,
        IReadOnlyList<string> Warnings);

    /// <param name="repo">The index, or null when it cannot be opened (an empty index is not fatal).</param>
    /// <param name="rawTarget">Target as the caller spelled it — may be a whole extension name.</param>
    /// <param name="targetKind">Explicit kind (class/table/form/…); resolved from the index when null.</param>
    /// <param name="className">Explicit class name; derived from the target and suffix when null.</param>
    /// <param name="suffix">Workspace suffix (<see cref="ExtensionNaming.ConfiguredSuffix"/>).</param>
    public static Plan Build(
        MetadataRepository? repo, string rawTarget, IReadOnlyList<string> methods,
        string? targetKind, string? className, string? suffix)
    {
        var warnings = new List<string>();

        // A target spelled as the extension itself (Base.Suffix, Base_Extension,
        // Base_TKM_Extension) is rewritten to its base rather than suffixed twice.
        var target = ObjectNamingRules.NormalizeExtensionTarget(rawTarget, out var renameNote, suffix);
        if (renameNote is not null) warnings.Add(renameNote);

        var kind = targetKind?.Trim().ToLowerInvariant();
        if (repo is not null)
        {
            try
            {
                var existing = repo.FindCocExtensions(target);
                if (existing.Count > 0)
                    warnings.Add($"There are already {existing.Count} CoC extension(s) of {target}. Consider extending an existing one instead of stacking a new wrapper.");

                if (kind is null)
                {
                    var kinds = repo.SymbolKinds(target);
                    kind = SymbolKindPriority.PreferredCocTarget(kinds);
                    var others = kinds.Where(k => k != kind && (k is "table" or "class" or "form" or "view" or "data-entity" or "map")).ToList();
                    if (kind is not null && others.Count > 0)
                        warnings.Add($"\"{target}\" is also a {string.Join(", ", others)}; wrapped as {kind}. Pass the target kind explicitly to wrap another.");
                }
            }
            catch { /* index may be empty; not fatal */ }
        }
        kind ??= "class";

        var sources = new List<(string Name, string Source)>();
        foreach (var method in methods)
        {
            var wrapper = BaseDeclaration(repo, target, kind, method) is { } declaration
                ? CocWrapper.FromBaseSource(method, declaration)
                : null;
            if (wrapper is null)
            {
                warnings.Add($"Signature of {target}::{method} not found — wrapper written as `public void {method}()`. " +
                             "Replace it with the base method's exact signature (without default values) before building.");
                wrapper = CocWrapper.Stub(method);
            }
            sources.Add((method, wrapper));
        }

        var name = string.IsNullOrWhiteSpace(className) ? ExtensionNaming.CocClassName(target, suffix) : className!.Trim();
        return new Plan(XppScaffolder.CocExtension(target, kind, name, sources), name, target, kind, warnings);
    }

    /// <summary>
    /// The base method's source, read from the declaring object's XML: the target itself, then
    /// its base classes. The symbol index keeps only the first line of a multi-line declaration,
    /// so it is the fallback, and only when that line closes the parameter list. Table data
    /// methods a table does not override (validateWrite, insert, …) come from the kernel table.
    /// </summary>
    private static string? BaseDeclaration(MetadataRepository? repo, string target, string kind, string method)
    {
        if (repo is null) return null;
        try
        {
            var owner = target;
            for (var depth = 0; depth < 16 && owner is not null; depth++)
            {
                var (path, extends) = kind switch
                {
                    "table" => (repo.GetTableDetails(owner)?.Table?.SourcePath, (string?)null),
                    "form" => (repo.GetForm(owner)?.Form?.SourcePath, (string?)null),
                    _ => repo.GetClassDetails(owner)?.Class is { } c ? (c.SourcePath, c.Extends) : (null, null),
                };
                var src = path is null ? null : XppSourceReader.Read(path);
                var hit = src is null ? null : XppSourceReader.FindMethod(src, method);
                if (hit is not null) return hit.Body;
                owner = string.IsNullOrWhiteSpace(extends) ? null : extends;
            }

            var indexed = repo.FindMethod(target, method)?.Signature;
            if (!string.IsNullOrWhiteSpace(indexed) && indexed.Contains(')')) return indexed;

            if (TableDataMethods.AppliesTo(kind) && TableDataMethods.Lookup(method) is { } kernel)
                return kernel.Signature;
        }
        catch { /* unreadable source is the same as no source */ }
        return null;
    }
}
