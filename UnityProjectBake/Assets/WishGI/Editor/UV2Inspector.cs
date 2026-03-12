using System;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Quick inspector to verify uv2 was written with probe associations.
/// - Select a Mesh asset and set probeCount (from meta).
/// - Inspect will print min/max and first few entries decoded to indices/weights.
/// </summary>
public class UV2Inspector : EditorWindow
{
    private Mesh mesh;
    private int probeCount = 128;
    private int sampleCount = 8;

    [MenuItem("WishGI/UV2 Inspector")]
    public static void ShowWindow()
    {
        GetWindow<UV2Inspector>(true, "WishGI UV2 Inspector", true);
    }

    private void OnGUI()
    {
        mesh = (Mesh)EditorGUILayout.ObjectField("Mesh", mesh, typeof(Mesh), false);
        probeCount = EditorGUILayout.IntField("Probe Count", probeCount);
        sampleCount = EditorGUILayout.IntSlider("Print First N", sampleCount, 1, 32);

        if (GUILayout.Button("Inspect uv2"))
        {
            Inspect();
        }
    }

    private void Inspect()
    {
        if (mesh == null)
        {
            ShowNotification(new GUIContent("Assign Mesh"));
            return;
        }
        if (probeCount <= 1)
        {
            ShowNotification(new GUIContent("Probe count must be >1"));
            return;
        }

        var uv2 = new System.Collections.Generic.List<Vector4>();
        mesh.GetUVs(1, uv2); // channel 1 is uv2
        if (uv2 == null || uv2.Count != mesh.vertexCount)
        {
            Debug.LogWarning($"uv2 missing or size mismatch. verts={mesh.vertexCount}, uv2={(uv2 == null ? 0 : uv2.Count)}");
            return;
        }

        float minI0 = float.MaxValue, maxI0 = float.MinValue;
        float minI1 = float.MaxValue, maxI1 = float.MinValue;
        float minW0 = float.MaxValue, maxW0 = float.MinValue;
        float minW1 = float.MaxValue, maxW1 = float.MinValue;

        int n = uv2.Count;
        sampleCount = Mathf.Clamp(sampleCount, 1, n);
        var sb = new StringBuilder();
        sb.AppendLine($"Inspect Mesh={mesh.name}, verts={n}, probeCount={probeCount}");
        sb.AppendLine("idx: i0_norm i1_norm -> i0 i1 | w0 w1");

        for (int i = 0; i < n; i++)
        {
            var u = uv2[i];
            minI0 = Mathf.Min(minI0, u.x); maxI0 = Mathf.Max(maxI0, u.x);
            minI1 = Mathf.Min(minI1, u.z); maxI1 = Mathf.Max(maxI1, u.z);
            minW0 = Mathf.Min(minW0, u.y); maxW0 = Mathf.Max(maxW0, u.y);
            minW1 = Mathf.Min(minW1, u.w); maxW1 = Mathf.Max(maxW1, u.w);

            if (i < sampleCount)
            {
                int i0 = Mathf.RoundToInt(u.x * (probeCount - 1));
                int i1 = Mathf.RoundToInt(u.z * (probeCount - 1));
                sb.AppendLine($"{i}: {u.x:F3} {u.z:F3} -> {i0} {i1} | {u.y:F3} {u.w:F3}");
            }
        }

        sb.AppendLine($"i0_norm min/max: {minI0:F3}/{maxI0:F3}");
        sb.AppendLine($"i1_norm min/max: {minI1:F3}/{maxI1:F3}");
        sb.AppendLine($"w0 min/max: {minW0:F3}/{maxW0:F3}");
        sb.AppendLine($"w1 min/max: {minW1:F3}/{maxW1:F3}");

        Debug.Log(sb.ToString());
        ShowNotification(new GUIContent("uv2 inspected; see Console"));
    }
}
