"""
将探针 SH 系数打包为 RGBA 浮点纹理布局（1D 条带）。

输入：由 fit_sh.py 生成的 probes_sh.npy（形状：P x C x 3，C=(order+1)^2）
输出：形状为 (1, width, 4) 的 .npy 纹理数组，其中 width = P * texels_per_probe。
同时可额外导出一个用于 Unity 导入/对照的小型元数据 JSON。

用法（在仓库根目录执行）：

python Offline/baking/pack_probes.py `
    --probes-npy Data/probes/probes_sh.npy `
    --order 2 `
    --output-tex Data/probes/probe_map.npy `
    --output-meta Data/probes/probe_map_meta.json

纹理布局：
texels_per_probe = ceil((C*3)/4)。当 order=2（C=9）时，每个探针占 7 个 texel。
每个探针的展平顺序为：[c0.r, c0.g, c0.b, c1.r, c1.g, c1.b, ...]，不足 4 浮点的块用 0 填充。
第 p 个探针在 X 轴上占据区间 [p*texels_per_probe, (p+1)*texels_per_probe-1]，Y=0。
"""

from __future__ import annotations

import argparse
import json
import math
import os
from typing import Tuple

import numpy as np


def infer_order(num_basis: int) -> int:
    """
        根据基函数数量反推 SH 阶数。
        例如：num_basis=9 时返回 order=2。
        反推失败时抛错，避免错误的阶数进入后续打包流程。
    """
    r = int(round(math.sqrt(num_basis)))
    if r * r != num_basis:
        raise ValueError(f"Cannot infer SH order from basis count={num_basis}")
    return r - 1


def pack_coeffs_to_texture(coeffs: np.ndarray, order: int) -> Tuple[np.ndarray, dict]:
    """将形状 (P, C, 3) 的系数打包到形状 (1, width, 4) 的纹理条带。"""
    if coeffs.ndim != 3 or coeffs.shape[2] != 3:
        raise ValueError(f"coeffs must be (P, C, 3); got {coeffs.shape}")

    num_probes, num_basis, _ = coeffs.shape
    if (order + 1) * (order + 1) != num_basis:
        raise ValueError(f"order mismatch: order={order} implies {(order+1)**2} basis, got {num_basis}")

    floats_per_probe = num_basis * 3
    texels_per_probe = math.ceil(floats_per_probe / 4)
    width = texels_per_probe * num_probes
    height = 1

    tex = np.zeros((height, width, 4), dtype=np.float32)
    flat = coeffs.reshape(num_probes, floats_per_probe)

    for p in range(num_probes):
        for t in range(texels_per_probe):
            base_f = t * 4
            src_slice = flat[p, base_f : base_f + 4]
            dst_x = p * texels_per_probe + t
            tex[0, dst_x, : len(src_slice)] = src_slice

    meta = {
        "order": order,
        "num_probes": int(num_probes),
        "num_basis": int(num_basis),
        "floats_per_probe": int(floats_per_probe),
        "texels_per_probe": int(texels_per_probe),
        "width": int(width),
        "height": int(height),
        "layout": "probe p occupies texels [p*texels_per_probe, (p+1)*texels_per_probe-1] on X, Y=0",
        "flatten_order": "[c0.r, c0.g, c0.b, c1.r, c1.g, c1.b, ...], padded to 4 floats per texel",
    }
    return tex, meta


def parse_args():
    """定义命令行参数。

    参数命名与离线管线其它脚本保持一致，便于在工具链中拼接命令。
    """
    parser = argparse.ArgumentParser(description="Pack probe SH coeffs into RGBA float texture strip (.npy)")
    parser.add_argument("--probes-npy", required=True, help="Path to probes_sh.npy from fit_sh.py (shape: P x C x 3)")
    parser.add_argument("--order", type=int, default=None, help="SH order; if omitted, inferred from C")
    parser.add_argument("--output-tex", required=True, help="Output .npy texture file (shape (1, width, 4))")
    parser.add_argument("--output-meta", default=None, help="Optional metadata JSON for importer")
    return parser.parse_args()


def main():
    """脚本入口：读取系数、打包纹理并输出元数据。"""
    args = parse_args()

    # probes_sh.npy 由 fit_sh.py 产生，约定形状为 (P, C, 3)。
    coeffs = np.load(args.probes_npy)
    if coeffs.ndim != 3:
        raise ValueError(f"Expected coeffs rank-3 (P,C,3); got {coeffs.shape}")
    # 如果未显式传入 order，则根据基函数数自动推断，减少手工配置错误。
    num_basis = coeffs.shape[1]
    order = args.order if args.order is not None else infer_order(num_basis)

    # 执行布局打包：把每个 probe 的 27 个浮点（L2）连续写入 RGBA 条带纹理。
    tex, meta = pack_coeffs_to_texture(coeffs, order)

    out_dir = os.path.dirname(args.output_tex)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    # 统一保存为 float32，匹配 Unity 侧 RGBAFloat 读取与内存预期。
    np.save(args.output_tex, tex.astype(np.float32))
    if args.output_meta:
        with open(args.output_meta, "w", encoding="utf-8") as f:
            json.dump(meta, f, indent=2)
    print(f"[pack_probes] Probes={meta['num_probes']}, order={order}, texels/probe={meta['texels_per_probe']}, width={meta['width']}")
    print(f"[pack_probes] Saved texture npy -> {args.output_tex}" + (f" and meta -> {args.output_meta}" if args.output_meta else ""))


if __name__ == "__main__":
    main()
