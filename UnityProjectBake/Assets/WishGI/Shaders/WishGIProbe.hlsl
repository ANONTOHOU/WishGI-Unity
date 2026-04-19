// URP 中用于 WishGI 探测器采样的辅助函数
// 函数：
//   EvalSH9_L2(float3 dir) -> 浮点数 3×3 基础矩阵（以 float3[9] 形式返回）
//   FetchProbeCoeffs(sampler2D tex, float texelsPerProbe, float probeCount, int probeIndex) -> 浮点数 3×9 系数（以 float3[9] 形式返回）
//   SampleWishGI(...) -> 根据 uv2 权重和法线返回浮点数 3×1 发射色

#ifndef WISHGI_PROBE_HLSL_INCLUDED
#define WISHGI_PROBE_HLSL_INCLUDED

// L=2 的 SH 基常量（已乘以漫反射卷积）
static const float c0 = 0.28209479177387814;
static const float c1 = 0.32573500793527993;
static const float c2 = 0.2731371076480198;
static const float c3 = 0.07884789131313001;
static const float c4 = 0.1365685538240099;

inline void EvalSH9_L2(float3 dir, out float basis[9])
{
    // 与离线 sh_basis.py 完全一致的基函数排列，避免离线/运行时不匹配。
    dir = normalize(dir);
    basis[0] = c0;
    basis[1] = c1 * dir.y;
    basis[2] = c1 * dir.z;
    basis[3] = c1 * dir.x;
    basis[4] = c2 * dir.x * dir.y;
    basis[5] = c2 * dir.y * dir.z;
    basis[6] = c3 * (3.0 * dir.z * dir.z - 1.0);
    basis[7] = c2 * dir.x * dir.z;
    basis[8] = c4 * (dir.x * dir.x - dir.y * dir.y);
}

inline void FetchProbeCoeffs(TEXTURE2D_PARAM(probeTex, samplerProbeTex), float texelsPerProbe, float probeCount, int probeIndex, out float3 coeffs[9])
{
    // L2 阶固定 9 个系数，每个系数 RGB 三通道，共 27 个 float。
    const int floatsPerProbe = 27; // L2, 9 coeffs * RGB
    float width = max(1.0, probeCount * texelsPerProbe);
    float baseX = probeIndex * texelsPerProbe;
    float3 tmp[9];

    // 初始化置零
    for (int k = 0; k < 9; k++) tmp[k] = 0;

    // 读取7个纹理单元（texelsPerProbe预期为7用于L2）
    int f = 0;
    // texelsPerProbe较小（<=7），显式有界循环，无需unroll/loop属性
    for (int t = 0; t < 7; t++)
    {
        float2 uv = float2((baseX + t + 0.5) / width, 0.5);
        float4 s = SAMPLE_TEXTURE2D_LOD(probeTex, samplerProbeTex, uv, 0);
        if (f < floatsPerProbe) tmp[f / 3][f % 3] = s.r; f++;
        if (f < floatsPerProbe) tmp[f / 3][f % 3] = s.g; f++;
        if (f < floatsPerProbe) tmp[f / 3][f % 3] = s.b; f++;
        if (f < floatsPerProbe) tmp[f / 3][f % 3] = s.a; f++;
    }

    for (int k = 0; k < 9; k++) coeffs[k] = tmp[k];
}

inline float3 EvalProbe(TEXTURE2D_PARAM(probeTex, samplerProbeTex), float texelsPerProbe, float probeCount, int probeIndex, float3 dir)
{
    // 读取单个 probe 的 SH 系数并沿指定方向求值。
    float3 coeffs[9];
    FetchProbeCoeffs(probeTex, samplerProbeTex, texelsPerProbe, probeCount, probeIndex, coeffs);
    float basis[9];
    EvalSH9_L2(dir, basis);
    float3 c = 0;
    [unroll] for (int k = 0; k < 9; k++) c += coeffs[k] * basis[k];
    return c;
}

inline float3 SampleWishGI(TEXTURE2D_PARAM(probeTex, samplerProbeTex), float texelsPerProbe, float probeCount, float4 uv2, float3 normalWS)
{
    // uv2 编码约定：x,z 为归一化探针索引；y,w 为对应权重。
    int i0 = (int)round(uv2.x * (probeCount - 1));
    int i1 = (int)round(uv2.z * (probeCount - 1));
    float w0 = uv2.y;
    float w1 = uv2.w;
    float3 n = normalize(normalWS);
    float3 c0 = EvalProbe(probeTex, samplerProbeTex, texelsPerProbe, probeCount, i0, n);
    float3 c1 = EvalProbe(probeTex, samplerProbeTex, texelsPerProbe, probeCount, i1, n);
    return c0 * w0 + c1 * w1;
}

#endif // WISHGI_PROBE_HLSL_INCLUDED
