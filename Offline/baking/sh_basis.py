"""
计算最高到 2 阶（9 个系数）的传输 SH 基函数 T(Y)。

WishGI 运行时使用与传输卷积后的 SH 项重建漫反射 GI。
为避免离线拟合与运行时结果不一致，离线端必须使用与着色器
评估完全一致的基函数常量。
"""

from __future__ import annotations

import numpy as np


def num_sh_coeffs(order: int) -> int:
	if order < 0:
		raise ValueError("order must be non-negative")
	if order > 2:
		raise ValueError("order>2 not implemented; extend sh_basis.py if needed")
	return (order + 1) * (order + 1)


def eval_sh_basis(dirs: np.ndarray, order: int) -> np.ndarray:
	"""为单位方向向量计算实 SH 基函数 Y_lm。

	参数：
		dirs: 形状为 (N,3) 的单位向量数组。
		order: SH 阶数（当前仅支持 <=2）。

	返回：
		形状为 (N, (order+1)^2) 的基函数值数组。
	"""
	if dirs.ndim != 2 or dirs.shape[1] != 3:
		raise ValueError("dirs must be (N,3)")
	order = int(order)
	C = num_sh_coeffs(order)
	# 归一化方向，提升对数值误差的鲁棒性
	dirs = dirs.astype(np.float64)
	norms = np.linalg.norm(dirs, axis=1, keepdims=True) + 1e-8
	dirs = dirs / norms
	x = dirs[:, 0]
	y = dirs[:, 1]
	z = dirs[:, 2]

	# 漫反射传输卷积常量（必须与 WishGI_Eval.hlsl / WishGIProbe.hlsl 保持一致）
	c0 = 0.28209479177387814
	c1 = 0.32573500793527993
	c2 = 0.2731371076480198
	c3 = 0.07884789131313001
	c4 = 0.1365685538240099

	vals = np.zeros((dirs.shape[0], C), dtype=np.float64)
	idx = 0
	# L0 项
	vals[:, idx] = c0
	idx += 1
	if order >= 1:
		vals[:, idx] = c1 * y; idx += 1
		vals[:, idx] = c1 * z; idx += 1
		vals[:, idx] = c1 * x; idx += 1
	if order >= 2:
		vals[:, idx] = c2 * x * y; idx += 1
		vals[:, idx] = c2 * y * z; idx += 1
		vals[:, idx] = c3 * (3.0 * z * z - 1.0); idx += 1
		vals[:, idx] = c2 * x * z; idx += 1
		vals[:, idx] = c4 * (x * x - y * y); idx += 1
	return vals

