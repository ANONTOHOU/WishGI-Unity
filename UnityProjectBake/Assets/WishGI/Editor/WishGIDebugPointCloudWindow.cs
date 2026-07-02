using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using static System.Net.Mime.MediaTypeNames;
using Application = UnityEngine.Application;
using Debug = UnityEngine.Debug;

public class WishGIDebugPointCloudWindow : EditorWindow
{
    [Serializable]
    private class Vec3Data
    {
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }
    }

    [Serializable]
    private class SampleEntry
    {
        public Vec3Data position;
    }

    [Serializable]
    private class SamplesFile
    {
        public List<SampleEntry> samples;
    }

    [Serializable]
    private class ProbeEntry
    {
        public int probe_id;
        public Vec3Data position;
        public string space;
    }

    [Serializable]
    private class ProbeArrayWrapper
    {
        public List<ProbeEntry> items;
    }

    private string samplesJsonPath = "Data/samples/SampleScene_samples_pt.json";
    private string probesJsonPath = "Data/probes/probes.json";
    private string rootObjectName = "WishGI_DebugPointCloud";

    private bool importSamples = true;
    private bool importProbes = true;
    private bool selectImportedObject = true;
    private int sampleStride = 1;

    [MenuItem("GI/Debug Point Cloud")]
    public static void ShowWindow()
    {
        GetWindow<WishGIDebugPointCloudWindow>("GI Debug Points");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("WishGI 点云可视化", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUILayout.HelpBox(
            "推荐做法：Sample 用 Gizmos 画灰色点，Probe 用 Gizmos 画红色点。\n" +
            "这样 Scene 里只需要一个根对象，清理时也只需要清空一次或删除一次。",
            MessageType.Info
        );

        rootObjectName = EditorGUILayout.TextField("Root Object Name", rootObjectName);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("输入文件", EditorStyles.boldLabel);

        importSamples = EditorGUILayout.ToggleLeft("导入 Samples", importSamples);
        using (new EditorGUI.DisabledScope(!importSamples))
        {
            samplesJsonPath = DrawPathField("Samples JSON", samplesJsonPath);
            sampleStride = EditorGUILayout.IntSlider("Sample Stride", Mathf.Max(1, sampleStride), 1, 16);
        }

        importProbes = EditorGUILayout.ToggleLeft("导入 Probes", importProbes);
        using (new EditorGUI.DisabledScope(!importProbes))
        {
            probesJsonPath = DrawPathField("Probes JSON", probesJsonPath);
        }

        selectImportedObject = EditorGUILayout.ToggleLeft("导入后选中根对象", selectImportedObject);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);

        if (GUILayout.Button("Import / Refresh", GUILayout.Height(32)))
        {
            ImportOrRefresh();
        }

        if (GUILayout.Button("Clear Point Data", GUILayout.Height(26)))
        {
            ClearPointData();
        }

        if (GUILayout.Button("Delete Root Object", GUILayout.Height(26)))
        {
            DeleteRootObject();
        }
    }

    private string DrawPathField(string label, string value)
    {
        EditorGUILayout.BeginHorizontal();
        value = EditorGUILayout.TextField(label, value);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string startDir = GetProjectRoot();
            string picked = EditorUtility.OpenFilePanel(label, startDir, "json");
            if (!string.IsNullOrEmpty(picked))
            {
                value = picked;
            }
        }
        EditorGUILayout.EndHorizontal();
        return value;
    }

    private void ImportOrRefresh()
    {
        try
        {
            List<Vector3> samplePoints = importSamples
                ? LoadSamplePoints(ResolvePath(samplesJsonPath), Mathf.Max(1, sampleStride))
                : new List<Vector3>();

            List<Vector3> probePoints = importProbes
                ? LoadProbePoints(ResolvePath(probesJsonPath))
                : new List<Vector3>();

            WishGIDebugPointCloud pointCloud = FindOrCreatePointCloud();

            Undo.RecordObject(pointCloud, "Import GI Debug Points");
            pointCloud.SetPoints(samplePoints, probePoints);
            EditorUtility.SetDirty(pointCloud);

            if (selectImportedObject)
            {
                Selection.activeObject = pointCloud.gameObject;
            }

            Debug.Log(
                "[WishGI] 点云导入完成: " +
                "samples=" + samplePoints.Count +
                ", probes=" + probePoints.Count +
                ", root=" + pointCloud.gameObject.name
            );
        }
        catch (Exception ex)
        {
            Debug.LogError("[WishGI] 点云导入失败: " + ex.Message);
        }
    }

    private void ClearPointData()
    {
        WishGIDebugPointCloud pointCloud = FindExistingPointCloud();
        if (pointCloud == null)
        {
            Debug.LogWarning("[WishGI] 未找到可视化根对象，无法清空。");
            return;
        }

        Undo.RecordObject(pointCloud, "Clear GI Debug Points");
        pointCloud.ClearPoints();
        EditorUtility.SetDirty(pointCloud);

        Debug.Log("[WishGI] 已清空点数据。");
    }

    private void DeleteRootObject()
    {
        WishGIDebugPointCloud pointCloud = FindExistingPointCloud();
        if (pointCloud == null)
        {
            Debug.LogWarning("[WishGI] 未找到可视化根对象，无法删除。");
            return;
        }

        Undo.DestroyObjectImmediate(pointCloud.gameObject);
        Debug.Log("[WishGI] 已删除可视化根对象。");
    }

    private WishGIDebugPointCloud FindOrCreatePointCloud()
    {
        WishGIDebugPointCloud existing = FindExistingPointCloud();
        if (existing != null)
        {
            return existing;
        }

        GameObject root = new GameObject(rootObjectName);
        Undo.RegisterCreatedObjectUndo(root, "Create GI Debug Point Cloud");

        root.transform.position = Vector3.zero;
        root.transform.rotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        WishGIDebugPointCloud pointCloud = Undo.AddComponent<WishGIDebugPointCloud>(root);
        return pointCloud;
    }

    private WishGIDebugPointCloud FindExistingPointCloud()
    {
        GameObject root = GameObject.Find(rootObjectName);
        if (root == null)
        {
            return null;
        }

        return root.GetComponent<WishGIDebugPointCloud>();
    }

    private List<Vector3> LoadSamplePoints(string path, int stride)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Samples 文件不存在: " + path);
        }

        string raw = File.ReadAllText(path);
        SamplesFile file = JsonUtility.FromJson<SamplesFile>(raw);

        List<Vector3> points = new List<Vector3>();
        if (file == null || file.samples == null)
        {
            return points;
        }

        for (int i = 0; i < file.samples.Count; i += stride)
        {
            SampleEntry entry = file.samples[i];
            if (entry != null && entry.position != null)
            {
                points.Add(entry.position.ToVector3());
            }
        }

        return points;
    }

    private List<Vector3> LoadProbePoints(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Probes 文件不存在: " + path);
        }

        string raw = File.ReadAllText(path);
        string wrapped = "{\"items\":" + raw + "}";

        ProbeArrayWrapper file = JsonUtility.FromJson<ProbeArrayWrapper>(wrapped);
        List<Vector3> points = new List<Vector3>();

        if (file == null || file.items == null)
        {
            return points;
        }

        for (int i = 0; i < file.items.Count; i++)
        {
            ProbeEntry entry = file.items[i];
            if (entry != null && entry.position != null)
            {
                points.Add(entry.position.ToVector3());
            }
        }

        return points;
    }

    private string ResolvePath(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(inputPath))
        {
            return Path.GetFullPath(inputPath);
        }

        return Path.GetFullPath(Path.Combine(GetProjectRoot(), "..", inputPath));
    }

    private string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }
}