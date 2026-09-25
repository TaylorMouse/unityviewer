using System.Collections.Concurrent;

namespace UnityBrowser.Unity;

/// <summary>
/// The files open in the browser, by the internal name of their serialised files (CAB-xxxx).
/// Bundles refer to each other only by that name, so this is how a dependency is found.
/// </summary>
public static class FileRegistry
{
    private static readonly ConcurrentDictionary<string, SerializedFile> ByName = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(LoadedFile file)
    {
        foreach (var sf in file.SerializedFiles) ByName[sf.Name] = sf;
    }

    public static void Clear() => ByName.Clear();

    public static bool IsOpen(string cabName) => ByName.ContainsKey(cabName);

    /// <summary>The internal name an external reference points to: "archive:/CAB-x/CAB-x" gives "CAB-x".</summary>
    public static string ExternalName(FileIdentifier external) =>
        external.PathName.Replace('\\', '/').Split('/').Last();

    /// <summary>
    /// Resolves a PPtr file ID: 0 is the file itself; others index its externals table (1-based) and are
    /// looked up among the same bundle's files and then among all open files. Null when not open.
    /// </summary>
    public static SerializedFile? Resolve(SerializedFile sf, int fileId)
    {
        if (fileId == 0) return sf;
        if (fileId < 0 || fileId > sf.Externals.Count) return null;
        string name = ExternalName(sf.Externals[fileId - 1]);
        return sf.Owner?.SerializedFiles.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? ByName.GetValueOrDefault(name);
    }
}
