using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using WishGI.Baking;

// 避免与 System.* 同名类型冲突
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;
using UEObject = UnityEngine.Object;

namespace WishGI.Baking.Editor
{
    /// <summary>
    /// 场景网格采集与导出工具。
    /// 菜单路径：Tools/WishGI/Export Mesh Bake Data (JSON / SO)
    /// </summary>
    public static class MeshBakeExporter
    {
        [MenuItem("Tools/WishGI/Export Mesh Bake Data/Export JSON...", priority = 0)]
        public static void ExportJson()
        {
            var data = CollectBakeData();
            if (data.meshObjects.Count == 0)
            {
                EditorUtility.DisplayDialog("Mesh Bake Export", "没有可用网格被导出。", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanel("Save Mesh Bake JSON", Application.dataPath, "MeshBakeData", "json");
            if (string.IsNullOrEmpty(path)) return;

            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            Debug.Log($"[MeshBakeExporter] JSON 导出完成: {path}");
            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("Tools/WishGI/Export Mesh Bake Data/Export ScriptableObject...", priority = 1)]
        public static void ExportScriptableObject()
        {
            var data = CollectBakeData();
            if (data.meshObjects.Count == 0)
            {
                EditorUtility.DisplayDialog("Mesh Bake Export", "没有可用网格被导出。", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Save Mesh Bake SO", "MeshBakeData", "asset", "Choose location for MeshBakeData asset");
            if (string.IsNullOrEmpty(path)) return;

            var asset = ScriptableObject.CreateInstance<MeshBakeData>();
            asset.meshObjects = data.meshObjects;

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[MeshBakeExporter] ScriptableObject 导出完成: {path}");
            Selection.activeObject = asset;
        }

        /// <summary>
        /// 核心采集逻辑：
        /// - 遍历场景 MeshRenderer
        /// - 过滤无效对象
        /// - 拉取 world-space 顶点、法线、UV2、索引
        /// </summary>
        private static MeshBakeDataJson CollectBakeData()
        {
            var result = new MeshBakeDataJson();
            var renderers = UEObject.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            foreach (var mr in renderers)
            {
                if (!mr.enabled) continue;

                var go = mr.gameObject;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null) continue;

                var mesh = mf.sharedMesh;
                if (mesh == null) continue;

                // 过滤不参与 GI 或 LightProbeOnly 的对象
                bool contributesGI = GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.ContributeGI);
                bool lightProbeOnly = mr.receiveGI == ReceiveGI.LightProbes; // 视为 “LightProbeOnly”
                if (!contributesGI || lightProbeOnly) continue;

                // 可读性检查
                if (!mesh.isReadable)
                {
                    Debug.LogWarning($"[MeshBakeExporter] Mesh 不可读 (需启用 Read/Write Enabled): {go.name}", go);
                    continue;
                }

                // UV2 检查
                var uv2 = mesh.uv2;
                if (uv2 == null || uv2.Length == 0)
                {
                    Debug.LogWarning($"[MeshBakeExporter] 缺少 UV2 (光照 UV): {go.name}", go);
                }

                var verts = mesh.vertices;
                var norms = mesh.normals;
                var indices = mesh.triangles;

                var positionsWS = new Vector3[verts.Length];
                var normalsWS = new Vector3[norms.Length];

                // 转世界空间
                for (int i = 0; i < verts.Length; i++)
                    positionsWS[i] = go.transform.TransformPoint(verts[i]);

                for (int i = 0; i < norms.Length; i++)
                    normalsWS[i] = go.transform.TransformDirection(norms[i]).normalized;

                var item = new MeshObjectData
                {
                    name = go.name,
                    instanceId = go.GetInstanceID(),
                    localToWorld = MatrixUtility.ToFloatArray(go.transform.localToWorldMatrix),
                    positions = positionsWS,
                    normals = normalsWS,
                    uv2 = uv2,
                    indices = indices
                };

                result.meshObjects.Add(item);
            }

            Debug.Log($"[MeshBakeExporter] 收集完成，共 {result.meshObjects.Count} 个网格对象。");
            return result;
        }
    }
}