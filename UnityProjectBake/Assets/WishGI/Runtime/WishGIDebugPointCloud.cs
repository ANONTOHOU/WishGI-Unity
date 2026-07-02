using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class WishGIDebugPointCloud : MonoBehaviour
{
    [SerializeField] private List<Vector3> samplePoints = new List<Vector3>();
    [SerializeField] private List<Vector3> probePoints = new List<Vector3>();

    [Header("Draw Options")]
    [SerializeField] private bool drawSamples = true;
    [SerializeField] private bool drawProbes = true;
    [SerializeField] private bool drawOnlyWhenSelected = true;

    [Header("Visual")]
    [SerializeField] private Color sampleColor = new Color(0.72f, 0.72f, 0.72f, 0.80f);
    [SerializeField] private Color probeColor = new Color(0.90f, 0.20f, 0.20f, 1.00f);
    [SerializeField] private float sampleSize = 0.03f;
    [SerializeField] private float probeSize = 0.10f;

    public int SampleCount
    {
        get { return samplePoints != null ? samplePoints.Count : 0; }
    }

    public int ProbeCount
    {
        get { return probePoints != null ? probePoints.Count : 0; }
    }

    public void SetPoints(List<Vector3> samples, List<Vector3> probes)
    {
        samplePoints = samples != null ? new List<Vector3>(samples) : new List<Vector3>();
        probePoints = probes != null ? new List<Vector3>(probes) : new List<Vector3>();
    }

    public void ClearPoints()
    {
        if (samplePoints == null) samplePoints = new List<Vector3>();
        if (probePoints == null) probePoints = new List<Vector3>();

        samplePoints.Clear();
        probePoints.Clear();
    }

    private void OnDrawGizmos()
    {
        if (!drawOnlyWhenSelected)
        {
            DrawPointCloud();
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (drawOnlyWhenSelected)
        {
            DrawPointCloud();
        }
    }

    private void DrawPointCloud()
    {
        if (drawSamples && samplePoints != null)
        {
            Gizmos.color = sampleColor;
            for (int i = 0; i < samplePoints.Count; i++)
            {
                Gizmos.DrawSphere(samplePoints[i], sampleSize);
            }
        }

        if (drawProbes && probePoints != null)
        {
            Gizmos.color = probeColor;
            for (int i = 0; i < probePoints.Count; i++)
            {
                Gizmos.DrawSphere(probePoints[i], probeSize);
            }
        }
    }
}