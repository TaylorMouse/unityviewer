using System.Text;
using UnityBrowser.Unity;

namespace UnityBrowser.Export;

/// <summary>
/// "Export Raw": data exactly as stored, no conversion. An object is written as its serialised bytes;
/// data it keeps outside itself is written next to it unchanged (streamed texture/mesh data as .resS,
/// an AudioClip's FMOD bank as .fsb, a TextAsset's contents as .txt or .bytes).
/// </summary>
public static class RawWriter
{
    public static void WriteObject(string path, SerializedFile sf, ObjectInfo o)
    {
        File.WriteAllBytes(path, sf.Data.AsSpan((int)o.ByteStart, (int)o.ByteSize).ToArray());
        try
        {
            WritePayload(path, sf, o);
        }
        catch
        {
            // The payload is a bonus; the object itself is already written.
        }
    }

    private static void WritePayload(string path, SerializedFile sf, ObjectInfo o)
    {
        switch (o.TypeName)
        {
            case "Texture2D":
            case "Mesh":
            {
                var root = AssetPreview.ReadObject(sf, o);
                var stream = root["m_StreamData"];
                long size = Convert.ToInt64(stream?["size"]?.Value ?? 0u);
                if (size > 0 && sf.Owner != null)
                    File.WriteAllBytes(Path.ChangeExtension(path, ".resS"),
                        sf.Owner.ReadResource(stream!["path"]?.Value as string ?? "", Convert.ToInt64(stream["offset"]?.Value ?? 0ul), size));
                break;
            }
            case "AudioClip":
            {
                var res = AssetPreview.ReadObject(sf, o)["m_Resource"];
                long size = Convert.ToInt64(res?["m_Size"]?.Value ?? 0ul);
                if (size > 0 && sf.Owner != null)
                    File.WriteAllBytes(Path.ChangeExtension(path, ".fsb"),
                        sf.Owner.ReadResource(res!["m_Source"]?.Value as string ?? "", Convert.ToInt64(res["m_Offset"]?.Value ?? 0ul), size));
                break;
            }
            case "TextAsset":
            {
                // Read m_Script's bytes directly: decoding it as a string would damage binary TextAssets.
                var r = sf.CreateReader(o.ByteStart);
                r.AlignedString();            // m_Name
                int length = r.I32();         // m_Script
                var bytes = r.Bytes(length);
                bool text = !bytes.Contains((byte)0) && IsUtf8(bytes);
                File.WriteAllBytes(Path.ChangeExtension(path, text ? ".txt" : ".script.bytes"), bytes);
                break;
            }
        }
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a whole file's contents: every bundle entry (CAB-xxxx, .resS, ...) as stored inside the bundle,
    /// or the file itself when it is a plain serialised file. Returns the number of files written.
    /// </summary>
    public static int WriteFile(LoadedFile file, string folder)
    {
        Directory.CreateDirectory(folder);
        if (file.Bundle is { } bundle)
        {
            foreach (var node in bundle.Nodes) WriteEntry(bundle, node, folder);
            return bundle.Nodes.Count;
        }
        File.Copy(file.Path, Path.Combine(folder, Path.GetFileName(file.Path)), overwrite: true);
        return 1;
    }

    public static void WriteEntry(BundleFile bundle, BundleNode node, string folder)
    {
        string target = Path.Combine(folder, node.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var fs = File.Create(target);
        fs.Write(bundle.Data, (int)node.Offset, (int)node.Size);
    }
}
