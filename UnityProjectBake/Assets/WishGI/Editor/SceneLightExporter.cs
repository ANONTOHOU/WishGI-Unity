using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.IO;

namespace WishGI.Editor
{
    // 用于序列化的简单向量结构，保持JSON清晰易读
    [System.Serializable]
    public class Vec3Data
    {
        public float x, y, z;
        public Vec3Data(Vector3 v) { x = v.x; y = v.y; z = v.z; }
    }

    [System.Serializable]
    public class ColorData
    {
        public float r, g, b, a;
        public ColorData(Color c) { r = c.r; g = c.g; b = c.b; a = c.a; }
    }

    [System.Serializable]
    public class LightExportData
    {
        public string name;
        public string type;
        public Vec3Data position;
        public Vec3Data direction; // 灯光的前朝向 (forward)
        public ColorData color;
        public float intensity;
        public float range;
        public float spotAngle;
        public float innerSpotAngle;
        
        // 如果是区域光（Area Light）相关的尺寸
        public float areaSizeX;
        public float areaSizeY;
    }

    [System.Serializable]
    public class AmbientExportData
    {
        public string mode; // Flat / Trilight / Skybox
        public ColorData ambientColor;
        public ColorData skyColor;
        public ColorData equatorColor;
        public ColorData groundColor;
        public float ambientIntensity;
        public string skyboxTexturePath; // HDRI贴图的路径
    }

    [System.Serializable]
    public class MeshInstanceData
    {
        public string objectName;
        public string meshPath;
        public Vec3Data position;
        public Vec3Data eulerAngles;
        public Vec3Data scale;
    }

    [System.Serializable]
    public class SceneLightExportRoot
    {
        public string sceneName;
        public AmbientExportData ambient;
        public List<LightExportData> lights;
        public List<MeshInstanceData> meshInstances;
    }

    public class SceneLightExporter : EditorWindow
    {
        [MenuItem("WishGI/Step 1: Export Scene Lights to JSON", false, 11)]
        public static void ExportLights()
        {
            var data = new SceneLightExportRoot();
            data.sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(data.sceneName)) data.sceneName = "UntitledScene";

            data.ambient = GetAmbientData();
            data.lights = GetLightsData();
            data.meshInstances = GetMeshInstancesData();

            string json = JsonUtility.ToJson(data, true);
            
            // 默认保存到工作区根目录下的 Data/scenes 文件夹
            string defaultFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Data/scenes"));
            if (!Directory.Exists(defaultFolder))
            {
                Directory.CreateDirectory(defaultFolder);
            }

            string defaultName = data.sceneName + "_lights.json";
            string path = EditorUtility.SaveFilePanel("Export Scene Lights", defaultFolder, defaultName, "json");
            
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, json);
                Debug.Log($"[WishGI] 成功导出 {data.lights.Count} 个光源及环境光信息到: {path}");
                AssetDatabase.Refresh();
            }
        }

        private static AmbientExportData GetAmbientData()
        {
            var amb = new AmbientExportData();
            amb.mode = RenderSettings.ambientMode.ToString();
            amb.ambientColor = new ColorData(RenderSettings.ambientLight);
            amb.skyColor = new ColorData(RenderSettings.ambientSkyColor);
            amb.equatorColor = new ColorData(RenderSettings.ambientEquatorColor);
            amb.groundColor = new ColorData(RenderSettings.ambientGroundColor);
            amb.ambientIntensity = RenderSettings.ambientIntensity;

            // 尝试获取HDRI贴图路径，如果是Skybox材质
            if (RenderSettings.skybox != null)
            {
                Texture tex = null;
                if (RenderSettings.skybox.HasProperty("_MainTex"))
                    tex = RenderSettings.skybox.GetTexture("_MainTex");
                else if (RenderSettings.skybox.HasProperty("_Tex"))
                    tex = RenderSettings.skybox.GetTexture("_Tex");
                
                if (tex != null)
                {
                    amb.skyboxTexturePath = AssetDatabase.GetAssetPath(tex);
                }
            }

            return amb;
        }

        private static List<LightExportData> GetLightsData()
        {
            var list = new List<LightExportData>();
            Light[] allLights = FindObjectsOfType<Light>();
            
            foreach(var l in allLights)
            {
                // 不导出被禁用或隐藏的灯光
                if (!l.enabled || !l.gameObject.activeInHierarchy) continue;

                var lData = new LightExportData();
                lData.name = l.name;
                lData.type = l.type.ToString();
                
                lData.position = new Vec3Data(l.transform.position);
                lData.direction = new Vec3Data(l.transform.forward);
                
                // 灯光属性
                lData.color = new ColorData(l.color);
                lData.intensity = l.intensity;
                lData.range = l.range;
                lData.spotAngle = l.spotAngle;
                lData.innerSpotAngle = l.innerSpotAngle;
                
                // 仅针对区域光/矩形光（部分管线才有效，比如URP/HDRP可能映射到这些属性）
                lData.areaSizeX = l.areaSize.x;
                lData.areaSizeY = l.areaSize.y;

                list.Add(lData);
            }
            return list;
        }

        private static List<MeshInstanceData> GetMeshInstancesData()
        {
            var list = new List<MeshInstanceData>();
            MeshFilter[] allMeshes = FindObjectsOfType<MeshFilter>();
            
            foreach (var mf in allMeshes)
            {
                if (!mf.gameObject.activeInHierarchy || mf.sharedMesh == null) continue;

                var mData = new MeshInstanceData();
                mData.objectName = mf.name;
                mData.meshPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
                
                mData.position = new Vec3Data(mf.transform.position);
                mData.eulerAngles = new Vec3Data(mf.transform.eulerAngles);
                mData.scale = new Vec3Data(mf.transform.lossyScale);
                
                list.Add(mData);
            }
            return list;
        }
    }
}