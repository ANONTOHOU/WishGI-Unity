import os

def replace_in_file(path, old, new):
    if not os.path.exists(path): return False
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    if old in content:
        content = content.replace(old, new)
        with open(path, 'w', encoding='utf-8') as f:
            f.write(content)
        return True
    return False

# loss.py
replace_in_file('Offline/baking/loss.py', 
    '\"\"\"Small helpers for SH least-squares solving.\"\"\"', 
    '\"\"\"为球谐函数(SH)最小二乘求解提供的小型辅助函数。\"\"\"')

replace_in_file('Offline/baking/loss.py',
    '# Fall back to least-squares on the regularized normal system for near-singular cases.',
    '# 对于近乎奇异矩阵的情况，退而使用正则化法线系统的最小二乘法。')

replace_in_file('Offline/baking/loss.py',
    '\"\"\"Solve (A^T A + λI)x = A^T b for multiple RHS columns.\n\n\tArgs:\n\t\tA: (M,N) design matrix.\n\t\tb: (M,K) target matrix (e.g., RGB).\n\t\tlambda_reg: non-negative ridge weight.\n\n\tReturns:\n\t\t(N,K) solution matrix.\n\t\"\"\"',
    '\"\"\"对多个 RHS 列求解 (A^T A + λI)x = A^T b。\n\n\t参数:\n\t\tA: (M,N) 维度的设计矩阵。\n\t\tb: (M,K) 维度的目标矩阵（如 RGB）。\n\t\tlambda_reg: 非负岭回归权重。\n\n\t返回:\n\t\t(N,K) 解矩阵。\n\t\"\"\"')

# sh_basis.py
replace_in_file('Offline/baking/sh_basis.py',
    '\"\"\"\nEvaluate transfer SH basis T(Y) up to order 2 (9 coefficients).\n\nMyGI runtime reconstructs diffuse GI using transfer-convolved SH terms.\nTo avoid train/runtime mismatch, offline fitting must use the same basis\nconstants as shader evaluation.\n\"\"\"',
    '\"\"\"\n计算最高为阶数 2（9 个系数）的传输球谐基底 T(Y)。\n\nMyGI 运行时使用传输卷积 SH 项重建漫反射全局光照。\n为避免训练与运行时不匹配，离线拟合必须使用与着色器评估相同的基常数。\n\"\"\"')

replace_in_file('Offline/baking/sh_basis.py',
    '\"\"\"Evaluate real SH basis Y_lm for unit directions.\n\n\tArgs:\n\t\tdirs: (N,3) array of unit vectors.\n\t\torder: SH order (only <=2 supported).\n\n\tReturns:\n\t\t(N, (order+1)^2) array of basis values.\n\t\"\"\"',
    '\"\"\"计算单位方向的实数球谐基底 Y_lm。\n\n\t参数:\n\t\tdirs: (N,3) 单位向量数组。\n\t\torder: 球谐阶数（仅支持 <=2）。\n\n\t返回:\n\t\t(N, (order+1)^2) 基底值数组。\n\t\"\"\"')

replace_in_file('Offline/baking/sh_basis.py',
    '# normalize directions to be robust to rounding',
    '# 对方向归一化，增强对舍入误差的鲁棒性')

replace_in_file('Offline/baking/sh_basis.py',
    '# Diffuse transfer-convolved constants (must match MyGI_Eval.hlsl / MyGIProbe.hlsl)',
    '# 漫反射传输卷积常数（必须与 MyGI_Eval.hlsl / MyGIProbe.hlsl 匹配）')

# pack_probes.py
replace_in_file('Offline/baking/pack_probes.py',
    '\"\"\"Pack (P, C, 3) coeffs into (1, width, 4) texture strip.\"\"\"',
    '\"\"\"将 (P, C, 3) 形状的系数打包进 (1, width, 4) 纹理带。\"\"\"')

replace_in_file('Offline/baking/pack_probes.py',
    '# zero init',
    '# 初始化为 0')

print('Translation applied successfully!')
