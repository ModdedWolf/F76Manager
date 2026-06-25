namespace F76ManagerApp;

public static class ThemeIds
{
    public const string Default = "fallout";

    private static readonly HashSet<string> Valid = new(StringComparer.Ordinal)
    {
        "fallout",
        "vault-tec",
        "red-black",
        "black-white",
    };

    public static bool IsBuiltIn(string? id) => IsValid(id);

    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id) && Valid.Contains(id);

    public static string Normalize(string? id) => IsValid(id) ? id! : Default;

    public static string NormalizeUiTheme(string? id, Func<string, bool>? isUserTheme = null)
    {
        if (IsValid(id)) return id!;
        if (!string.IsNullOrWhiteSpace(id)
            && isUserTheme != null
            && isUserTheme(id)
            && Managers.ThemePackageValidator.IsValidThemeId(id))
            return id;
        return Default;
    }
}
