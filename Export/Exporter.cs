using UnityBrowser.Audio;
using UnityBrowser.Unity;

namespace UnityBrowser.Export;

public enum ExportFormat { Png, Dds, Wav, Obj, Dae, Raw }

public sealed class ExportResult
{
    public int Exported { get; set; }
    public List<string> Failures { get; } = new();
    /// <summary>Objects deliberately not exported (e.g. prop animation clips).</summary>
    public List<string> Skipped { get; } = new();

    public void Add(ExportResult other)
    {
        Exported += other.Exported;
        Failures.AddRange(other.Failures);
        Skipped.AddRange(other.Skipped);
    }
}

/// <summary>
/// Writes textures and sprites (PNG/DDS), audio clips (WAV) or meshes (OBJ/DAE) to disk. Files go into a
/// subfolder per type (Texture2D, Sprite, AudioClip, Mesh), because a sprite often shares its name with its texture.
/// </summary>
public sealed class Exporter
{
    private readonly string _folder;
    private readonly ExportFormat _format;
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

    public Exporter(string folder, ExportFormat format)
    {
        _folder = folder;
        _format = format;
    }

    public ExportResult Result { get; } = new();

    public static bool CanExport(ObjectInfo o, ExportFormat format) => format switch
    {
        ExportFormat.Raw => true,
        ExportFormat.Wav => AudioClipDecoder.CanDecode(o),
        ExportFormat.Obj => MeshReader.CanRead(o),
        ExportFormat.Dae => MeshReader.CanRead(o) || AnimationClipReader.CanRead(o),
        _ => AssetPreview.CanPreview(o),
    };

    public void Export(SerializedFile sf, ObjectInfo o)
    {
        string name = ObjectNames.GetName(sf, o) ?? "";
        string? path = null;
        try
        {
            path = UniquePath(o.TypeName, name, o.PathId);
            if (_format == ExportFormat.Raw)
            {
                RawWriter.WriteObject(path, sf, o);
            }
            else if (_format == ExportFormat.Wav)
            {
                WavWriter.Write(path, AudioClipDecoder.Decode(sf, o));
            }
            else if (_format == ExportFormat.Obj)
            {
                ObjWriter.Write(path, MeshReader.Read(sf, o));
            }
            else if (_format == ExportFormat.Dae && AnimationClipReader.CanRead(o))
            {
                AnimationDaeWriter.Write(path, sf, o);
            }
            else if (_format == ExportFormat.Dae)
            {
                var mesh = MeshReader.Read(sf, o);
                DaeWriter.Write(path, mesh, SkeletonReader.Read(sf, o, mesh));
            }
            else if (_format == ExportFormat.Dds && o.TypeName == "Texture2D")
            {
                DdsWriter.WriteTexture(path, AssetPreview.ReadTextureData(sf, o));
            }
            else
            {
                var image = AssetPreview.GetImage(sf, o);
                if (_format == ExportFormat.Png) PngWriter.Write(path, image.Width, image.Height, image.Bgra);
                else DdsWriter.WriteBgra(path, image.Width, image.Height, image.Bgra);
            }
            Result.Exported++;
        }
        catch (NotSupportedException ex) when (o.TypeName == "AnimationClip")
        {
            CleanUp(path);
            Result.Skipped.Add($"{o.TypeName} {(name.Length > 0 ? name : "#" + o.PathId)}: {ex.Message}");
        }
        catch (Exception ex)
        {
            CleanUp(path);
            Result.Failures.Add($"{o.TypeName} {(name.Length > 0 ? name : "#" + o.PathId)}: {ex.Message}");
        }
    }

    /// <summary>After a failure: remove a partly written file, and its type folder if nothing else is in it.</summary>
    private static void CleanUp(string? path)
    {
        if (path == null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch
        {
            // Best effort.
        }
    }

    private string UniquePath(string typeName, string name, long pathId)
    {
        string dir = Path.Combine(_folder, typeName);
        Directory.CreateDirectory(dir);

        string safe = Sanitise(name);
        if (safe.Length == 0) safe = pathId.ToString();
        string ext = _format == ExportFormat.Raw ? ".bytes" : "." + _format.ToString().ToLowerInvariant();

        // Same name twice in one export (e.g. from different bundles): add the path ID.
        string path = Path.Combine(dir, safe + ext);
        if (!_written.Add(path))
        {
            path = Path.Combine(dir, $"{safe}_{pathId}{ext}");
            _written.Add(path);
        }
        return path;
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }
}
