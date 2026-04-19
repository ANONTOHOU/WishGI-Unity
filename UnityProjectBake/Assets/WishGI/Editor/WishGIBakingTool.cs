using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;

public class WishGIBakingTool : EditorWindow
{
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

    public enum QualityPreset
    {
        Low,       // 64 个探针，128 个方向，256 个采样点
        High       // 128 个探针，960 个方向，1024 个采样点
    }

    private string meshJsonPath = "Data/meshs/SampleScene_mesh.json";
    private string sceneJsonPath = "Data/scenes/SampleScene_lights.json";
    private string pythonPath = "python";
    private string lastBakeOutputDir = "";
    private QualityPreset qualityPreset = QualityPreset.Low;

    [MenuItem("GI/Baking Tool")]
    public static void ShowWindow()
    {
        GetWindow<WishGIBakingTool>("GI Baking Tool");
    }

    /// <summary>
    /// 绘制烘焙工具窗口，分为“离线计算”和“回填 Unity”两步。
    /// </summary>
    private void OnGUI()
    {
        GUILayout.Label("GI Offline Baking (Python Pipeline)", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        string workspaceRoot = GetWorkspaceRoot();

        // 网格 JSON 输入
        GUILayout.BeginHorizontal();
        meshJsonPath = EditorGUILayout.TextField("Mesh JSON", meshJsonPath);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string path = EditorUtility.OpenFilePanel("Select Mesh JSON", Path.Combine(workspaceRoot, "Data", "meshs"), "json");
            if (!string.IsNullOrEmpty(path)) meshJsonPath = GetWorkspaceRelativePath(path, workspaceRoot);
        }
        GUILayout.EndHorizontal();

        // 场景灯光 JSON 输入
        GUILayout.BeginHorizontal();
        sceneJsonPath = EditorGUILayout.TextField("Scene Lights JSON", sceneJsonPath);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string path = EditorUtility.OpenFilePanel("Select Scene Lights JSON", Path.Combine(workspaceRoot, "Data", "scenes"), "json");
            if (!string.IsNullOrEmpty(path)) sceneJsonPath = GetWorkspaceRelativePath(path, workspaceRoot);
        }
        GUILayout.EndHorizontal();

        EditorGUILayout.Space();
        qualityPreset = (QualityPreset)EditorGUILayout.EnumPopup("Quality Preset", qualityPreset);

