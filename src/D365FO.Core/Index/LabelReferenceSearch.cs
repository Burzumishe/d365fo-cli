using System.Text.RegularExpressions;

namespace D365FO.Core.Index;

/// <summary>An AOT object whose XML mentions the searched label.</summary>
public sealed record LabelRefHit(
    string Kind,
    string Name,
    string Model,
    string Path,
    IReadOnlyList<SourceRefMatchLine> Matches);

/// <summary>Result of a label where-used search.</summary>
/// <param name="Needle">The label token that was searched for.</param>
/// <param name="LabelFile">The label file the token belongs to.</param>
/// <param name="OwnerModel">The model that ships the label file, when the index knows it.</param>
/// <param name="ModelsScanned">The models whose folders were read.</param>
/// <param name="FilesScanned">AOT XML files read.</param>
/// <param name="Truncated">The limit was reached, so there may be more.</param>
/// <param name="Hits">The objects that use the label.</param>
/// <param name="Caveat">What the scan did not cover, when that matters for an empty result.</param>
public sealed record LabelRefResult(
    string Needle,
    string LabelFile,
    string? OwnerModel,
    IReadOnlyList<string> ModelsScanned,
    int FilesScanned,
    bool Truncated,
    IReadOnlyList<LabelRefHit> Hits,
    string? Caveat);

