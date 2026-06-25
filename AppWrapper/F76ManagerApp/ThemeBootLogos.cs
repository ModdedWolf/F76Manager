namespace F76ManagerApp;

public static class ThemeBootLogos
{
    public static IReadOnlyDictionary<string, string> LogoPaths { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fallout"] = "assets/themes/fallout.png",
            ["vault-tec"] = "assets/themes/vault-tec.png",
            ["red-black"] = "assets/themes/red-black.png",
            ["black-white"] = "assets/themes/black-white.webp"
        };

    public static IReadOnlyList<string> BuiltInIds { get; } = new[] { "fallout", "vault-tec", "red-black", "black-white" };
}
