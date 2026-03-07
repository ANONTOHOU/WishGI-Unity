// WishGI probe sampling helpers for URP
// Functions:
//   EvalSH9_L2(float3 dir) -> float3x3 basis (as float3[9])
//   FetchProbeCoeffs(sampler2D tex, float texelsPerProbe, float probeCount, int probeIndex) -> float3 coeffs[9]
//   SampleWishGI(...) -> float3 emissive color from uv2 weights and normal

#ifndef WISHGI_PROBE_HLSL_INCLUDED
#define WISHGI_PROBE_HLSL_INCLUDED

static const float c0 = 0.282095;
static const float c1 = 0.488603;
static const float c2 = 1.092548;
static const float c3 = 0.315392;
static const float c4 = 0.546274;

inline void EvalSH9_L2(float3 dir, out float basis[9])
{
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
    const int floatsPerProbe = 27; // L2, 9 coeffs * RGB
    float width = max(1.0, probeCount * texelsPerProbe);
    float baseX = probeIndex * texelsPerProbe;
    float3 tmp[9];

    // zero init
    for (int k = 0; k < 9; k++) tmp[k] = 0;

    // read 7 texels (texelsPerProbe expected 7 for L2)
    int f = 0;
    // texelsPerProbe is small (<=7), explicit bounded loop without unroll/loop attributes
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
