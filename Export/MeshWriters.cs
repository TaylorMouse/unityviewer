using System.Globalization;
using System.Security;
using System.Text;
using UnityBrowser.Unity;

namespace UnityBrowser.Export;

/// <summary>Wavefront OBJ writer (positions, UV0, normals, one group per sub-mesh).</summary>
public static class ObjWriter
{
    public static void Write(string path, MeshData m)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Exported by Unity Browser");
        sb.AppendLine($"# {m.VertexCount} vertices, {m.TriangleCount} triangles, {m.SubMeshes.Count} sub-meshes");
        sb.AppendLine($"o {Clean(m.Name)}");

        for (int i = 0; i < m.VertexCount; i++)
            sb.Append("v ").Append(F(m.Positions[i * 3])).Append(' ').Append(F(m.Positions[i * 3 + 1])).Append(' ')
              .AppendLine(F(m.Positions[i * 3 + 2]));
        if (m.Uv0 != null)
            for (int i = 0; i < m.VertexCount; i++)
                sb.Append("vt ").Append(F(m.Uv0[i * 2])).Append(' ').AppendLine(F(m.Uv0[i * 2 + 1]));
        if (m.Normals != null)
            for (int i = 0; i < m.VertexCount; i++)
                sb.Append("vn ").Append(F(m.Normals[i * 3])).Append(' ').Append(F(m.Normals[i * 3 + 1])).Append(' ')
                  .AppendLine(F(m.Normals[i * 3 + 2]));

        for (int s = 0; s < m.SubMeshes.Count; s++)
        {
            sb.AppendLine($"g {Clean(m.Name)}_{s}");
            var t = m.SubMeshes[s].Triangles;
            for (int i = 0; i < t.Length; i += 3)
                sb.Append("f ").Append(Corner(t[i], m)).Append(' ').Append(Corner(t[i + 1], m)).Append(' ')
                  .AppendLine(Corner(t[i + 2], m));
        }
        File.WriteAllText(path, sb.ToString());

        string F(float v) => v.ToString("0.######", ci);
    }

    private static string Corner(int index, MeshData m)
    {
        int n = index + 1;
        bool uv = m.Uv0 != null, normal = m.Normals != null;
        return uv && normal ? $"{n}/{n}/{n}" : uv ? $"{n}/{n}" : normal ? $"{n}//{n}" : n.ToString();
    }

    private static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "mesh" : name.Replace(' ', '_');
}

/// <summary>
/// COLLADA 1.4.1 writer: positions, normals, UV0, one material slot per sub-mesh and, for skinned meshes,
/// the joint hierarchy (in bind pose) plus a skin controller with inverse bind matrices and vertex weights.
/// The Append* building blocks are shared with <see cref="AnimationDaeWriter"/>.
/// </summary>
public static class DaeWriter
{
    internal static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static void Write(string path, MeshData m, Skeleton? skeleton = null)
    {
        string id = MakeId(m.Name);
        bool skinned = skeleton != null && m.IsSkinned && m.BoneIndices != null && m.BoneWeights != null;

        var sb = new StringBuilder();
        AppendHeader(sb);
        AppendEffects(sb);
        sb.AppendLine("  <library_materials>");
        AppendMaterials(sb, id, m);
        sb.AppendLine("  </library_materials>");
        sb.AppendLine("  <library_geometries>");
        AppendGeometry(sb, id, m);
        sb.AppendLine("  </library_geometries>");
        if (skinned)
        {
            sb.AppendLine("  <library_controllers>");
            AppendController(sb, id, m, Enumerable.Range(0, m.BindPoses.Count).Select(s => skeleton!.Nodes[skeleton.SlotToNode[s]].Sid).ToArray());
            sb.AppendLine("  </library_controllers>");
        }

        sb.AppendLine("  <library_visual_scenes>");
        sb.AppendLine("    <visual_scene id=\"scene\" name=\"scene\">");
        var roots = new List<string>();
        if (skinned)
            for (int i = 0; i < skeleton!.Nodes.Count; i++)
                if (skeleton.Nodes[i].Parent < 0)
                {
                    WriteJoint(sb, id, skeleton, i, 3);
                    roots.Add($"{id}-{skeleton.Nodes[i].Sid}");
                }
        AppendMeshInstance(sb, id, m, skinned, roots, 3);
        sb.AppendLine("    </visual_scene>");
        sb.AppendLine("  </library_visual_scenes>");
        AppendFooter(sb);
        File.WriteAllText(path, sb.ToString());
    }

    // ---- Shared building blocks ----

