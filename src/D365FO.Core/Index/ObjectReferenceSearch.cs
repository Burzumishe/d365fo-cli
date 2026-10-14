using System.Text.RegularExpressions;

namespace D365FO.Core.Index;

/// <summary>Result of a where-used for an AOT object that only custom models define.</summary>
/// <param name="Needle">The object name that was searched for.</param>
/// <param name="Owners">The objects with that name, as <c>Kind:Model</c>.</param>
/// <param name="ModelsScanned">The models whose folders were read.</param>
/// <param name="FilesScanned">AOT XML files read.</param>
/// <param name="Truncated">The limit was reached, so there may be more.</param>
/// <param name="Hits">The objects that mention the name.</param>
/// <param name="Caveat">What the scan did not cover, when that matters for an empty result.</param>
public sealed record ObjectRefResult(
    string Needle,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> ModelsScanned,
    int FilesScanned,
    bool Truncated,
    IReadOnlyList<AotXmlHit> Hits,
    string? Caveat);

/// <summary>
/// Where-used for a bare AOT object name (<c>IntEDIDistribution_TKM</c>, <c>CustTable</c>).
/// </summary>
/// <remarks>
/// An object defined in a custom model can only be referenced by that model and the custom
/// models that depend on it — standard code never references ours. Reading the whole XML of
/// those few models finds every use, including metadata properties (a button's
/// <c>MenuItemName</c>, a privilege entry point, a form datasource) and code that has not been
/// built yet, which DYNAMICSXREFDB does not have.
///
/// A standard object can be referenced from every package, so it is looked up in
/// DYNAMICSXREFDB by its exact AOT path (<see cref="XrefPaths"/>), the way Visual Studio's
/// Find references does.
/// </remarks>
public static class ObjectReferenceSearch
{
    /// <summary>
    /// Scans the owner and dependent custom models, or returns null when the name is not an
    /// indexed object or a standard model defines one of its objects.
    /// </summary>
    public static ObjectRefResult? FindInCustomModels(
        MetadataRepository repo, string name, string? kind, int limit, int sampleLinesPerHit = 3)
    {
        var owners = repo.FindObjectsByName(name);
        if (owners.Count == 0 || owners.Any(o => !o.IsCustom)) return null;

        var roots = repo.GetModelRootDirectories();
        var models = new List<string>();
        foreach (var owner in owners.Select(o => o.Model).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var reachable = roots.TryGetValue(owner, out var root)
                ? ModelXmlScan.OwnerAndDependentCustomModels(repo, owner, root)
                : [owner];
            models.AddRange(reachable.Where(m => !models.Contains(m, StringComparer.OrdinalIgnoreCase)));
        }

        // The object's own definition names it, but is not a reference to it.
        var definitionFolders = owners.Select(o => "Ax" + o.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex($@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var scan = ModelXmlScan.Scan(models, roots, name, rx, kind, limit, sampleLinesPerHit,
            skip: (typeFolder, file) => definitionFolders.Contains(typeFolder)
                                        && file.Equals(name, StringComparison.OrdinalIgnoreCase));

        string? caveat = null;
        if (scan.ModelsScanned.Count == 0)
            caveat = "Nothing was searched: no folder on disk could be located for "
                     + string.Join(", ", models)
                     + ". An empty result here is not evidence that the object is unused — check "
                     + "D365FO_PACKAGES_PATH, or refresh the index on this machine.";

        return new ObjectRefResult(
            name, owners.Select(o => $"{o.Kind}:{o.Model}").ToList(),
            scan.ModelsScanned, scan.FilesScanned, scan.Truncated, scan.Hits, caveat);
    }

    /// <summary>
    /// The DYNAMICSXREFDB paths of every indexed object called <paramref name="name"/>
    /// (<c>/MenuItemDisplays/Name</c>, <c>/Tables/Name</c>, …); empty when the index does not
    /// know the name.
    /// </summary>
    public static IReadOnlyList<string> XrefPaths(MetadataRepository repo, string name)
        => repo.FindObjectsByName(name)
               .Select(o => "/" + XrefFolder(o.Kind) + "/" + name)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList();

    /// <summary>True when a standard model defines an object called <paramref name="name"/>.</summary>
    public static bool IsStandardObject(MetadataRepository repo, string name)
        => repo.FindObjectsByName(name).Any(o => !o.IsCustom);

    /// <summary>
    /// The compiler names its root folders in the plural: Table → Tables, Query → Queries,
    /// Class → Classes, MenuItemDisplay → MenuItemDisplays.
    /// </summary>
    private static string XrefFolder(string kind)
    {
        if (kind.EndsWith('y') && kind.Length > 1 && !"aeiou".Contains(kind[^2])) return kind[..^1] + "ies";
        if (kind.EndsWith('s')) return kind + "es";
        return kind + "s";
    }
}
