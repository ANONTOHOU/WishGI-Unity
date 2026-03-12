"""Small helpers for SH least-squares solving."""

from __future__ import annotations

import numpy as np


def solve_ridge(A: np.ndarray, b: np.ndarray, lambda_reg: float) -> np.ndarray:
	"""Solve (A^T A + λI)x = A^T b for multiple RHS columns.

	Args:
		A: (M,N) design matrix.
		b: (M,K) target matrix (e.g., RGB).
		lambda_reg: non-negative ridge weight.

	Returns:
		(N,K) solution matrix.
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
		# Fall back to least-squares on the regularized normal system for near-singular cases.
		x, *_ = np.linalg.lstsq(ATA, ATb, rcond=None)
		return x

