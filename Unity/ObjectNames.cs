namespace UnityBrowser.Unity;

/// <summary>Best-effort extraction of object names and AssetBundle container paths.</summary>
public static class ObjectNames
{
    public static string? GetName(SerializedFile file, ObjectInfo obj)
    {
        try
        {
            var r = file.CreateReader(obj.ByteStart);
            if (obj.Type is { Nodes.Count: > 0 } type)
                return TypeTreeReader.ReadName(type.Nodes, r);

            // No type tree: fall back to known layouts.
            if (ClassIds.IsNamedObject(obj.ClassId))
                return ReadCheckedString(r, obj);

            if (obj.ClassId == ClassIds.GameObject && file.Version >= 14)
            {
                int count = r.I32();
                if (count < 0 || count > 10000) return null;
                r.Skip(count * 12L); // PPtr<Component>
                r.I32();             // m_Layer
                return ReadCheckedString(r, obj);
            }

            if (obj.ClassId == ClassIds.MonoBehaviour && file.Version >= 14)
            {
                r.Skip(12); // m_GameObject
                r.Skip(4);  // m_Enabled + align
                r.Skip(12); // m_Script
                return ReadCheckedString(r, obj);
            }
        }
        catch
        {
            // Unreadable name is not fatal.
        }
        return null;
    }

    private static string? ReadCheckedString(EndianReader r, ObjectInfo obj)
    {
        long limit = obj.ByteStart + obj.ByteSize;
        long start = r.Position;
        int len = r.I32();
        if (len < 0 || start + 4 + len > limit || len > 4096) return null;
        r.Position = start;
        return r.AlignedString(4096);
    }

    /// <summary>Maps PathID to container path using the file's AssetBundle object, if any.</summary>
    public static Dictionary<long, string> GetContainer(SerializedFile file)
    {
        var map = new Dictionary<long, string>();
        var bundleObj = file.Objects.FirstOrDefault(o => o.ClassId == ClassIds.AssetBundle);
        if (bundleObj == null) return map;

        try
        {
            var r = file.CreateReader(bundleObj.ByteStart);
            if (bundleObj.Type is { Nodes.Count: > 0 } type)
            {
                var root = TypeTreeReader.Read(type.Nodes, r);
                var container = root["m_Container"];
                if (container == null) return map;
                foreach (var pair in container.Children)
                {
                    var path = pair["first"]?.Value as string;
                    var asset = pair["second"]?["asset"];
                    if (path == null || asset?["m_PathID"]?.Value is not long pathId) continue;
                    map.TryAdd(pathId, path);
                }
            }
            else if (file.Version >= 14)
            {
                r.AlignedString();                 // m_Name
                int preload = r.I32();
                r.Skip(preload * 12L);             // m_PreloadTable
                int count = r.I32();
                for (int i = 0; i < count; i++)
                {
                    var path = r.AlignedString();
                    r.I32(); r.I32();              // preloadIndex, preloadSize
                    r.I32();                       // m_FileID
                    map.TryAdd(r.I64(), path);
                }
            }
        }
        catch
        {
            // Partial or unreadable container: keep whatever was collected.
        }
        return map;
    }
}
