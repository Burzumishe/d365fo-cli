using Dapper;

namespace D365FO.Core.Index;

/// <summary>
/// Which packages a model can see. X++ resolves a type only from the model's own package and
/// the packages its descriptor lists under ModuleReferences (transitively); a type that exists
/// in the installation but in an unreferenced package fails xppc with "does not denote a
/// class, a table, or an extended data type". The index knows every type, so without this
/// check generated code that names such a type passes validation and fails the build.
/// </summary>
public sealed partial class MetadataRepository
{
    /// <summary>Model name → package (module) name, derived from where its objects live on disk.</summary>
    public IReadOnlyDictionary<string, string> ModelPackages()
    {
        using var conn = OpenReadOnly();
        // <root>\<Package>\<Model>\AxTable\X.xml — the package is the model folder's parent.
        // Edts/Enums carry no SourcePath, so tables/classes/forms stand in for the model.
        var rows = conn.Query<(string Model, string? Path)>(@"
            SELECT m.Name AS Model,
                   COALESCE((SELECT SourcePath FROM Tables  t WHERE t.ModelId = m.ModelId AND SourcePath IS NOT NULL LIMIT 1),
                            (SELECT SourcePath FROM Classes c WHERE c.ModelId = m.ModelId AND SourcePath IS NOT NULL LIMIT 1),
                            (SELECT SourcePath FROM Forms   f WHERE f.ModelId = m.ModelId AND SourcePath IS NOT NULL LIMIT 1)) AS Path
            FROM Models m");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, path) in rows)
            map[model] = PackageFromSourcePath(path) ?? model;
        return map;
    }

    internal static string? PackageFromSourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        var aotFolder = Path.GetDirectoryName(sourcePath);      // ...\<Model>\AxTable
        var modelFolder = Path.GetDirectoryName(aotFolder);     // ...\<Model>
        var packageFolder = Path.GetDirectoryName(modelFolder); // ...\<Package>
        return packageFolder is null ? null : Path.GetFileName(packageFolder);
    }

    /// <summary>
    /// Packages visible from <paramref name="model"/>: its own package plus the transitive
    /// closure of ModuleReferences. Null when the model is not in the index.
    /// </summary>
    public HashSet<string>? VisiblePackages(string model)
    {
        var packages = ModelPackages();
        if (!packages.TryGetValue(model, out var own)) return null;

        // Package → its models → their descriptor ModuleReferences.
        var modelsByPackage = packages.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList(), StringComparer.OrdinalIgnoreCase);
        using var conn = OpenReadOnly();
        var depsByModel = conn.Query<(string Model, string Target)>(@"
                SELECT m.Name AS Model, d.Target AS Target
                FROM ModelDependencies d JOIN Models m ON m.ModelId = d.ModelId")
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Target).ToList(), StringComparer.OrdinalIgnoreCase);

        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { own };
        var queue = new Queue<string>(visible);
        while (queue.Count > 0)
        {
            var package = queue.Dequeue();
            if (!modelsByPackage.TryGetValue(package, out var models)) continue;
            foreach (var m in models)
            {
                if (!depsByModel.TryGetValue(m, out var targets)) continue;
                foreach (var t in targets)
                    if (visible.Add(t)) queue.Enqueue(t);
            }
        }
        return visible;
    }

    /// <summary>Models that declare an AOT type of this name (table, class, EDT, enum, view, …).</summary>
    public IReadOnlyList<string> ModelsDeclaring(string name)
    {
        using var conn = OpenReadOnly();
        return conn.Query<string>(@"
            SELECT m.Name FROM Models m WHERE m.ModelId IN (
                SELECT ModelId FROM Tables       WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM Classes      WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM Edts         WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM Enums        WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM Views        WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM DataEntities WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM Maps         WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM Queries      WHERE Name = @name COLLATE NOCASE
                UNION SELECT ModelId FROM Forms        WHERE Name = @name COLLATE NOCASE)",
            new { name }).ToList();
    }
}
