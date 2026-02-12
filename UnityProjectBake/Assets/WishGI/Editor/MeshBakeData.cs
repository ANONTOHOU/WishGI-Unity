using System;
using System.Collections.Generic;
using UnityEngine;

namespace WishGI.Baking
{
    /// <summary>
    /// 用于存储一组可离线烘焙的网格数据。
    /// 可直接作为 ScriptableObject 资产保存。
    /// </summary>
    [CreateAssetMenu(fileName = "MeshBakeData", menuName = "WishGI/Mesh Bake Data", order = 0)]
    public class MeshBakeData : ScriptableObject
    {
        public List<MeshObjectData> meshObjects = new List<MeshObjectData>();
    }

    [Serializable]
    public class MeshObjectData
    {
        public string name;
        public int instanceId;                    // 便于回溯到场景对象
        public float[] localToWorld = new float[16]; // 以列主序存储 4x4 矩阵
        public Vector3[] positions;
        public Vector3[] normals;
        public Vector2[] uv2;
        public int[] indices;
    }

    /// <summary>
    /// Json 容器（JsonUtility 需要一个可序列化的根）
    /// </summary>
    [Serializable]
    public class MeshBakeDataJson
    {
        public List<MeshObjectData> meshObjects = new List<MeshObjectData>();
    }

    public static class MatrixUtility
    {
        public static float[] ToFloatArray(Matrix4x4 m)
        {
            return new float[]
            {
                m.m00, m.m10, m.m20, m.m30,
                m.m01, m.m11, m.m21, m.m31,
                m.m02, m.m12, m.m22, m.m32,
                m.m03, m.m13, m.m23, m.m33
            };
        }
    }
}