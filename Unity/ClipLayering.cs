namespace UnityBrowser.Unity;

/// <summary>An AnimationClip plus the other clips it plays together with in the game.</summary>
public sealed class LayeredClip
{
    /// <summary>Layers in playback order: later layers override earlier ones for the properties they animate.</summary>
    public List<(ClipData Clip, string Role)> Layers { get; } = new();
    /// <summary>The clip that was asked for.</summary>
    public required ClipData Primary { get; init; }
    /// <summary>Set when the clip is an override layer but the bundle holding its base clip is not open.</summary>
    public string? MissingDependency { get; init; }

    public bool IsLayered => Layers.Count > 1;
    public float StartTime => Layers.Min(l => l.Clip.StartTime);
    public float StopTime => Layers.Max(l => l.Clip.StopTime);
    public IEnumerable<ClipBinding> AllTransformBindings => Layers.SelectMany(l => l.Clip.Bindings.Where(b => b.IsTransform && b.Attribute is >= 1 and <= 4));
}

/// <summary>
/// Skin (cosmetic) clips are often override layers: an AnimatorOverrideController replaces an empty
/// placeholder clip on the controller's cosmetic layer, while the body layer keeps playing the base clip.
/// Example (Warcraft Rumble): ThrallShaman_Override maps AbilityMelee_Cosmetic -> ThrallShaman@AbilityChop,
/// and the body plays Thrall@AbilityChop from the base unit bundle. This pairs such a clip with its base
/// clip (same name after the '@'), found in the controller's bundle when that bundle is open.
/// </summary>
public static class ClipLayering
{
    public static LayeredClip Resolve(SerializedFile sf, ObjectInfo clipObj)
    {
        var clip = AnimationClipReader.Read(sf, clipObj);
        string? missing = null;

        foreach (var ov in sf.Objects.Where(o => o.TypeName == "AnimatorOverrideController"))
        {
            FieldValue root;
            try { root = AssetPreview.ReadObject(sf, ov); }
            catch { continue; }

            bool overridesThis = root["m_Clips"]?.Children.Any(pair =>
                Convert.ToInt32(pair["m_OverrideClip"]?["m_FileID"]?.Value) == 0 &&
                Convert.ToInt64(pair["m_OverrideClip"]?["m_PathID"]?.Value) == clipObj.PathId) == true;
            if (!overridesThis) continue;

            int controllerFile = Convert.ToInt32(root["m_Controller"]?["m_FileID"]?.Value);
            var baseFile = FileRegistry.Resolve(sf, controllerFile);
            if (baseFile == null)
            {
                missing = controllerFile > 0 && controllerFile <= sf.Externals.Count
                    ? FileRegistry.ExternalName(sf.Externals[controllerFile - 1]) : null;
                continue;
            }

            var baseClip = FindBaseClip(baseFile, clip.Name);
            if (baseClip == null) continue;
            var layered = new LayeredClip { Primary = clip };
            layered.Layers.Add((baseClip, "body"));
            layered.Layers.Add((clip, "cosmetic layer"));
            return layered;
        }

        var single = new LayeredClip { Primary = clip, MissingDependency = missing };
        single.Layers.Add((clip, "clip"));
        return single;
    }

    /// <summary>The base clip for "Skin@Action" is the clip named "Something@Action" that animates transforms.</summary>
    private static ClipData? FindBaseClip(SerializedFile baseFile, string skinClipName)
    {
        int at = skinClipName.IndexOf('@');
        if (at < 0) return null;
        string action = skinClipName[(at + 1)..];
        foreach (var o in baseFile.Objects.Where(AnimationClipReader.CanRead))
        {
            string name = ObjectNames.GetName(baseFile, o) ?? "";
            int a = name.IndexOf('@');
            if (a < 0 || !name[(a + 1)..].Equals(action, StringComparison.OrdinalIgnoreCase) || name == skinClipName) continue;
            try
            {
                var data = AnimationClipReader.Read(baseFile, o);
                if (data.Bindings.Any(b => b.IsTransform)) return data;
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return null;
    }
}
