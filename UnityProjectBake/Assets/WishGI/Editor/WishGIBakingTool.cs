using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using WishGI.Baking.Editor;
using WishGI.Editor;

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
        High,      // 128 个探针，512 个方向，512 个采样点
        Custom
    }

    private struct TimeEstimate
    {
        public float step1Sec;
        public float step2Sec;
        public float step3Sec;
        public float step4Sec;
        public float totalSec;
    }

    private string meshJsonPath = "Data/meshs/SampleScene_mesh.json";
    private string sceneJsonPath = "Data/scenes/SampleScene_lights.json";
    private string pythonPath = "python";
    private string lastBakeOutputDir = "";
    private QualityPreset qualityPreset = QualityPreset.Low;

    private int customProbes = 96;
    private int customDirections = 256;
    private int customSamples = 384;

    private int seed = 42;
    private float minDist = 0.05f;
    private int bounces = 3;
    private float defaultAlbedo = 0.8f;
    private int topKSample = 4;
    private int topKVertex = 2;
    private int shOrder = 2;
    private float lambdaReg = 0.1f;

    private bool includeStep0GenerateUV2 = true;
    private bool includeStep1ExportLights = true;
    private bool includeStep2ExportMesh = true;
    private bool autoApplyAfterBake = true;
    private bool autoAssignProbeMapToMaterials = true;
    private string lastPreflightMessage = "";
    private Vector2 scrollPos;
    private bool isIntegratedRunInProgress;

    private const string TimeCalibrationKey = "WishGI.BakeTimeCalibration";

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
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        GUILayout.Label("离线烘焙（python 管线）", EditorStyles.boldLabel);
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
        qualityPreset = (QualityPreset)EditorGUILayout.EnumPopup("Quality Settings", qualityPreset);

        int probes;
        int dirs;
        int samples;
        ResolveEffectiveQuality(out probes, out dirs, out samples);

        if (qualityPreset == QualityPreset.Custom)
        {
            customProbes = EditorGUILayout.IntField("Probes", customProbes);
            customDirections = EditorGUILayout.IntField("Directions", customDirections);
            customSamples = EditorGUILayout.IntField("Samples", customSamples);
            ResolveEffectiveQuality(out probes, out dirs, out samples);
        }
        else
        {
            EditorGUILayout.LabelField("Probes", probes.ToString());
            EditorGUILayout.LabelField("Directions", dirs.ToString());
            EditorGUILayout.LabelField("Samples", samples.ToString());
        }

        EditorGUILayout.Space();
        GUILayout.Label("其余参数", EditorStyles.boldLabel);
        seed = EditorGUILayout.IntField("Seed", seed);
        minDist = EditorGUILayout.FloatField("Min Dist", minDist);
        bounces = EditorGUILayout.IntField("Bounces", bounces);
        defaultAlbedo = EditorGUILayout.Slider("Default Albedo", defaultAlbedo, 0.0f, 1.0f);
        topKSample = EditorGUILayout.IntField("Top K Sample", topKSample);
        topKVertex = EditorGUILayout.IntField("Top K Vertex", topKVertex);
        shOrder = EditorGUILayout.IntSlider("SH Order", shOrder, 0, 2);
        lambdaReg = EditorGUILayout.FloatField("Lambda Reg", lambdaReg);

        string validationError;
        bool valid = ValidateParameters(probes, dirs, samples, out validationError);
        if (!valid)
        {
            EditorGUILayout.HelpBox(validationError, MessageType.Error);
        }
        else if (samples * dirs > 450000)
        {
            EditorGUILayout.HelpBox("当前参数组合较重，预计耗时会显著上升。", MessageType.Warning);
        }

        TimeEstimate estimate = EstimateBakeTime(probes, dirs, samples);
        EditorGUILayout.HelpBox(
            $"预估时间: {FormatSeconds(estimate.totalSec)}\n" +
            $"S1 Sampling: {FormatSeconds(estimate.step1Sec)}\n" +
            $"S2 Probe Export: {FormatSeconds(estimate.step2Sec)}\n" +
            $"S3 SH Fit: {FormatSeconds(estimate.step3Sec)}\n" +
            $"S4 Pack: {FormatSeconds(estimate.step4Sec)}",
            MessageType.Info
        );

        EditorGUILayout.Space();
        pythonPath = EditorGUILayout.TextField("Python Command", pythonPath);
        EditorGUILayout.HelpBox("Custom模式下一切手动指定 probes/directions/samples。\n输出自动根据“场景-日期-次数”命名，目录在 Data/probes/.", MessageType.Info);

        EditorGUILayout.Space();
        GUILayout.Label("步骤集成", EditorStyles.boldLabel);
        includeStep0GenerateUV2 = EditorGUILayout.ToggleLeft("运行步骤0: 生成 UV2", includeStep0GenerateUV2);
        includeStep1ExportLights = EditorGUILayout.ToggleLeft("运行步骤1: 导出场景灯光", includeStep1ExportLights);
        includeStep2ExportMesh = EditorGUILayout.ToggleLeft("运行步骤2: 导出网格烘焙数据", includeStep2ExportMesh);
        autoApplyAfterBake = EditorGUILayout.ToggleLeft("烘焙后自动应用到 Unity", autoApplyAfterBake);
        autoAssignProbeMapToMaterials = EditorGUILayout.ToggleLeft("自动将探针图分配给 GI 材质（实例）", autoAssignProbeMapToMaterials);

        if (!string.IsNullOrEmpty(lastPreflightMessage))
        {
            EditorGUILayout.HelpBox(lastPreflightMessage, MessageType.None);
        }

        using (new EditorGUI.DisabledScope(!valid))
        {
            if (GUILayout.Button("导出数据并烘焙回写", GUILayout.Height(34)))
            {
                RunIntegratedPipeline();
            }
            if (GUILayout.Button("仅运行 Python 烘焙", GUILayout.Height(40)))
            {
                RunBakingPipeline();
            }
        }
        
        EditorGUILayout.Space();
        if (!string.IsNullOrEmpty(lastBakeOutputDir))
        {
            GUI.color = Color.green;
            if (GUILayout.Button("导入纹理并应用 UV2", GUILayout.Height(30)))
            {
                ApplyBakeDataToUnity(lastBakeOutputDir);
            }
            GUI.color = Color.white;
            EditorGUILayout.HelpBox($"将应用以下目录的数据:\n{lastBakeOutputDir}", MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
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

        int probes;
        int dirs;
        int samples;
        ResolveEffectiveQuality(out probes, out dirs, out samples);

        string validationError;
        if (!ValidateParameters(probes, dirs, samples, out validationError))
        {
            UnityEngine.Debug.LogError($"[GI] Invalid parameters: {validationError}");
            return;
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
            Stopwatch totalTimer = Stopwatch.StartNew();
            float step1Sec = 0.0f;
            float step2Sec = 0.0f;
            float step3Sec = 0.0f;
            float step4Sec = 0.0f;
            string progressTitle = isIntegratedRunInProgress ? "GI Run All Pipeline" : "GI Baking Pipeline";

            Stopwatch stepTimer = Stopwatch.StartNew();
            ShowProgress(progressTitle, "1/4 Surface Sampling & Raytracing...", isIntegratedRunInProgress ? 0.35f : 0.2f);
            // 参数来自本工具 UI。
            RunPython(workspaceRoot, "Offline/sampling/sample_surface.py", 
                $"--mesh-json \"{meshJsonPath}\" --scene-json \"{sceneJsonPath}\" --output \"{samplesOut}\" --min-dist {minDist} --num-samples {samples} --directions {dirs} --bounces {bounces} --default-albedo {defaultAlbedo} --seed {seed} --dirs-out \"{dirsOut}\"");
            step1Sec = (float)stepTimer.Elapsed.TotalSeconds;

            stepTimer.Restart();
            ShowProgress(progressTitle, "2/4 Probe Clustering & Weights...", isIntegratedRunInProgress ? 0.55f : 0.45f);
            RunPython(workspaceRoot, "Offline/export/export_probes.py", 
                $"--samples-json \"{samplesOut}\" --mesh-json \"{meshJsonPath}\" --probes {probes} --top-k-sample {topKSample} --top-k-vertex {topKVertex} --output-dir \"{outDir}\" --seed {seed}");
            step2Sec = (float)stepTimer.Elapsed.TotalSeconds;

            stepTimer.Restart();
            ShowProgress(progressTitle, "3/4 Solving SH Coefficients...", isIntegratedRunInProgress ? 0.75f : 0.7f);
            RunPython(workspaceRoot, "Offline/baking/fit_sh.py", 
                $"--samples-json \"{samplesOut}\" --sample-weights \"{outDir}/sample_weights.json\" --order {shOrder} --lambda-reg {lambdaReg} --output-npy \"{outDir}/probes_sh.npy\" --output-json \"{outDir}/probes_sh.json\" --dirs-npy \"{dirsOut}\"");
            step3Sec = (float)stepTimer.Elapsed.TotalSeconds;

            stepTimer.Restart();
            ShowProgress(progressTitle, "4/4 Packing Probes to Texture...", isIntegratedRunInProgress ? 0.9f : 0.9f);
            RunPython(workspaceRoot, "Offline/baking/pack_probes.py", 
                $"--probes-npy \"{outDir}/probes_sh.npy\" --order {shOrder} --output-tex \"{outDir}/probe_map.npy\" --output-meta \"{outDir}/probe_map_meta.json\"");
            step4Sec = (float)stepTimer.Elapsed.TotalSeconds;

            lastBakeOutputDir = Path.Combine(workspaceRoot, outDir).Replace('\\', '/');
            totalTimer.Stop();
            UpdateTimeCalibration(probes, dirs, samples, step1Sec + step2Sec + step3Sec + step4Sec);

            UnityEngine.Debug.Log(
                $"<color=#00FF00><b>[GI] Baking successful!</b></color>\n" +
                $"Outputs saved in: {outDir}\n" +
                $"Actual Time: {FormatSeconds((float)totalTimer.Elapsed.TotalSeconds)}\n" +
                $"S1={FormatSeconds(step1Sec)}, S2={FormatSeconds(step2Sec)}, S3={FormatSeconds(step3Sec)}, S4={FormatSeconds(step4Sec)}"
            );
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
    /// 一键串联 Step0/1/2 与 Python 四步，并可在完成后自动回填 Unity。
    /// </summary>
    private void RunIntegratedPipeline()
    {
        if (!PreflightCheck(out string message))
        {
            lastPreflightMessage = message;
            UnityEngine.Debug.LogError($"[GI] Preflight failed: {message}");
            return;
        }
        lastPreflightMessage = message;

        string workspaceRoot = GetWorkspaceRoot();
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (string.IsNullOrEmpty(sceneName))
        {
            sceneName = "UntitledScene";
        }

        try
        {
            Stopwatch integratedTimer = Stopwatch.StartNew();
            isIntegratedRunInProgress = true;
            if (includeStep0GenerateUV2)
            {
                Stopwatch stepTimer = Stopwatch.StartNew();
                ShowProgress("GI Run All Pipeline", "Step0: Generate UV2...", 0.05f);
                UV2Generator.GenerateUV2ForScene();
                UnityEngine.Debug.Log($"[GI] Step0 finished in {FormatSeconds((float)stepTimer.Elapsed.TotalSeconds)}");
            }

            if (includeStep1ExportLights)
            {
                Stopwatch stepTimer = Stopwatch.StartNew();
                ShowProgress("GI Run All Pipeline", "Step1: Export Lights...", 0.12f);
                string lightAbsPath = Path.Combine(workspaceRoot, "Data", "scenes", sceneName + "_lights.json");

                bool canSkip = !EditorSceneManager.GetActiveScene().isDirty && File.Exists(lightAbsPath);
                if (canSkip)
                {
                    sceneJsonPath = GetWorkspaceRelativePath(lightAbsPath, workspaceRoot);
                    UnityEngine.Debug.Log($"[GI] Step1 skipped (scene unchanged): {sceneJsonPath}");
                }
                else if (SceneLightExporter.ExportLightsToPath(lightAbsPath, includeMeshInstances: false, prettyPrint: false))
                {
                    sceneJsonPath = GetWorkspaceRelativePath(lightAbsPath, workspaceRoot);
                }
                UnityEngine.Debug.Log($"[GI] Step1 finished in {FormatSeconds((float)stepTimer.Elapsed.TotalSeconds)}");
            }

            if (includeStep2ExportMesh)
            {
                Stopwatch stepTimer = Stopwatch.StartNew();
                ShowProgress("GI Run All Pipeline", "Step2: Export Mesh Data...", 0.2f);
                string meshAbsPath = Path.Combine(workspaceRoot, "Data", "meshs", sceneName + "_mesh.json");
                if (MeshBakeExporter.ExportJsonToPath(meshAbsPath))
                {
                    meshJsonPath = GetWorkspaceRelativePath(meshAbsPath, workspaceRoot);
                }
                UnityEngine.Debug.Log($"[GI] Step2 finished in {FormatSeconds((float)stepTimer.Elapsed.TotalSeconds)}");
            }

            RunBakingPipeline();

            if (autoApplyAfterBake && !string.IsNullOrEmpty(lastBakeOutputDir))
            {
                ApplyBakeDataToUnity(lastBakeOutputDir);
            }

            integratedTimer.Stop();
            UnityEngine.Debug.Log($"[GI] Run All finished in {FormatSeconds((float)integratedTimer.Elapsed.TotalSeconds)}");
        }
        finally
        {
            isIntegratedRunInProgress = false;
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// 统一进度条更新并触发界面刷新，避免长任务阶段看起来卡在旧文案。
    /// </summary>
    private void ShowProgress(string title, string info, float progress)
    {
        EditorUtility.DisplayProgressBar(title, info, Mathf.Clamp01(progress));
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
        Repaint();
    }

    /// <summary>
    /// 执行一键流程前的基础可用性检查。
    /// </summary>
    private bool PreflightCheck(out string message)
    {
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            message = "Python command is empty.";
            return false;
        }

        int probes;
        int dirs;
        int samples;
        ResolveEffectiveQuality(out probes, out dirs, out samples);
        if (!ValidateParameters(probes, dirs, samples, out string validationError))
        {
            message = validationError;
            return false;
        }

        string workspaceRoot = GetWorkspaceRoot();
        string[] requiredScripts =
        {
            Path.Combine(workspaceRoot, "Offline", "sampling", "sample_surface.py"),
            Path.Combine(workspaceRoot, "Offline", "export", "export_probes.py"),
            Path.Combine(workspaceRoot, "Offline", "baking", "fit_sh.py"),
            Path.Combine(workspaceRoot, "Offline", "baking", "pack_probes.py"),
        };

        foreach (string s in requiredScripts)
        {
            if (!File.Exists(s))
            {
                message = "Missing script: " + s;
                return false;
            }
        }

        message = "Preflight passed.";
        return true;
    }

    /// <summary>
    /// 运行单个 Python 脚本，采用持续轮询并强制刷新 UI，解决假死定格问题。
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
            // 使用异步事件吸收输出，防止缓冲区塞满死锁
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };
            
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 保持主线程轮询，并且用 DisplayProgressBar 强制系统 Pump 消息泵
            while (!process.HasExited)
            {
                // 注意：这里适当停顿防止占满CPU，并且不阻碍UI绘制
                System.Threading.Thread.Sleep(50);
            }

            process.WaitForExit(); // 确保安全结束

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
            int texelsPerProbe;
            int probeCount = ImportProbeTexture(npyPath, metaPath, targetAssetPath, out texelsPerProbe);

            if (probeCount > 0)
            {
                EditorUtility.DisplayProgressBar("GI Apply", "Applying UV2 to Meshes...", 0.6f);
                ApplyMeshAssocToAll(assocPath, probeCount);
                if (autoAssignProbeMapToMaterials)
                {
                    EditorUtility.DisplayProgressBar("GI Apply", "Assigning ProbeMap To GI Materials...", 0.85f);
                    int bound, skipped;
                    AssignProbeMapToGiMaterials(targetAssetPath, probeCount, texelsPerProbe, out bound, out skipped);
                    UnityEngine.Debug.Log($"[GI] Material ProbeMap assignment done. Bound={bound}, Skipped={skipped}.");
                }
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
    private int ImportProbeTexture(string npy, string metaStr, string assetPath, out int texelsPerProbe)
    {
        var meta = JsonUtility.FromJson<ProbeMapMeta>(File.ReadAllText(metaStr));
        texelsPerProbe = meta != null ? meta.texels_per_probe : 0;
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
    /// 将 ProbeMap 自动绑定到场景中启用 GI 的对象实例材质。
    /// </summary>
    private void AssignProbeMapToGiMaterials(string probeMapAssetPath, int probeCount, int texelsPerProbe, out int boundCount, out int skippedCount)
    {
        boundCount = 0;
        skippedCount = 0;

        Texture2D probeTex = AssetDatabase.LoadAssetAtPath<Texture2D>(probeMapAssetPath);
        if (probeTex == null)
        {
            throw new Exception($"ProbeMap asset not found: {probeMapAssetPath}");
        }

        var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var mr in renderers)
        {
            if (!mr.enabled)
            {
                continue;
            }

            bool contributesGI = GameObjectUtility.AreStaticEditorFlagsSet(mr.gameObject, StaticEditorFlags.ContributeGI);
            bool lightProbeOnly = mr.receiveGI == ReceiveGI.LightProbes;
            if (!contributesGI || lightProbeOnly)
            {
                continue;
            }

            Material[] mats = mr.materials; // 实例材质修改
            bool rendererChanged = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material mat = mats[i];
                if (mat == null)
                {
                    skippedCount++;
                    continue;
                }
                if (!mat.HasProperty("_ProbeMap"))
                {
                    skippedCount++;
                    continue;
                }

                mat.SetTexture("_ProbeMap", probeTex);
                if (mat.HasProperty("_ProbeCount"))
                {
                    mat.SetFloat("_ProbeCount", probeCount);
                }
                if (mat.HasProperty("_TexelsPerProbe"))
                {
                    mat.SetFloat("_TexelsPerProbe", texelsPerProbe);
                }
                EditorUtility.SetDirty(mat);
                boundCount++;
                rendererChanged = true;
            }

            if (rendererChanged)
            {
                mr.materials = mats;
                EditorUtility.SetDirty(mr);
            }
        }

        var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(currentScene);
        AssetDatabase.SaveAssets();
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
        var goMap = new Dictionary<string, MeshFilter>();
        foreach (var mf in filters)
        {
            if (mf.sharedMesh != null && !goMap.ContainsKey(mf.gameObject.name))
            {
                // 以 GameObject 名称映射到 MeshFilter，因为需要回写 sharedMesh 引用。
                goMap[mf.gameObject.name] = mf;
            }
        }

        string bakedMeshFolder = "Assets/WishGI/Resources/BakedMeshes";
        EnsureAssetFolder(bakedMeshFolder);

        int successCount = 0;
        int replacedMeshCount = 0;
        int probeDenom = Mathf.Max(probeCount - 1, 1);

        foreach (var entry in wrapper.items)
        {
            if (goMap.TryGetValue(entry.mesh_name, out MeshFilter targetFilter))
            {
                Mesh meshToModify = targetFilter.sharedMesh;
                if (meshToModify == null)
                {
                    continue;
                }

                string sourceAssetPath = AssetDatabase.GetAssetPath(meshToModify);
                bool isImportedMesh = !string.IsNullOrEmpty(sourceAssetPath) &&
                                      !sourceAssetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase);

                if (isImportedMesh)
                {
                    string safeName = SanitizeAssetName(entry.mesh_name);
                    string bakedMeshPath = $"{bakedMeshFolder}/{safeName}_Baked.asset";
                    Mesh bakedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(bakedMeshPath);

                    if (bakedMesh == null)
                    {
                        bakedMesh = UnityEngine.Object.Instantiate(meshToModify);
                        bakedMesh.name = meshToModify.name + "_Baked";
                        AssetDatabase.CreateAsset(bakedMesh, bakedMeshPath);
                    }

                    meshToModify = bakedMesh;
                    if (targetFilter.sharedMesh != meshToModify)
                    {
                        targetFilter.sharedMesh = meshToModify;
                        EditorUtility.SetDirty(targetFilter);
                        replacedMeshCount++;
                    }
                }

                var uv2 = new List<Vector4>(meshToModify.vertexCount);
                for (int i = 0; i < meshToModify.vertexCount; i++) uv2.Add(Vector4.zero);

                foreach (var v in entry.vertices)
                {
                    if (v.vertex_id < 0 || v.vertex_id >= uv2.Count) continue;
                    float i0 = 0, w0 = 0, i1 = 0, w1 = 0;
                    if (v.probes != null && v.probes.Count > 0)
                    {
                        w0 = v.probes[0].w;
                        // 将探针索引归一化写入 uv2，运行时按 probeCount 反解。
                        i0 = v.probes[0].id / (float)probeDenom;
                        if (v.probes.Count > 1)
                        {
                            w1 = v.probes[1].w;
                            i1 = v.probes[1].id / (float)probeDenom;
                        }
                    }
                    uv2[v.vertex_id] = new Vector4(i0, w0, i1, w1);
                }

                meshToModify.SetUVs(1, uv2);
                EditorUtility.SetDirty(meshToModify);
                successCount++;
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[GI] Could not find scene mesh mapped to name: {entry.mesh_name}");
            }
        }

        AssetDatabase.SaveAssets();
        if (replacedMeshCount > 0)
        {
            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(currentScene);
        }

        UnityEngine.Debug.Log($"[GI] UV2 updated for {successCount} meshes. Persistent mesh replacements: {replacedMeshCount}.");
    }

    /// <summary>
    /// 确保 Asset 文件夹存在（例如 Assets/WishGI/Resources/BakedMeshes）。
    /// </summary>
    private void EnsureAssetFolder(string folderPath)
    {
        folderPath = folderPath.Replace('\\', '/');
        if (folderPath == "Assets" || AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(parent))
        {
            throw new Exception($"Invalid asset folder path: {folderPath}");
        }

        EnsureAssetFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folderPath));
    }

    /// <summary>
    /// 清洗文件名，避免写入非法字符导致资产创建失败。
    /// </summary>
    private string SanitizeAssetName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "Mesh";
        }

        StringBuilder sb = new StringBuilder(rawName.Length);
        foreach (char c in rawName)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 根据预设或自定义设置解析实际的 probes / directions / samples。
    /// </summary>
    private void ResolveEffectiveQuality(out int probes, out int dirs, out int samples)
    {
        probes = 64;
        dirs = 128;
        samples = 256;

        if (qualityPreset == QualityPreset.High)
        {
            probes = 128;
            dirs = 512;
            samples = 512;
        }
        else if (qualityPreset == QualityPreset.Custom)
        {
            probes = customProbes;
            dirs = customDirections;
            samples = customSamples;
        }
    }

    /// <summary>
    /// 参数合法性检查
    /// </summary>
    private bool ValidateParameters(int probes, int dirs, int samples, out string error)
    {
        if (probes <= 0)
        {
            error = "Probes must be > 0.";
            return false;
        }
        if (dirs <= 0)
        {
            error = "Directions must be > 0.";
            return false;
        }
        if (samples <= 0)
        {
            error = "Samples must be > 0.";
            return false;
        }
        if (seed < 0)
        {
            error = "Seed must be >= 0.";
            return false;
        }
        if (minDist <= 0.0f)
        {
            error = "Min Dist must be > 0.";
            return false;
        }
        if (bounces < 1)
        {
            error = "Bounces must be >= 1.";
            return false;
        }
        if (defaultAlbedo < 0.0f || defaultAlbedo > 1.0f)
        {
            error = "Default Albedo must be in [0,1].";
            return false;
        }
        if (topKSample <= 0 || topKVertex <= 0)
        {
            error = "Top-K values must be > 0.";
            return false;
        }
        if (shOrder < 0 || shOrder > 2)
        {
            error = "SH Order must be in [0,2].";
            return false;
        }
        if (lambdaReg < 0.0f)
        {
            error = "Lambda Reg must be >= 0.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 估算四个离线步骤耗时。采用简单经验模型，并通过历史运行进行校准。
    /// </summary>
    private TimeEstimate EstimateBakeTime(int probes, int dirs, int samples)
    {
        float rawS1 = samples * dirs * Mathf.Max(1, bounces) * 0.00035f;
        float rawS2 = samples * probes * 0.00008f;
        float rawS3 = samples * dirs * 0.00005f + probes * probes * 0.0015f;
        float rawS4 = probes * 0.02f;

        float calib = EditorPrefs.GetFloat(TimeCalibrationKey, 1.0f);
        calib = Mathf.Clamp(calib, 0.25f, 4.0f);

        TimeEstimate t;
        t.step1Sec = rawS1 * calib;
        t.step2Sec = rawS2 * calib;
        t.step3Sec = rawS3 * calib;
        t.step4Sec = rawS4 * calib;
        t.totalSec = t.step1Sec + t.step2Sec + t.step3Sec + t.step4Sec;
        return t;
    }

    /// <summary>
    /// 使用本次真实耗时更新估时校准系数。
    /// </summary>
    private void UpdateTimeCalibration(int probes, int dirs, int samples, float actualTotalSec)
    {
        float rawS1 = samples * dirs * Mathf.Max(1, bounces) * 0.00035f;
        float rawS2 = samples * probes * 0.00008f;
        float rawS3 = samples * dirs * 0.00005f + probes * probes * 0.0015f;
        float rawS4 = probes * 0.02f;
        float rawTotal = Mathf.Max(1.0f, rawS1 + rawS2 + rawS3 + rawS4);

        float measured = Mathf.Max(1.0f, actualTotalSec);
        float targetCalib = Mathf.Clamp(measured / rawTotal, 0.25f, 4.0f);
        float oldCalib = Mathf.Clamp(EditorPrefs.GetFloat(TimeCalibrationKey, 1.0f), 0.25f, 4.0f);
        float newCalib = Mathf.Lerp(oldCalib, targetCalib, 0.35f);
        EditorPrefs.SetFloat(TimeCalibrationKey, newCalib);
    }

    /// <summary>
    /// 将秒数格式化为可读时长。
    /// </summary>
    private string FormatSeconds(float sec)
    {
        sec = Mathf.Max(0.0f, sec);
        TimeSpan ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalHours >= 1.0)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        }
        if (ts.TotalMinutes >= 1.0)
        {
            return $"{ts.Minutes}m {ts.Seconds}s";
        }
        return $"{ts.Seconds}s";
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
