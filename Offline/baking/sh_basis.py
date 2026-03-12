"""
Evaluate transfer SH basis T(Y) up to order 2 (9 coefficients).

WishGI runtime reconstructs diffuse GI using transfer-convolved SH terms.
To avoid train/runtime mismatch, offline fitting must use the same basis
constants as shader evaluation.
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
	"""Evaluate real SH basis Y_lm for unit directions.

	Args:
		dirs: (N,3) array of unit vectors.
		order: SH order (only <=2 supported).

	Returns:
		(N, (order+1)^2) array of basis values.
	"""
	if dirs.ndim != 2 or dirs.shape[1] != 3:
		raise ValueError("dirs must be (N,3)")
	order = int(order)
	C = num_sh_coeffs(order)
	# normalize directions to be robust to rounding
	dirs = dirs.astype(np.float64)
	norms = np.linalg.norm(dirs, axis=1, keepdims=True) + 1e-8
	dirs = dirs / norms
	x = dirs[:, 0]
	y = dirs[:, 1]
	z = dirs[:, 2]

	# Diffuse transfer-convolved constants (must match WishGI_Eval.hlsl / WishGIProbe.hlsl)
	c0 = 0.28209479177387814
	c1 = 0.32573500793527993
	c2 = 0.2731371076480198
	c3 = 0.07884789131313001
	c4 = 0.1365685538240099

	vals = np.zeros((dirs.shape[0], C), dtype=np.float64)
	idx = 0
	# L0
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

