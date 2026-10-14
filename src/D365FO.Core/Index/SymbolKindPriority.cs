namespace D365FO.Core.Index;

/// <summary>
/// Which kind a name means when several share it. <see cref="MetadataRepository.SymbolKinds"/>
/// comes back from a SQL UNION, i.e. alphabetically, which put "form" ahead of "table" for
/// every Table/Form pair (SalesTable, CustTable). A CoC or field change on such a name almost
/// always means the table (or the class).
/// </summary>
public static class SymbolKindPriority
{
    private static readonly string[] Order =
        ["table", "class", "form", "view", "data-entity", "query", "map", "enum", "edt", "menu-item"];

    /// <summary>The highest-priority kind in <paramref name="kinds"/>, or the first one when none ranks.</summary>
    public static string? Preferred(IReadOnlyList<string> kinds)
    {
        foreach (var k in Order)
            if (kinds.Contains(k)) return k;
        return kinds.FirstOrDefault();
    }

    /// <summary>The highest-priority kind among those a CoC class can extend.</summary>
    public static string? PreferredCocTarget(IReadOnlyList<string> kinds)
    {
        foreach (var k in Order)
            if ((k is "table" or "class" or "form" or "view" or "data-entity" or "map") && kinds.Contains(k)) return k;
        return null;
    }
}
