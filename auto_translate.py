# -*- coding: utf-8 -*-
import os
import re

replacements = {
    # .py files
    '拟合球谐函数': '拟合球谐函数',
    'samples_pt.json (每个探针的路径追踪 RGB 采样 + 采样方向)': 'samples_pt.json (每个探针的路径追踪 RGB 采样 + 采样方向)',
    'sampe_weights.json (用于蒙特卡洛积分的维诺面积权重)': 'sampe_weights.json (用于蒙特卡洛积分的维诺面积权重)',
    'probes_sh.npy (P x C x 3) 和 probes_sh.json': 'probes_sh.npy (P x C x 3) 和 probes_sh.json',
    'SH阶数 0-2 = 9 个系数': 'SH阶数 0-2 = 9 个系数',
    '使用岭回归避免因覆盖缺失导致的矩阵奇异性': '使用岭回归避免因覆盖缺失导致的矩阵奇异性',
    '使用 K-Medoids 算法在导航网格或静态网格上分布探针。': '使用 K-Medoids 算法在导航网格或静态网格上分布探针。',
    '计算测地线(Geodesic)或欧几里得(Euclidean)距离': '计算测地线(Geodesic)或欧几里得(Euclidean)距离',
    '用于导入已烘焙 GI(全局光照)纹理的 Unity 编辑器窗口': '用于导入已烘焙 GI(全局光照)纹理的 Unity 编辑器窗口',
    '将元数据(Meta Data)和权重信息写入 UV2 发送给着色器(Shader)': '将元数据(Meta Data)和权重信息写入 UV2 发送给着色器(Shader)',
    '读取 .npy 数据为 Unity Texture2D': '读取 .npy 数据为 Unity Texture2D'
}

def translate_file(path):
    if not os.path.exists(path): return
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    original = content
    for old, new in replacements.items():
        if old in content:
            content = content.replace(old, new)
            
    # Generic replacements using Regex
    content = re.sub(r'# (todo|TODO):(.*)', r'# [待办]: \2', content)
    content = re.sub(r'// (todo|TODO):(.*)', r'// [待办]: \2', content)
            
    if original != content:
        with open(path, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"[{path}] 已翻译")

def scan_and_translate(root_dir):
    for root, _, files in os.walk(root_dir):
        if '.git' in root or 'Library' in root or 'Logs' in root:
            continue
        for file in files:
            if file.endswith(('.py', '.cs', '.hlsl', '.shader')):
                translate_file(os.path.join(root, file))

scan_and_translate('.')
print('全部批量翻译完成！')
