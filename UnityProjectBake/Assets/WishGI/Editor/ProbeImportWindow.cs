using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 编辑器窗口：导入探针贴图（.npy + meta），并将网格关联写入 uv2。
/// 默认假设 probe_map.npy 由 Offline/baking/pack_probes.py 生成，且同目录存在 meta json。
/// 关联文件需与 export_probes.py 输出格式一致（top_k_vertex，vertices 中包含 probes{id,w}）。
/// </summary>
public class ProbeImportWindow : EditorWindow
{
    private string probeNpyPath = "";
    private string probeMetaPath = "";
    private string meshAssocPath = "";
    private string textureAssetPath = "Assets/WishGI/Resources/ProbeMap.asset";
    private Mesh targetMesh;

    [Serializable]
    private class ProbeMapMeta
    {
        public int order;
        public int num_probes;
        public int texels_per_probe;
        public int width;
        public int height;
    }

    [Serializable]
    private class MeshAssocWrapper { public List<MeshAssocEntry> items; }

    [Serializable]
    private class MeshAssocEntry
    {
        public string mesh_name;
        public int top_k_vertex;
        public int vertex_count;
        public List<VertexAssoc> vertices;
    }

    [Serializable]
    private class VertexAssoc
    {
        public int vertex_id;
        public List<ProbeWeight> probes;
    }

    [Serializable]
    private class ProbeWeight
    {
        public int id;
        public float w;
    }

