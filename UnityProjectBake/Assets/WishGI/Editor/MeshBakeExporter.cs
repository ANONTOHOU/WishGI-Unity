using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using WishGI.Baking;

// ������ System.* ͬ�����ͳ�ͻ
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;
using UEObject = UnityEngine.Object;

namespace WishGI.Baking.Editor
{
    /// <summary>
    /// ��������ɼ��뵼�����ߡ�
    /// �˵�·����Tools/WishGI/Export Mesh Bake Data (JSON / SO)
    /// </summary>
    public static class MeshBakeExporter
    {
        [MenuItem("WishGI/Step 2: Export Mesh Bake Data/Export JSON", false, 12)]
        public static void ExportJson()
        {
            var data = CollectBakeData();
            if (data.meshObjects.Count == 0)
            {
                EditorUtility.DisplayDialog("Mesh Bake Export", "û�п������񱻵�����", "OK");
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

            string path = EditorUtility.SaveFilePanel("Save Mesh Bake JSON", defaultFolder, sceneName + "_mesh.json", "json");
            if (string.IsNullOrEmpty(path)) return;

            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            Debug.Log($"[WishGI] 成功导出 {data.meshObjects.Count} 个网格的烘焙数据至: {path}");
            AssetDatabase.Refresh();
        }

        [MenuItem("WishGI/Step 2: Export Mesh Bake Data/Export ScriptableObject", false, 13)]
        public static void ExportScriptableObject()
        {
            var data = CollectBakeData();
            if (data.meshObjects.Count == 0)
            {
                EditorUtility.DisplayDialog("Mesh Bake Export", "û�п������񱻵�����", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Save Mesh Bake SO", "MeshBakeData", "asset", "Choose location for MeshBakeData asset");
            if (string.IsNullOrEmpty(path)) return;

            var asset = ScriptableObject.CreateInstance<MeshBakeData>();
            asset.meshObjects = data.meshObjects;

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[WishGI] ScriptableObject �������: {path}");
            Selection.activeObject = asset;
        }

        /// <summary>
        /// ���Ĳɼ��߼���
        /// - �������� MeshRenderer
        /// - ������Ч����
        /// - ��ȡ world-space ���㡢���ߡ�UV2������
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

                // ���˲����� GI �� LightProbeOnly �Ķ���
                bool contributesGI = GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.ContributeGI);
                bool lightProbeOnly = mr.receiveGI == ReceiveGI.LightProbes; // ��Ϊ ��LightProbeOnly��
                if (!contributesGI || lightProbeOnly) continue;

                // �ɶ��Լ��
                if (!mesh.isReadable)
                {
                    Debug.LogWarning($"[WishGI] Mesh ���ɶ� (������ Read/Write Enabled): {go.name}", go);
                    continue;
                }

                // UV2 ���
                var uv2 = mesh.uv2;
                if (uv2 == null || uv2.Length == 0)
                {
                    Debug.LogWarning($"[WishGI] ȱ�� UV2 (���� UV): {go.name}", go);
                }

                var verts = mesh.vertices;
                var norms = mesh.normals;
                var indices = mesh.triangles;

                var positionsWS = new Vector3[verts.Length];
                var normalsWS = new Vector3[norms.Length];

                // ת����ռ�
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

            Debug.Log($"[WishGI] �ռ���ɣ��� {result.meshObjects.Count} ���������");
            return result;
        }
    }
}
