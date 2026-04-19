using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using WishGI.Baking;

// 消除 System.* 与 UnityEngine.Object 的命名空间冲突
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;
using UEObject = UnityEngine.Object;

namespace WishGI.Baking.Editor
{
    /// <summary>
    /// 菜单入口： 第 2 步，导出网格烘焙数据（JSON / SO）。
    /// </summary>
    public static class MeshBakeExporter
    {
        /// <summary>
        /// 导出当前场景参与 GI 的网格数据到 JSON。
        /// </summary>
        [MenuItem("GI/Step 2: Export Mesh Bake Data/Export JSON", false, 12)]
        public static void ExportJson()
        {
            var data = CollectBakeData();
            if (data.meshObjects.Count == 0)
            {
                EditorUtility.DisplayDialog("Mesh Bake Export", "没有找到符合渲染条件的模型物体", "OK");
                return;
            }

            // 默认保存到工作区根目录下的 Data/meshs 文件夹
            string defaultFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Data/meshs"));
            if (!Directory.Exists(defaultFolder))
            {
                Directory.CreateDirectory(defaultFolder);
            }

            string sceneName = SceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(sceneName)) sceneName = "UntitledScene";

            // 采用“场景名 + _mesh.json”的默认命名，便于后续 Python 管线自动识别输入。
            string path = EditorUtility.SaveFilePanel("Save Mesh Bake JSON", defaultFolder, sceneName + "_mesh.json", "json");
            if (string.IsNullOrEmpty(path)) return;

            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            Debug.Log($"[GI] 成功导出 {data.meshObjects.Count} 个网格的烘焙数据至: {path}");
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 导出为 ScriptableObject 资产，便于在 Unity 内部调试和可视化检查。
        /// </summary>
        [MenuItem("GI/Step 2: Export Mesh Bake Data/Export ScriptableObject", false, 13)]
        public static void ExportScriptableObject()
        {
            var data = CollectBakeData();
            if (data.meshObjects.Count == 0)
            {
                EditorUtility.DisplayDialog("Mesh Bake Export", "没有找到符合渲染条件的模型物体", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Save Mesh Bake SO", "MeshBakeData", "asset", "Choose location for MeshBakeData asset");
            if (string.IsNullOrEmpty(path)) return;

            var asset = ScriptableObject.CreateInstance<MeshBakeData>();
            asset.meshObjects = data.meshObjects;

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[GI] ScriptableObject 导出成功: {path}");
            Selection.activeObject = asset;
        }

        /// <summary>
        /// 收集需要参与烘焙的数据：
        /// - 过滤有效的 MeshRenderer
        /// - 获取世界空间顶点法线以及 UV2 信息
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

                // 判断是否贡献 GI 且接收模式不是仅探针
                bool contributesGI = GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.ContributeGI);
                bool lightProbeOnly = mr.receiveGI == ReceiveGI.LightProbes;
                // 仅导出真正由 Lightmap 驱动的静态对象；只接收探针的对象不进入离线烘焙。
                if (!contributesGI || lightProbeOnly) continue;

                // 读写支持检测
                if (!mesh.isReadable)
                {
                    Debug.LogWarning($"[GI] Mesh 不可读 (需要在 Import Setting 勾选 Read/Write Enabled): {go.name}", go);
                    continue;
                }

                // 确保拥有 UV2 贴图
                var uv2 = mesh.uv2;
                if (uv2 == null || uv2.Length == 0)
                {
                    Debug.LogWarning($"[GI] 缺少 UV2 (没有 Lightmap UV): {go.name}", go);
                }

                var verts = mesh.vertices;
                var norms = mesh.normals;
                var indices = mesh.triangles;

                var positionsWS = new Vector3[verts.Length];
                var normalsWS = new Vector3[norms.Length];

                // 转换坐标系
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

            Debug.Log($"[GI] 收集完成，共有 {result.meshObjects.Count} 个有效进行光照烘焙的对象。");
            return result;
        }
    }
}