    [MenuItem("WishGI/Probe Importer")]
    public static void ShowWindow()
    {
        GetWindow<ProbeImportWindow>(true, "WishGI Probe Importer", true);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Probe Map Inputs", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");
        probeNpyPath = FileField("Probe .npy", probeNpyPath);
        probeMetaPath = FileField("Meta .json", probeMetaPath);
        textureAssetPath = EditorGUILayout.TextField("Texture Asset Path", textureAssetPath);
        if (GUILayout.Button("Import Probe Texture"))
        {
            ImportProbeTexture();
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Mesh Assoc", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");
        meshAssocPath = FileField("mesh_assoc.json", meshAssocPath);
        targetMesh = (Mesh)EditorGUILayout.ObjectField("Target Mesh", targetMesh, typeof(Mesh), false);
        if (GUILayout.Button("Apply Mesh Assoc to uv2"))
        {
            ApplyMeshAssoc();
        }
        EditorGUILayout.EndVertical();
    }

    private string FileField(string label, string value)
    {
        EditorGUILayout.BeginHorizontal();
        value = EditorGUILayout.TextField(label, value);
        if (GUILayout.Button("...", GUILayout.MaxWidth(30)))
        {
            string selected = EditorUtility.OpenFilePanel(label, Application.dataPath, "");
            if (!string.IsNullOrEmpty(selected)) value = selected;
        }
        EditorGUILayout.EndHorizontal();
        return value;
    }

    private void ImportProbeTexture()
    {
        if (string.IsNullOrEmpty(probeNpyPath) || string.IsNullOrEmpty(probeMetaPath))
        {
            ShowNotification(new GUIContent("Set .npy and meta paths"));
            return;
        }
        if (!File.Exists(probeNpyPath) || !File.Exists(probeMetaPath))
        {
            ShowNotification(new GUIContent("File not found"));
            return;
        }

        var meta = JsonUtility.FromJson<ProbeMapMeta>(File.ReadAllText(probeMetaPath));
        float[] data = ReadNpyFloat32(probeNpyPath, out int[] shape);
        if (shape.Length != 3 || shape[0] != 1 || shape[1] != meta.width || shape[2] != 4)
        {
            ShowNotification(new GUIContent($"NPY shape mismatch: [{string.Join(",", shape)}]"));
            return;
        }

        Texture2D tex = new Texture2D(meta.width, meta.height, TextureFormat.RGBAFloat, false, true);
        var colors = new Color[meta.width * meta.height];
        int idx = 0;
        for (int y = 0; y < meta.height; y++)
        {
            for (int x = 0; x < meta.width; x++)
            {
                int baseF = (y * meta.width + x) * 4;
                colors[idx++] = new Color(data[baseF + 0], data[baseF + 1], data[baseF + 2], data[baseF + 3]);
            }
        }
        tex.SetPixels(colors);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Point; // 避免跨探针插值混合
        tex.Apply(false, false);

        string assetPath = textureAssetPath;
        if (string.IsNullOrEmpty(assetPath)) assetPath = "Assets/WishGI/Resources/ProbeMap.asset";
        string dir = Path.GetDirectoryName(assetPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        if (existing == null)
            AssetDatabase.CreateAsset(tex, assetPath);
        else
        {
            EditorUtility.CopySerialized(tex, existing);
            AssetDatabase.SaveAssets();
        }

        AssetDatabase.Refresh();
        ShowNotification(new GUIContent($"Probe texture imported -> {assetPath}"));
    }

    private void ApplyMeshAssoc()
    {
        if (targetMesh == null)
        {
            ShowNotification(new GUIContent("Assign Target Mesh"));
            return;
        }
        if (string.IsNullOrEmpty(meshAssocPath) || !File.Exists(meshAssocPath))
        {
            ShowNotification(new GUIContent("Set mesh_assoc.json path"));
            return;
        }
        string raw = File.ReadAllText(meshAssocPath);
        string wrapped = "{\"items\":" + raw + "}"; // 使用 JsonUtility 时不能直接解析顶层数组
        var wrapper = JsonUtility.FromJson<MeshAssocWrapper>(wrapped);
        if (wrapper == null || wrapper.items == null || wrapper.items.Count == 0)
        {
            ShowNotification(new GUIContent("No assoc entries found"));
            return;
        }
        MeshAssocEntry entry = wrapper.items.Find(e => e.mesh_name == targetMesh.name);
        if (entry == null)
        {
            ShowNotification(new GUIContent($"Mesh name not found in assoc: {targetMesh.name}"));
            return;
        }
        int probeCount = InferProbeCountFromMeta();
        if (probeCount <= 1)
        {
            ShowNotification(new GUIContent("Invalid probe count"));
            return;
        }
        var uv2 = new List<Vector4>(targetMesh.vertexCount);
        for (int i = 0; i < targetMesh.vertexCount; i++) uv2.Add(Vector4.zero);

        foreach (var v in entry.vertices)
        {
            if (v.vertex_id < 0 || v.vertex_id >= uv2.Count) continue;
            float i0 = 0, w0 = 0, i1 = 0, w1 = 0;
            if (v.probes != null && v.probes.Count > 0)
            {
                w0 = v.probes[0].w;
                i0 = v.probes[0].id / (float)(probeCount - 1);
                if (v.probes.Count > 1)
                {
                    w1 = v.probes[1].w;
                    i1 = v.probes[1].id / (float)(probeCount - 1);
                }
            }
            uv2[v.vertex_id] = new Vector4(i0, w0, i1, w1);
        }

        targetMesh.SetUVs(1, uv2);
        EditorUtility.SetDirty(targetMesh);
        AssetDatabase.SaveAssets();
        ShowNotification(new GUIContent("Applied mesh assoc to uv2"));
    }

    private int InferProbeCountFromMeta()
    {
        string path = probeMetaPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (!string.IsNullOrEmpty(meshAssocPath))
            {
                path = Path.Combine(Path.GetDirectoryName(meshAssocPath), "probe_map_meta.json");
            }
        }
        
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) 
        {
            Debug.LogError($"[WishGI] Cannot find probe_map_meta.json. Evaluated path: {path}");
            return -1;
        }

        var meta = JsonUtility.FromJson<ProbeMapMeta>(File.ReadAllText(path));
        return meta != null ? meta.num_probes : -1;
    }

    private float[] ReadNpyFloat32(string path, out int[] shape)
    {
        using (var fs = File.OpenRead(path))
        using (var br = new BinaryReader(fs))
        {
            byte[] magic = br.ReadBytes(6); // 用于识别 NPY 文件的魔数字节头（\x93NUMPY）
            if (magic[0] != 0x93 || magic[1] != (byte)'N') throw new Exception("Not an npy file");
            byte vMajor = br.ReadByte();
            byte vMinor = br.ReadByte();
            int headerLen = vMajor == 1 ? br.ReadUInt16() : br.ReadInt32();
            string header = Encoding.ASCII.GetString(br.ReadBytes(headerLen));
            // 简单解析：仅支持小端 float32，且 fortran_order=False
            if (!header.Contains("<f4") || header.Contains("True"))
                throw new Exception("Only supports little-endian float32, fortran_order=False");
            int shapeStart = header.IndexOf('(');
            int shapeEnd = header.IndexOf(')');
            string shapeStr = header.Substring(shapeStart + 1, shapeEnd - shapeStart - 1);
            string[] dims = shapeStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            shape = new int[dims.Length];
            int count = 1;
            for (int i = 0; i < dims.Length; i++)
            {
                shape[i] = int.Parse(dims[i]);
                count *= shape[i];
            }
            byte[] buf = br.ReadBytes(count * sizeof(float));
            float[] data = new float[count];
            Buffer.BlockCopy(buf, 0, data, 0, buf.Length);
            return data;
        }
    }
}