    internal static void AppendHeader(StringBuilder sb)
    {
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">");
        sb.AppendLine("  <asset>");
        sb.AppendLine("    <contributor><authoring_tool>Unity Browser</authoring_tool></contributor>");
        sb.AppendLine($"    <created>{DateTime.UtcNow:s}</created><modified>{DateTime.UtcNow:s}</modified>");
        sb.AppendLine("    <unit name=\"meter\" meter=\"1\"/>");
        sb.AppendLine("    <up_axis>Y_UP</up_axis>");
        sb.AppendLine("  </asset>");
    }

    internal static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine("  <scene><instance_visual_scene url=\"#scene\"/></scene>");
        sb.AppendLine("</COLLADA>");
    }

    /// <summary>One grey Lambert effect; each sub-mesh gets its own material slot so it can be reassigned.</summary>
    internal static void AppendEffects(StringBuilder sb)
    {
        sb.AppendLine("  <library_effects>");
        sb.AppendLine("    <effect id=\"default-effect\"><profile_COMMON><technique sid=\"common\"><lambert>");
        sb.AppendLine("      <diffuse><color>0.8 0.8 0.8 1</color></diffuse>");
        sb.AppendLine("    </lambert></technique></profile_COMMON></effect>");
        sb.AppendLine("  </library_effects>");
    }

    internal static void AppendMaterials(StringBuilder sb, string id, MeshData m)
    {
        for (int s = 0; s < m.SubMeshes.Count; s++)
            sb.AppendLine($"    <material id=\"{id}-mat{s}\" name=\"{Esc(m.Name)}_{s}\"><instance_effect url=\"#default-effect\"/></material>");
    }

    internal static void AppendGeometry(StringBuilder sb, string id, MeshData m)
    {
        string Floats(float[] a) => string.Join(" ", a.Select(v => v.ToString("0.######", Ci)));
        sb.AppendLine($"    <geometry id=\"{id}-mesh\" name=\"{Esc(m.Name)}\"><mesh>");
        WriteSource(sb, $"{id}-positions", m.Positions, m.VertexCount, new[] { "X", "Y", "Z" }, Floats);
        if (m.Normals != null) WriteSource(sb, $"{id}-normals", m.Normals, m.VertexCount, new[] { "X", "Y", "Z" }, Floats);
        if (m.Uv0 != null) WriteSource(sb, $"{id}-uv0", m.Uv0, m.VertexCount, new[] { "S", "T" }, Floats);
        sb.AppendLine($"      <vertices id=\"{id}-vertices\"><input semantic=\"POSITION\" source=\"#{id}-positions\"/></vertices>");
        for (int s = 0; s < m.SubMeshes.Count; s++)
        {
            var t = m.SubMeshes[s].Triangles;
            if (t.Length == 0) continue;
            sb.AppendLine($"      <triangles material=\"mat{s}\" count=\"{t.Length / 3}\">");
            sb.AppendLine($"        <input semantic=\"VERTEX\" source=\"#{id}-vertices\" offset=\"0\"/>");
            if (m.Normals != null) sb.AppendLine($"        <input semantic=\"NORMAL\" source=\"#{id}-normals\" offset=\"0\"/>");
            if (m.Uv0 != null) sb.AppendLine($"        <input semantic=\"TEXCOORD\" source=\"#{id}-uv0\" offset=\"0\" set=\"0\"/>");
            sb.Append("        <p>").Append(string.Join(" ", t)).AppendLine("</p>");
            sb.AppendLine("      </triangles>");
        }
        sb.AppendLine("    </mesh></geometry>");
    }

    /// <summary>Skin controller: joints (by sid, one per bone slot), inverse bind matrices, and per-vertex weights.</summary>
    internal static void AppendController(StringBuilder sb, string id, MeshData m, string[] slotSids)
    {
        int slots = m.BindPoses.Count;
        var weights = new List<float>();
        var vcount = new StringBuilder();
        var v = new StringBuilder();
        for (int i = 0; i < m.VertexCount; i++)
        {
            int n = 0;
            for (int k = 0; k < 4; k++)
            {
                float w = m.BoneWeights![i * 4 + k];
                int slot = m.BoneIndices![i * 4 + k];
                if (w <= 0 || slot < 0 || slot >= slots) continue;
                v.Append(slot).Append(' ').Append(weights.Count).Append(' ');
                weights.Add(w);
                n++;
            }
            vcount.Append(n).Append(' ');
        }

        sb.AppendLine($"    <controller id=\"{id}-skin\" name=\"{Esc(m.Name)}_skin\"><skin source=\"#{id}-mesh\">");
        sb.AppendLine($"      <bind_shape_matrix>{Matrix(Mat4.Identity())}</bind_shape_matrix>");

        sb.AppendLine($"      <source id=\"{id}-joints\">");
        sb.Append($"        <Name_array id=\"{id}-joints-array\" count=\"{slots}\">").Append(string.Join(" ", slotSids)).AppendLine("</Name_array>");
        sb.AppendLine($"        <technique_common><accessor source=\"#{id}-joints-array\" count=\"{slots}\" stride=\"1\"><param name=\"JOINT\" type=\"name\"/></accessor></technique_common>");
        sb.AppendLine("      </source>");

        sb.AppendLine($"      <source id=\"{id}-bind-poses\">");
        sb.Append($"        <float_array id=\"{id}-bind-poses-array\" count=\"{slots * 16}\">");
        sb.Append(string.Join(" ", m.BindPoses.Select(Matrix))).AppendLine("</float_array>");
        sb.AppendLine($"        <technique_common><accessor source=\"#{id}-bind-poses-array\" count=\"{slots}\" stride=\"16\"><param name=\"TRANSFORM\" type=\"float4x4\"/></accessor></technique_common>");
        sb.AppendLine("      </source>");

        sb.AppendLine($"      <source id=\"{id}-weights\">");
        sb.Append($"        <float_array id=\"{id}-weights-array\" count=\"{weights.Count}\">");
        sb.Append(string.Join(" ", weights.Select(w => w.ToString("0.######", Ci)))).AppendLine("</float_array>");
        sb.AppendLine($"        <technique_common><accessor source=\"#{id}-weights-array\" count=\"{weights.Count}\" stride=\"1\"><param name=\"WEIGHT\" type=\"float\"/></accessor></technique_common>");
        sb.AppendLine("      </source>");

        sb.AppendLine($"      <joints><input semantic=\"JOINT\" source=\"#{id}-joints\"/><input semantic=\"INV_BIND_MATRIX\" source=\"#{id}-bind-poses\"/></joints>");
        sb.AppendLine($"      <vertex_weights count=\"{m.VertexCount}\">");
        sb.AppendLine($"        <input semantic=\"JOINT\" source=\"#{id}-joints\" offset=\"0\"/>");
        sb.AppendLine($"        <input semantic=\"WEIGHT\" source=\"#{id}-weights\" offset=\"1\"/>");
        sb.Append("        <vcount>").Append(vcount.ToString().TrimEnd()).AppendLine("</vcount>");
        sb.Append("        <v>").Append(v.ToString().TrimEnd()).AppendLine("</v>");
        sb.AppendLine("      </vertex_weights>");
        sb.AppendLine("    </skin></controller>");
    }

    /// <summary>A scene node holding the mesh, bound either directly or through its skin controller.</summary>
    internal static void AppendMeshInstance(StringBuilder sb, string id, MeshData m, bool skinned, IEnumerable<string> skeletonRootIds, int depth)
    {
        string pad = new(' ', depth * 2);
        sb.AppendLine($"{pad}<node id=\"{id}-node\" name=\"{Esc(m.Name)}\" type=\"NODE\">");
        if (skinned)
        {
            sb.AppendLine($"{pad}  <instance_controller url=\"#{id}-skin\">");
            foreach (var root in skeletonRootIds) sb.AppendLine($"{pad}    <skeleton>#{root}</skeleton>");
        }
        else
        {
            sb.AppendLine($"{pad}  <instance_geometry url=\"#{id}-mesh\">");
        }
        sb.AppendLine($"{pad}    <bind_material><technique_common>");
        for (int s = 0; s < m.SubMeshes.Count; s++)
            sb.AppendLine($"{pad}      <instance_material symbol=\"mat{s}\" target=\"#{id}-mat{s}\"/>");
        sb.AppendLine($"{pad}    </technique_common></bind_material>");
        sb.AppendLine(skinned ? $"{pad}  </instance_controller>" : $"{pad}  </instance_geometry>");
        sb.AppendLine($"{pad}</node>");
    }

    internal static string Matrix(double[] m) => string.Join(" ", m.Select(v => v.ToString("0.#########", Ci)));

    private static void WriteJoint(StringBuilder sb, string id, Skeleton sk, int node, int depth)
    {
        var n = sk.Nodes[node];
        string pad = new(' ', depth * 2);
        sb.AppendLine($"{pad}<node id=\"{id}-{n.Sid}\" name=\"{Esc(n.Name)}\" sid=\"{n.Sid}\" type=\"JOINT\">");
        sb.AppendLine($"{pad}  <matrix sid=\"transform\">{Matrix(n.Local)}</matrix>");
        for (int c = 0; c < sk.Nodes.Count; c++)
            if (sk.Nodes[c].Parent == node) WriteJoint(sb, id, sk, c, depth + 1);
        sb.AppendLine($"{pad}</node>");
    }

    private static void WriteSource(StringBuilder sb, string id, float[] data, int count, string[] axes, Func<float[], string> floats)
    {
        sb.AppendLine($"      <source id=\"{id}\">");
        sb.AppendLine($"        <float_array id=\"{id}-array\" count=\"{data.Length}\">{floats(data)}</float_array>");
        sb.AppendLine($"        <technique_common><accessor source=\"#{id}-array\" count=\"{count}\" stride=\"{axes.Length}\">");
        foreach (var a in axes) sb.AppendLine($"          <param name=\"{a}\" type=\"float\"/>");
        sb.AppendLine("        </accessor></technique_common>");
        sb.AppendLine("      </source>");
    }

    internal static string MakeId(string name)
    {
        var chars = (string.IsNullOrWhiteSpace(name) ? "mesh" : name).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        string id = new(chars);
        return char.IsLetter(id[0]) || id[0] == '_' ? id : "_" + id;
    }

    internal static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
