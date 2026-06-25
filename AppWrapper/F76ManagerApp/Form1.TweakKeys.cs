namespace F76ManagerApp;

public partial class Form1
{
    private static readonly HashSet<string> TweakUiKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "godrays", "grass", "rendergrass", "grassfade", "shadows", "shadowres", "shadowfilter",
        "focusshadows", "volumquality", "fastload", "texturequality", "treedist", "lod", "decals",
        "decalsperframe", "ssr", "rainocclusion", "npcshadowlights", "tiledlighting", "ao", "blood",
        "gridload", "cellloads", "vsync", "fpscap", "fov", "fov1st", "fovpipboy", "motionblur", "dof",
        "lensflare", "extrablur", "vatsblur", "taa", "aniso", "water", "lodsky", "leafanim", "gamma",
        "glassshader", "pbrshadows", "corpsehighlight", "playernames", "playerpings",
        "conversationhistory", "pipboyfx", "ping", "bandwidth", "skipsplash",
        "steamfpscap", "xboxfpscap"
    };

    private static readonly HashSet<string> PlatformTweakFieldSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Godrays", "Dof", "Grass", "Ping", "Bandwidth", "Fastload", "Vsync", "Ao", "Blood",
        "DofSpecific", "LensFlare", "ExtraBlur", "VatsBlur", "Shadows", "Taa", "Fov", "Fov1st",
        "FovPipboy", "FpsCap", "Aniso", "Water", "Lod", "Decals", "PipboyFx", "VolumQuality",
        "ShadowRes", "ShadowFilter", "TextureQuality", "DecalsPerFrame", "GridLoad", "CorpseHighlight",
        "FocusShadows", "RenderGrass", "Ssr", "RainOcclusion", "NpcShadowLights", "CellLoads",
        "TiledLighting", "SkipSplash", "GlassShader", "PbrShadows", "PlayerNames", "PlayerPings",
        "GrassFade", "TreeDist", "LodSky", "LeafAnim", "ConversationHistory", "Gamma"
    };

    private static bool IsTweakSettingKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (string.Equals(key, "activeTweaksPreset", StringComparison.OrdinalIgnoreCase)) return true;
        if (TweakUiKeys.Contains(key)) return true;

        if (key.StartsWith("steam", StringComparison.OrdinalIgnoreCase) && key.Length > 5)
        {
            string suffix = key[5..];
            if (PlatformTweakFieldSuffixes.Contains(suffix)) return true;
        }

        if (key.StartsWith("xbox", StringComparison.OrdinalIgnoreCase) && key.Length > 4)
        {
            string suffix = key[4..];
            if (PlatformTweakFieldSuffixes.Contains(suffix)) return true;
        }

        return false;
    }
}
