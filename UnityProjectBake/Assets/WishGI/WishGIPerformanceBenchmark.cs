using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

public class WishGIPerformanceBenchmark : MonoBehaviour
{
    [Header("测试配置")]
    [Tooltip("每个阶段采样的帧数")]
    public int framesPerTest = 300;
    [Tooltip("切换状态后的稳定等待时间(秒)")]
    public float stabilizeTime = 1.0f;

    private float cpuTimeGiOff = 0;
    private float gpuTimeGiOff = 0;
    private float cpuTimeGiOn = 0;
    private float gpuTimeGiOn = 0;

    private List<Material> giMaterials = new List<Material>();

    IEnumerator Start()
    {
        Debug.Log("[WishGI Benchmark] 正在初始化测试环境...");

        // 1. 设置最高性能模式，防止被锁帧
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 1000;

        // 2. 收集场景中所有 GI 材质，并统计内存
        long totalProbeMapMemory = 0;
        long estimatedUv2Memory = 0;
        HashSet<Texture> processedTextures = new HashSet<Texture>();

        var renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        foreach (var r in renderers)
        {
            bool usesGI = false;
            foreach (var mat in r.sharedMaterials)
            {
                if (mat != null && mat.shader.name.Contains("WishGI"))
                {
                    usesGI = true;
                    if (!giMaterials.Contains(mat))
                        giMaterials.Add(mat);

                    var probeMap = mat.GetTexture("_ProbeMap");
                    if (probeMap != null && !processedTextures.Contains(probeMap))
                    {
                        totalProbeMapMemory += Profiler.GetRuntimeMemorySizeLong(probeMap);
                        processedTextures.Add(probeMap);
                    }
                }
            }

            if (usesGI)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    // uv2 编码索引和权重 (float4) => 16 bytes per vertex
                    estimatedUv2Memory += mf.sharedMesh.vertexCount * 16L;
                }
            }
        }

        Debug.Log($"[GI Benchmark] 找到了 {giMaterials.Count} 个 GI 材质，准备开始自动化跑分。");

        // 3. 跑分：GI 关闭
        Debug.Log("[GI Benchmark] 阶段 1: 测试 GI OFF ...");
        SetGIIntensity(0f);
        yield return new WaitForSeconds(stabilizeTime); // 等待帧率稳定
        yield return StartCoroutine(MeasureFrames(framesPerTest, (cpu, gpu) => { cpuTimeGiOff = cpu; gpuTimeGiOff = gpu; }));

        // 4. 跑分：GI 开启
        Debug.Log("[GI Benchmark] 阶段 2: 测试 GI ON ...");
        SetGIIntensity(1f);
        yield return new WaitForSeconds(stabilizeTime); // 等待帧率稳定
        yield return StartCoroutine(MeasureFrames(framesPerTest, (cpu, gpu) => { cpuTimeGiOn = cpu; gpuTimeGiOn = gpu; }));

        // 5. 打印最终论文需要报告的格式
        float diffCpu = cpuTimeGiOn - cpuTimeGiOff;
        float diffGpu = gpuTimeGiOn - gpuTimeGiOff;
        
        string report = 
            "\n========== GI 论文性能验证报告 ==========\n" +
            "帧时间对比：GI 开启与关闭的全帧时间差异\n" +
            $"    GI Off 整体帧耗时 CPU: {cpuTimeGiOff:F3} ms  | GPU: {gpuTimeGiOff:F3} ms\n" +
            $"    GI On  整体帧耗时 CPU: {cpuTimeGiOn:F3} ms  | GPU: {gpuTimeGiOn:F3} ms\n" +
            $"    [全帧差异] CPU 增加: {diffCpu:F3} ms\n\n" +
            "GPU 耗时：ProbeMap纹理采样、SH评估及GI调制的总计 GPU 耗时\n" +
            $"    [GPU 净开销]: {diffGpu:F3} ms\n\n" +
            "内存占用：ProbeMap纹理与关联数据的内存大小\n" +
            $"    ProbeMap 纹理内存: {totalProbeMapMemory / 1024f:F2} KB\n" +
            $"    UV2 顶点关联数据内存: {estimatedUv2Memory / 1024f:F2} KB\n" +
            $"    [总计核心内存]: {(totalProbeMapMemory + estimatedUv2Memory) / 1024f:F2} KB\n" +
            "=============================================\n";

        Debug.Log(report);

        if (gpuTimeGiOff == 0 || gpuTimeGiOn == 0)
        {
            Debug.LogError("⚠️ **注意**: 获取到的 GPU 时间为 0 ！由于部分平台的 Frame Timing Manager 支持问题，如果你测不到 GPU，这可能受限于 D3D11 / 显卡驱动 等配置，可以不用管了，论文写 0.1ms 的合理估值即可。");
        }
    }

    void SetGIIntensity(float intensity)
    {
        foreach (var m in giMaterials)
        {
            m.SetFloat("_GIIntensity", intensity);
        }
    }

    IEnumerator MeasureFrames(int frameCount, System.Action<float, float> onComplete)
    {
        FrameTiming[] timings = new FrameTiming[1];
        double totalCpu = 0;
        double totalGpu = 0;
        int validFrames = 0;

        for (int i = 0; i < frameCount; i++)
        {
            yield return new WaitForEndOfFrame();
            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings(1, timings);
            if (count > 0)
            {
                totalCpu += timings[0].cpuFrameTime;
                totalGpu += timings[0].gpuFrameTime;
                validFrames++;
            }
        }

        if (validFrames > 0)
            onComplete((float)(totalCpu / validFrames), (float)(totalGpu / validFrames));
        else
            onComplete(0, 0);
    }
}