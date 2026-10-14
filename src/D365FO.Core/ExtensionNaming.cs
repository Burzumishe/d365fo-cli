namespace D365FO.Core;

/// <summary>
/// The workspace's naming suffix (<c>D365FO_EXTENSION_SUFFIX</c>, e.g. <c>_TKM</c>) and the
/// extension names it produces. Without it the generators knew two shapes only —
/// <c>Target_Extension</c> for CoC classes and <c>Target.&lt;Model&gt;</c> for element
/// extensions — so a suffix-only convention (<c>CustTable_TKM_Extension</c>,
/// <c>CustTable.Extension_TKM</c>) could not be produced: passing <c>CustTable_TKM</c> as the
/// CoC target named the class right but wrapped a class that does not exist.
/// </summary>
public static class ExtensionNaming
{
    public const string SuffixKey = "D365FO_EXTENSION_SUFFIX";

    /// <summary>The configured suffix, or null when the workspace has none.</summary>
    public static string? ConfiguredSuffix()
    {
        var value = D365FoSettings.Resolve(SuffixKey)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// CoC class name: <c>{Target}{Suffix}_Extension</c> (<c>CustTable_TKM_Extension</c>), or
    /// <c>{Target}_Extension</c> without a suffix.
    /// </summary>
    public static string CocClassName(string target, string? suffix)
        => string.IsNullOrEmpty(suffix) ? $"{target}_Extension" : $"{target}{suffix}_Extension";

    /// <summary>
    /// The token after the dot of an element extension: <c>Extension{Suffix}</c>
    /// (<c>CustTable.Extension_TKM</c>) when a suffix is configured, else the model name
    /// (the Visual Studio default), else <c>Extension</c>.
    /// </summary>
    public static string ElementExtensionToken(string? suffix, string? model)
    {
        if (!string.IsNullOrEmpty(suffix)) return $"Extension{suffix}";
        return string.IsNullOrWhiteSpace(model) ? "Extension" : model!;
    }
}
