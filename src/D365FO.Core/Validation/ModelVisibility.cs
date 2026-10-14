using D365FO.Core.Index;

namespace D365FO.Core.Validation;

/// <summary>
/// Flags types the code names that exist in the installation but not in any package the
/// target model references. ReferenceResolver proves a type exists; this proves the model
/// can compile against it — the two differ exactly when a model's descriptor is missing a
/// ModuleReference (e.g. code in a model that references only ApplicationPlatform and
/// ApplicationFoundation using CustTable, which lives in ApplicationSuite).
/// </summary>
public static class ModelVisibility
{
    /// <summary>
    /// The model to check against when the caller names none: the single configured custom
    /// model, if there is exactly one. Null otherwise — guessing among several would report
    /// errors against the wrong descriptor.
    /// </summary>
    public static string? DefaultModel(D365FoSettings settings)
        => settings.CustomModels.Count == 1 ? settings.CustomModels[0] : null;

    public static IReadOnlyList<ReferenceViolation> Check(ResolveResult result, MetadataRepository repo, string model)
    {
        var violations = new List<ReferenceViolation>();
        if (result.IndexedTypes is not { Count: > 0 } types) return violations;

        var visible = repo.VisiblePackages(model);
        if (visible is null) return violations; // model not indexed — nothing to check against

        var packages = repo.ModelPackages();
        foreach (var (name, line) in types)
        {
            var owners = repo.ModelsDeclaring(name)
                .Select(m => packages.TryGetValue(m, out var p) ? p : m)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (owners.Count == 0 || owners.Any(visible.Contains)) continue;

            violations.Add(new ReferenceViolation("not-visible-from-model", "error", line, name,
                $"\"{name}\" exists, but only in package {string.Join(" / ", owners)}, which model {model} does not reference. " +
                $"xppc will reject it (\"does not denote a class, a table, or an extended data type\"). " +
                $"Add the package to {model}'s Descriptor ModuleReferences, or use a type from a referenced package " +
                $"({string.Join(", ", visible.OrderBy(v => v))})."));
        }
        return violations;
    }
}