        EditorGUILayout.Space();
        pythonPath = EditorGUILayout.TextField("Python Command", pythonPath);
        EditorGUILayout.HelpBox("High: 128 probes, 960 directions\nLow: 64 probes, 128 directions.\n输出自动根据场景-日期-次数命名，目录在 Data/probes/.", MessageType.Info);

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("1. Run Python Baking!", GUILayout.Height(40)))
        {
            RunBakingPipeline();
        }
        
        EditorGUILayout.Space();
        if (!string.IsNullOrEmpty(lastBakeOutputDir))
        {
            GUI.color = Color.green;
            if (GUILayout.Button("2. Import Texture & Apply UV2", GUILayout.Height(30)))
            {
                ApplyBakeDataToUnity(lastBakeOutputDir);
            }
            GUI.color = Color.white;
            EditorGUILayout.HelpBox($"Will apply data from:\n{lastBakeOutputDir}", MessageType.Info);
        }
    }

    /// <summary>
    /// 计算工作区根目录。
    /// </summary>
    private string GetWorkspaceRoot()
    {
        // 向上两层，返回工作区根目录 D:\Programs\unity\WishGI-Unity
        return Path.GetFullPath(Path.Combine(Application.dataPath, "../..")).Replace('\\', '/');
    }

    /// <summary>
    /// 将绝对路径转为工作区相对路径，便于传给 Python 脚本。
    /// </summary>
    private string GetWorkspaceRelativePath(string absPath, string workspaceRoot)
    {
        absPath = absPath.Replace('\\', '/');
        if (absPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return absPath.Substring(workspaceRoot.Length).TrimStart('/');
        }
        return absPath; // 若位于工作区之外，则返回绝对路径
    }

    /// <summary>
    /// 执行离线烘焙四步流水线。
    /// </summary>
    private void RunBakingPipeline()
    {
        string workspaceRoot = GetWorkspaceRoot();
        string absMeshPath = Path.Combine(workspaceRoot, meshJsonPath);
        string absScenePath = Path.Combine(workspaceRoot, sceneJsonPath);

        if (!File.Exists(absMeshPath) || !File.Exists(absScenePath))
        {
            UnityEngine.Debug.LogError("[GI] Input files not found. Please check paths.");
            return;
        }

        // 根据预设配置参数。
        // 这些值直接对应“速度/质量”权衡：
        // - probes: 探针容量
        // - dirs: 每采样点方向分辨率
        // - samples: 表面采样密度上限
        int probes = 64;
        int dirs = 128;
        int samples = 256;
        
        if (qualityPreset == QualityPreset.High)
        {
            probes = 128;
            dirs = 960;
            samples = 1024;
        }

        // 自动命名（Scene_Date_Count）
        string sceneName = Path.GetFileNameWithoutExtension(meshJsonPath).Replace("_mesh", "");
        string dateStr = DateTime.Now.ToString("yyyyMMdd");
        string probesDirBase = Path.Combine(workspaceRoot, "Data", "probes").Replace('\\', '/');
        string samplesDirBase = Path.Combine(workspaceRoot, "Data", "samples").Replace('\\', '/');

        if (!Directory.Exists(probesDirBase)) Directory.CreateDirectory(probesDirBase);
        if (!Directory.Exists(samplesDirBase)) Directory.CreateDirectory(samplesDirBase);

        // 扫描已有目录，计算当日序号
        int count = 1;
        string baseName = $"{sceneName}-{dateStr}-{count:D2}";
        string outputFolder = Path.Combine(probesDirBase, baseName).Replace('\\', '/');
        
        while (Directory.Exists(outputFolder))
        {
            count++;
            baseName = $"{sceneName}-{dateStr}-{count:D2}";
            outputFolder = Path.Combine(probesDirBase, baseName).Replace('\\', '/');
        }

        Directory.CreateDirectory(outputFolder);

        // 设置相对于工作区工作目录的路径
        string samplesOut = $"Data/samples/{baseName}_samples_pt.json";
        string dirsOut = $"Data/samples/{baseName}_dirs.npy";
        string outDir = $"Data/probes/{baseName}";

        try
        {
            EditorUtility.DisplayProgressBar("GI Baking Pipeline", "1/4 Surface Sampling & Raytracing...", 0.2f);
            // min-dist 固定为 0.05 作为当前项目经验值：密度足够，且耗时可控。
            RunPython(workspaceRoot, "Offline/sampling/sample_surface.py", 
                $"--mesh-json \"{meshJsonPath}\" --scene-json \"{sceneJsonPath}\" --output \"{samplesOut}\" --min-dist 0.05 --num-samples {samples} --directions {dirs} --bounces 3 --albedo 0.8 --seed 42 --dirs-out \"{dirsOut}\"");

            EditorUtility.DisplayProgressBar("GI Baking Pipeline", "2/4 Probe Clustering & Weights...", 0.45f);
            // top-k-sample=4 提高拟合稳定性；top-k-vertex=2 控制运行时顶点开销。
            RunPython(workspaceRoot, "Offline/export/export_probes.py", 
                $"--samples-json \"{samplesOut}\" --mesh-json \"{meshJsonPath}\" --probes {probes} --top-k-sample 4 --top-k-vertex 2 --output-dir \"{outDir}\"");

            EditorUtility.DisplayProgressBar("GI Baking Pipeline", "3/4 Solving SH Coefficients...", 0.7f);
            // lambda-reg=0.1 与论文默认一致，避免过拟合并增强数值稳定性。
            RunPython(workspaceRoot, "Offline/baking/fit_sh.py", 
                $"--samples-json \"{samplesOut}\" --sample-weights \"{outDir}/sample_weights.json\" --order 2 --lambda-reg 0.1 --output-npy \"{outDir}/probes_sh.npy\" --output-json \"{outDir}/probes_sh.json\" --dirs-npy \"{dirsOut}\"");

            EditorUtility.DisplayProgressBar("GI Baking Pipeline", "4/4 Packing Probes to Texture...", 0.9f);
            RunPython(workspaceRoot, "Offline/baking/pack_probes.py", 
                $"--probes-npy \"{outDir}/probes_sh.npy\" --order 2 --output-tex \"{outDir}/probe_map.npy\" --output-meta \"{outDir}/probe_map_meta.json\"");

            lastBakeOutputDir = Path.Combine(workspaceRoot, outDir).Replace('\\', '/');

            UnityEngine.Debug.Log($"<color=#00FF00><b>[GI] Baking successful!</b></color>\nOutputs saved in: {outDir}");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[GI] Baking failed in pipeline.\n{e.Message}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// 运行单个 Python 脚本，并在失败时抛出详细错误。
    /// </summary>
    private void RunPython(string workingDir, string scriptPath, string args)
    {
        string arguments = $"\"{scriptPath}\" {args}";
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Execution failed: {scriptPath}\nError:\n{error}\nOutput:\n{output}");
            }
        }
    }

    /// <summary>
    /// 将离线产物导入 Unity：生成探针纹理并把关联写入场景网格 uv2。
    /// </summary>
    private void ApplyBakeDataToUnity(string dataDir)
    {
        string npyPath = Path.Combine(dataDir, "probe_map.npy");
        string metaPath = Path.Combine(dataDir, "probe_map_meta.json");
        string assocPath = Path.Combine(dataDir, "mesh_assoc.json");
        string targetAssetPath = $"Assets/WishGI/Resources/{new DirectoryInfo(dataDir).Name}_ProbeMap.asset";

        if (!File.Exists(npyPath) || !File.Exists(metaPath) || !File.Exists(assocPath))
        {
            UnityEngine.Debug.LogError("[GI] Missing .npy, _meta.json, or mesh_assoc.json in the output directory!");
            return;
        }

        try
        {
            EditorUtility.DisplayProgressBar("GI Apply", "Importing Texture...", 0.3f);
            int probeCount = ImportProbeTexture(npyPath, metaPath, targetAssetPath);

            if (probeCount > 0)
            {
                EditorUtility.DisplayProgressBar("GI Apply", "Applying UV2 to Meshes...", 0.6f);
                ApplyMeshAssocToAll(assocPath, probeCount);
                UnityEngine.Debug.Log($"<color=#00FF00><b>[GI] Apply to Unity successful!</b></color>\nTexture built at: {targetAssetPath}\nuv2 injected into meshes.");
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[GI] Failed to apply data to Unity:\n{ex.Message}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// 导入 probe_map.npy 为 Texture2D 资产，并返回探针数量。
    /// </summary>
    private int ImportProbeTexture(string npy, string metaStr, string assetPath)
    {
        var meta = JsonUtility.FromJson<ProbeMapMeta>(File.ReadAllText(metaStr));
        float[] data = ReadNpyFloat32(npy, out int[] shape);
        if (shape.Length != 3 || shape[0] != 1 || shape[1] != meta.width || shape[2] != 4)
            throw new Exception($"NPY shape mismatch: [{string.Join(",", shape)}]");

        // RGBAFloat 与离线 float32 打包格式完全对应，避免量化误差。
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
        tex.filterMode = FilterMode.Point;
        tex.Apply(false, false);

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
        return meta.num_probes;
    }

    /// <summary>
    /// 将 mesh_assoc.json 关联应用到场景中所有匹配名称的 Mesh。
    /// </summary>
    private void ApplyMeshAssocToAll(string assocPath, int probeCount)
    {
        string raw = File.ReadAllText(assocPath);
        string wrapped = "{\"items\":" + raw + "}"; 
        var wrapper = JsonUtility.FromJson<MeshAssocWrapper>(wrapped);

        if (wrapper == null || wrapper.items == null || wrapper.items.Count == 0)
            throw new Exception("No assoc entries found in mesh_assoc.json");

        // 扫描场景中所有 MeshFilter
        var filters = UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var goMap = new Dictionary<string, Mesh>();
        foreach (var mf in filters)
        {
            if (mf.sharedMesh != null && !goMap.ContainsKey(mf.gameObject.name))
            {
                // 以 GameObject 名称映射到 sharedMesh，因为导出器使用的是 go.name
                goMap[mf.gameObject.name] = mf.sharedMesh;
            }
        }

        int successCount = 0;

        foreach (var entry in wrapper.items)
        {
            if (goMap.TryGetValue(entry.mesh_name, out Mesh targetMesh))
            {
                var uv2 = new List<Vector4>(targetMesh.vertexCount);
                for (int i = 0; i < targetMesh.vertexCount; i++) uv2.Add(Vector4.zero);

                foreach (var v in entry.vertices)
                {
                    if (v.vertex_id < 0 || v.vertex_id >= uv2.Count) continue;
                    float i0 = 0, w0 = 0, i1 = 0, w1 = 0;
                    if (v.probes != null && v.probes.Count > 0)
                    {
                        w0 = v.probes[0].w;
                        // 将探针索引归一化写入 uv2，运行时按 probeCount 反解。
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
                successCount++;
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[GI] Could not find scene mesh mapped to name: {entry.mesh_name}");
            }
        }

        AssetDatabase.SaveAssets();
        UnityEngine.Debug.Log($"[GI] UV2 updated for {successCount} meshes.");
    }

    /// <summary>
    /// 读取 .npy（float32）为展平数组。
    /// </summary>
    private float[] ReadNpyFloat32(string path, out int[] shape)
    {
        using (var fs = File.OpenRead(path))
        using (var br = new BinaryReader(fs))
        {
            byte[] magic = br.ReadBytes(6); 
            if (magic[0] != 0x93 || magic[1] != (byte)'N') throw new Exception("Not an npy file");
            byte vMajor = br.ReadByte();
            byte vMinor = br.ReadByte();
            int headerLen = vMajor == 1 ? br.ReadUInt16() : br.ReadInt32();
            string header = Encoding.ASCII.GetString(br.ReadBytes(headerLen));
            
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