/// <summary>
/// Where-used for a label token (<c>@File:Key</c> or legacy <c>@SYS12345</c>).
/// </summary>
/// <remarks>
/// A label is mostly used in metadata — a form <c>Caption</c>, a menu item or field
/// <c>Label</c>, a <c>HelpText</c> — not in X++ method bodies, so the method-source search
/// behind <c>find refs</c> cannot answer it. This reads the whole XML of every AOT object
/// instead, which covers properties and X++ source alike.
///
/// Only a model that ships the label file or depends on it can reference the label, so the
/// scan is limited to those models. Reading every package in PackagesLocalDirectory takes
/// minutes; reading one custom model takes about a second.
/// </remarks>
public static class LabelReferenceSearch
{
    private static readonly Regex LabelToken = new(
        @"^@[A-Za-z_][A-Za-z0-9_]*(?::[A-Za-z0-9_]+)?$", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="needle"/> is a label reference rather than a symbol.</summary>
    public static bool IsLabelToken(string needle) => LabelToken.IsMatch(needle.Trim());

    public static LabelRefResult Find(
        MetadataRepository repo,
        string needle,
        string? kind,
        string? model,
        int limit,
        int sampleLinesPerHit = 3)
    {
        var token = needle.Trim();
        var labelFile = LabelFileOf(token);
        var ownerRoot = OwnerModelRoot(repo, labelFile);
        var ownerModel = ownerRoot is null ? null : Path.GetFileName(ownerRoot);

        var roots = new Dictionary<string, string>(repo.GetModelRootDirectories(), StringComparer.OrdinalIgnoreCase);
        // A model that holds only labels has no indexed object to read its folder from.
        if (ownerModel is not null) roots.TryAdd(ownerModel, ownerRoot!);

        var (models, caveat) = ModelsToScan(repo, ownerModel, ownerRoot, model);

        // "@File:Key" must not match inside "@File:KeyLonger", nor "@SYS12" inside "@SYS123".
        var rx = new Regex($@"(?<![\w@:]){Regex.Escape(token)}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hits = new List<LabelRefHit>();
        var scannedModels = new List<string>();
        int scanned = 0;
        bool truncated = false;

        foreach (var m in models)
        {
            if (truncated) break;
            if (!roots.TryGetValue(m, out var root) || !Directory.Exists(root)) continue;
            scannedModels.Add(m);
            foreach (var typeDir in Directory.EnumerateDirectories(root, "Ax*").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var typeName = Path.GetFileName(typeDir);
                if (typeName.Equals("AxLabelFile", StringComparison.OrdinalIgnoreCase)) continue;
                if (!KindMatches(typeName, kind)) continue;
                foreach (var file in Directory.EnumerateFiles(typeDir, "*.xml", SearchOption.AllDirectories)
                                              .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    scanned++;
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    // cheap pre-check: nearly every file does not mention the label at all
                    if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var sample = SampleLines(text, rx, sampleLinesPerHit);
                    if (sample.Count == 0) continue;
                    if (hits.Count >= limit) { truncated = true; break; }
                    hits.Add(new LabelRefHit(typeName[2..], Path.GetFileNameWithoutExtension(file), m, file, sample));
                }
                if (truncated) break;
            }
        }

        if (scannedModels.Count == 0)
            caveat = "Nothing was searched: no folder on disk could be located for "
                     + (models.Count == 0 ? "any model" : string.Join(", ", models))
                     + ". An empty result here is not evidence that the label is unused — check "
                     + "D365FO_PACKAGES_PATH, or refresh the index on this machine.";

        return new LabelRefResult(token, labelFile, ownerModel, scannedModels, scanned, truncated, hits, caveat);
    }

    /// <summary>The label file a token belongs to: "@Foo:Bar" -> "Foo", legacy "@SYS12345" -> "SYS".</summary>
    private static string LabelFileOf(string token)
    {
        var raw = token.TrimStart('@');
        var colon = raw.IndexOf(':');
        if (colon > 0) return raw[..colon];
        var letters = 0;
        while (letters < raw.Length && char.IsLetter(raw[letters])) letters++;
        return letters > 0 ? raw[..letters] : raw;
    }

    /// <summary>
    /// Folder of the model that ships <paramref name="labelFile"/>: the parent of the
    /// <c>AxLabelFile</c> folder its <c>.label.txt</c> lives under
    /// (<c>AxLabelFile/LabelResources/&lt;lang&gt;/</c>, or directly in <c>AxLabelFile</c> in the legacy layout).
    /// </summary>
    private static string? OwnerModelRoot(MetadataRepository repo, string labelFile)
    {
        foreach (var path in repo.GetLabelFilePaths(labelFile))
        {
            for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (!Path.GetFileName(dir).Equals("AxLabelFile", StringComparison.OrdinalIgnoreCase)) continue;
                var modelRoot = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(modelRoot)) return modelRoot;
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// The models that can reference the label: the one that ships it and the custom models that
    /// depend on it. A label shipped by a standard model is usable by all of them, and scanning
    /// every standard package is what made this take minutes, so only custom models are read then.
    /// </summary>
    private static (IReadOnlyList<string> Models, string? Caveat) ModelsToScan(
        MetadataRepository repo, string? ownerModel, string? ownerRoot, string? requestedModel)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel))
            return (new[] { requestedModel.Trim() }, null);

        var custom = repo.ListModels().Where(m => m.IsCustom).Select(m => m.Name).ToList();
        var ownerIsCustom = ownerModel is not null && custom.Contains(ownerModel, StringComparer.OrdinalIgnoreCase);
        if (!ownerIsCustom)
        {
            var where = ownerModel is null ? "an unknown model" : $"standard model {ownerModel}";
            return (custom,
                $"The label file is shipped by {where}; only the custom models were scanned. "
                + "Pass --model <Name> to scan one standard model, or --xref for the cross-reference database.");
        }

        // Descriptors reference packages (modules), which is the folder above the model's own folder.
        var ownerPackage = Path.GetFileName(Path.GetDirectoryName(ownerRoot!)) ?? ownerModel!;
        var graph = repo.GetDependencyGraph();
        var models = new List<string> { ownerModel! };
        foreach (var m in custom)
        {
            if (m.Equals(ownerModel, StringComparison.OrdinalIgnoreCase)) continue;
            if (DependsOn(graph, m, ownerPackage, ownerModel!)) models.Add(m);
        }
        return (models, null);
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
