"""SH 最小二乘求解所需的小型辅助函数。"""

from __future__ import annotations

import numpy as np


def solve_ridge(A: np.ndarray, b: np.ndarray, lambda_reg: float) -> np.ndarray:
	"""对多列右端项求解 (A^T A + λI)x = A^T b。

	参数：
		A: 形状为 (M,N) 的设计矩阵。
		b: 形状为 (M,K) 的目标矩阵（例如 RGB）。
		lambda_reg: 非负的岭回归权重。

	返回：
		形状为 (N,K) 的解矩阵。
	"""
	A = np.asarray(A, dtype=np.float64)
	b = np.asarray(b, dtype=np.float64)
	if A.ndim != 2 or b.ndim != 2 or A.shape[0] != b.shape[0]:
		raise ValueError("A must be (M,N) and b must be (M,K) with matching rows")
	if lambda_reg < 0:
		raise ValueError("lambda_reg must be >= 0")
	ATA = A.T @ A
	if lambda_reg > 0:
		ATA += lambda_reg * np.eye(ATA.shape[0], dtype=np.float64)
	ATb = A.T @ b
	try:
		return np.linalg.solve(ATA, ATb)
	except np.linalg.LinAlgError:
		# 对接近奇异的情况，回退到正则化正规方程的最小二乘解。
		x, *_ = np.linalg.lstsq(ATA, ATb, rcond=None)
		return x

