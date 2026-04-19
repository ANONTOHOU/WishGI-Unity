#ifndef WISHGI_EVAL_INCLUDED
#define WISHGI_EVAL_INCLUDED

// SH basis constants for L=2 (pre-multiplied by cosine convolution A_l / pi for diffuse irradiance)
static const float c0 = 0.28209479177387814;
static const float c1 = 0.32573500793527993; // 0.4886025 * (2/3)
static const float c2 = 0.2731371076480198;  // 1.0925484 * 0.25
static const float c3 = 0.07884789131313001; // 0.3153915 * 0.25
static const float c4 = 0.1365685538240099;  // 0.5462742 * 0.25

// Evaluate L=2 Spherical Harmonics with given coefficients and direction
float3 EvalSH_L2(float3 coeffs0[9], float3 coeffs1[9], float w0, float w1, float3 dir) {
    // dir 取表面法线方向，输出漫反射近似辐照度。
    float Y[9];
    Y[0] = c0;
    Y[1] = c1 * dir.y;
    Y[2] = c1 * dir.z;
    Y[3] = c1 * dir.x;
    Y[4] = c2 * dir.x * dir.y;
    Y[5] = c2 * dir.y * dir.z;
    Y[6] = c3 * (3.0 * dir.z * dir.z - 1.0);
    Y[7] = c2 * dir.x * dir.z;
    Y[8] = c4 * (dir.x * dir.x - dir.y * dir.y);

    float3 col = float3(0, 0, 0);
    for (int i = 0; i < 9; i++) {
        float3 combinedCoeffs = w0 * coeffs0[i] + w1 * coeffs1[i];
        col += combinedCoeffs * Y[i];
    }
    return max(col, 0.0); // Prevent negative lights
}

void WishGI_Custom_float_float(float3 normalWS, float4 assoc, UnityTexture2D probeMap, float probeCount, float texelsPerProbe, out float3 Out) {
    // assoc.xyzw: [i0_norm, w0, i1_norm, w1]
    // i0_norm / i1_norm 为 [0,1] 归一化索引，这里乘以 (probeCount-1) 反解。
    int i0 = (int)round(assoc.x * (probeCount - 1.0));
    int i1 = (int)round(assoc.z * (probeCount - 1.0));
    float w0 = assoc.y;
    float w1 = assoc.w;
    
    int _TexelsPerProbe = (int)texelsPerProbe;
    int _ProbeCount = (int)probeCount;
    int width = _ProbeCount * _TexelsPerProbe;
    float invWidth = 1.0 / (float)width;

    float coeffs0_raw[28];
    float coeffs1_raw[28];

    // Read texels for i0。
    // 用 texel center 采样（+0.5）并配合 Point 过滤，避免跨 probe 混色。
    int baseTexel0 = i0 * _TexelsPerProbe;
    for (int t0 = 0; t0 < _TexelsPerProbe; t0++) {
        float u = (baseTexel0 + t0 + 0.5) * invWidth;
        float4 tex = SAMPLE_TEXTURE2D_LOD(probeMap.tex, probeMap.samplerstate, float2(u, 0.5), 0);
        int basef = t0 * 4;
        coeffs0_raw[basef] = tex.r;
        coeffs0_raw[basef + 1] = tex.g;
        coeffs0_raw[basef + 2] = tex.b;
        coeffs0_raw[basef + 3] = tex.a;
    }

    // Read texels for i1
    int baseTexel1 = i1 * _TexelsPerProbe;
    for (int t1 = 0; t1 < _TexelsPerProbe; t1++) {
        float u = (baseTexel1 + t1 + 0.5) * invWidth;
        float4 tex = SAMPLE_TEXTURE2D_LOD(probeMap.tex, probeMap.samplerstate, float2(u, 0.5), 0);
        int basef = t1 * 4;
        coeffs1_raw[basef] = tex.r;
        coeffs1_raw[basef + 1] = tex.g;
        coeffs1_raw[basef + 2] = tex.b;
        coeffs1_raw[basef + 3] = tex.a;
    }

    // Unpack coeffs into array of 9 float3（L2 共 9 项）
    float3 coeffs0[9];
    float3 coeffs1[9];
    for (int i = 0; i < 9; i++) {
        int basef = i * 3;
        coeffs0[i] = float3(coeffs0_raw[basef], coeffs0_raw[basef+1], coeffs0_raw[basef+2]);
        coeffs1[i] = float3(coeffs1_raw[basef], coeffs1_raw[basef+1], coeffs1_raw[basef+2]);
    }

    Out = EvalSH_L2(coeffs0, coeffs1, w0, w1, normalWS);
}

#endif
