using System.Text.RegularExpressions;

namespace D365FO.Core.Index;

/// <summary>An AOT object whose XML mentions the searched token.</summary>
public sealed record AotXmlHit(
    string Kind,
    string Name,
    string Model,
    string Path,
    IReadOnlyList<SourceRefMatchLine> Matches);

/// <summary>Result of <see cref="ModelXmlScan.Scan"/>.</summary>
public sealed record AotXmlScanResult(
    IReadOnlyList<string> ModelsScanned,
    int FilesScanned,
    bool Truncated,
    IReadOnlyList<AotXmlHit> Hits);

/// <summary>
/// Reads the whole XML of every AOT object in a set of model folders. Shared by the label and
/// the custom-object where-used: both look for a name that is mostly used in metadata
/// properties (a form caption, a button's <c>MenuItemName</c>, a privilege entry point), which
/// the method-source index never sees.
/// </summary>
/// <remarks>
/// Only the model that owns the searched element, and the custom models that depend on it, can
/// reference it. Reading every package in PackagesLocalDirectory takes minutes; reading one
/// custom model takes about a second.
/// </remarks>
public static class ModelXmlScan
{
    /// <param name="token">Literal text every hit contains; files without it are skipped unparsed.</param>
    /// <param name="rx">The exact match, applied per line to the files that contain <paramref name="token"/>.</param>
    /// <param name="kind">AOT folder prefix filter (see <see cref="KindMatches"/>), or null for every folder.</param>
    /// <param name="skip">Files that are not a reference, such as the searched object's own definition.</param>
    public static AotXmlScanResult Scan(
        IReadOnlyList<string> models,
        IReadOnlyDictionary<string, string> modelRoots,
        string token,
        Regex rx,
        string? kind,
        int limit,
        int sampleLinesPerHit,
        Func<string, string, bool>? skip = null)
    {
        var hits = new List<AotXmlHit>();
        var scannedModels = new List<string>();
        int scanned = 0;
        bool truncated = false;

        foreach (var m in models)
        {
            if (truncated) break;
            if (!modelRoots.TryGetValue(m, out var root) || !Directory.Exists(root)) continue;
            scannedModels.Add(m);
            foreach (var typeDir in Directory.EnumerateDirectories(root, "Ax*").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var typeName = Path.GetFileName(typeDir);
                if (typeName.Equals("AxLabelFile", StringComparison.OrdinalIgnoreCase)) continue;
                if (!KindMatches(typeName, kind)) continue;
                foreach (var file in Directory.EnumerateFiles(typeDir, "*.xml", SearchOption.AllDirectories)
                                              .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (skip is not null && skip(typeName, name)) continue;
                    scanned++;
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    // cheap pre-check: nearly every file does not mention the token at all
                    if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var sample = SampleLines(text, rx, sampleLinesPerHit);
                    if (sample.Count == 0) continue;
                    if (hits.Count >= limit) { truncated = true; break; }
                    hits.Add(new AotXmlHit(typeName[2..], name, m, file, sample));
                }
                if (truncated) break;
            }
        }

        return new AotXmlScanResult(scannedModels, scanned, truncated, hits);
    }

    /// <summary>
    /// The owner model followed by every custom model that depends on it, directly or through
    /// another model.
    /// </summary>
    public static IReadOnlyList<string> OwnerAndDependentCustomModels(
        MetadataRepository repo, string ownerModel, string ownerRoot)
    {
        // Descriptors reference packages (modules), which is the folder above the model's own folder.
        var ownerPackage = Path.GetFileName(Path.GetDirectoryName(ownerRoot)) ?? ownerModel;
        var graph = repo.GetDependencyGraph();
        var models = new List<string> { ownerModel };
        foreach (var m in repo.ListModels().Where(m => m.IsCustom).Select(m => m.Name))
        {
            if (m.Equals(ownerModel, StringComparison.OrdinalIgnoreCase)) continue;
            if (DependsOn(graph, m, ownerPackage, ownerModel)) models.Add(m);
        }
        return models;
    }

    private static bool DependsOn(
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph, string model, string ownerPackage, string ownerModel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(model);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current) || !graph.TryGetValue(current, out var targets)) continue;
            foreach (var target in targets)
            {
                if (target.Equals(ownerPackage, StringComparison.OrdinalIgnoreCase)
                    || target.Equals(ownerModel, StringComparison.OrdinalIgnoreCase))
                    return true;
                pending.Push(target);
            }
        }
        return false;
    }

    /// <summary>
    /// Prefix of the AOT folder: "form" matches AxForm and AxFormExtension, "menuitem" every
    /// AxMenuItem* folder. No filter matches every folder.
    /// </summary>
    private static bool KindMatches(string typeDir, string? kind)
        => string.IsNullOrWhiteSpace(kind)
           || typeDir.StartsWith("Ax" + kind.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The first matching lines; at least one, so a file with a match is always a hit.</summary>
    private static IReadOnlyList<SourceRefMatchLine> SampleLines(string text, Regex rx, int max)
    {
        var res = new List<SourceRefMatchLine>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length && res.Count < Math.Max(max, 1); i++)
            if (rx.IsMatch(lines[i]))
                res.Add(new SourceRefMatchLine(i + 1, lines[i].Trim()));
        return res;
    }
}
