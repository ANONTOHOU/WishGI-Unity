"""
根据采样辐亮度与 sample→probe 权重，为每个探针拟合 SH 系数。

用法（在仓库根目录执行）：

python Offline/baking/fit_sh.py \
	--samples-json Data/samples/SampleScene_samples_pt.json \
	--sample-weights Data/probes/sample_weights.json \
	--order 2 \
	--lambda-reg 1e-4 \
	--output-npy Data/probes/probes_sh.npy \
	--output-json Data/probes/probes_sh.json \
	--dirs-npy Data/samples/SampleScene_dirs.npy

计算 SH 基函数必须提供方向集。可通过 --dirs-npy 传入，或在新版采样器
导出的 samples.json 中使用 dirsLocal 字段内嵌方向。若两者都不存在，
程序会中止，避免在未知方向上进行拟合。
"""

from __future__ import annotations

import argparse
import json
import os
from dataclasses import dataclass
from typing import Dict, List, Tuple

import numpy as np

from loss import solve_ridge
from sh_basis import eval_sh_basis, num_sh_coeffs


@dataclass
class SampleRadiance:
	sample_id: int
	radiance: np.ndarray  # 形状：(num_dirs, 3)
	normal: np.ndarray    # 形状：(3,)


def load_samples(path: str) -> Tuple[List[SampleRadiance], np.ndarray | None, int]:
	"""
		读取采样结果，并提取每个采样点的逐方向辐亮度与法线。
		samples: 每个采样点的辐亮度样本。
		dirs_from_file: 若 JSON 内嵌了 dirsLocal，则返回其数组，否则为 None。
		num_dirs: JSON 声明的方向数（用于一致性校验）。
	"""
	with open(path, "r", encoding="utf-8") as f:
		data = json.load(f)
	samples_json = data.get("samples", []) if isinstance(data, dict) else data
	dirs_from_file = None
	if isinstance(data, dict) and "dirsLocal" in data:
		dirs_from_file = np.array(
			[[float(d.get("x", 0.0)), float(d.get("y", 0.0)), float(d.get("z", 0.0))] for d in data["dirsLocal"]],
			dtype=np.float64,
		)
	num_dirs = int(data.get("numDirections", 0)) if isinstance(data, dict) else 0
	out: List[SampleRadiance] = []
	for i, s in enumerate(samples_json):
		rad = s.get("radiancePerDir") or s.get("radiance_per_dir")
		if rad is None:
			raise ValueError(f"Sample {i} missing radiancePerDir")
		rad_np = np.asarray(rad, dtype=np.float64)
		if rad_np.ndim != 2 or rad_np.shape[1] != 3:
			raise ValueError(f"Sample {i} radiance shape invalid: {rad_np.shape}")
		sample_id = int(s.get("sample_id", i))
		norm_np = np.array([0., 1., 0.], dtype=np.float64)
		if "normal" in s:
			nd = s["normal"]
			if isinstance(nd, dict):
				norm_np = np.array([float(nd.get("x", 0)), float(nd.get("y", 1)), float(nd.get("z", 0))], dtype=np.float64)
			else:
				norm_np = np.asarray(nd, dtype=np.float64)
			n_len = np.linalg.norm(norm_np)
			if n_len > 1e-6:
				norm_np /= n_len
		out.append(SampleRadiance(sample_id=sample_id, radiance=rad_np, normal=norm_np))
	return out, dirs_from_file, num_dirs


def load_sample_weights(path: str) -> Tuple[Dict[int, List[Tuple[int, float]]], int, int]:
	"""
		读取 sample->probe 稀疏权重表。
		返回的 weights_map 以 sample_id 为键，值为 [(probe_id, weight), ...]。
	"""
	with open(path, "r", encoding="utf-8") as f:
		data = json.load(f)
	weights_map: Dict[int, List[Tuple[int, float]]] = {}
	num_probes = int(data.get("num_probes", 0))
	if num_probes <= 0:
		raise ValueError("sample_weights.json missing valid num_probes")
	top_k = int(data.get("top_k_sample", 0))
	for row in data.get("weights", []):
		sid = int(row.get("sample_id", row.get("id", -1)))
		probes = [(int(p["id"]), float(p["w"])) for p in row.get("probes", [])]
		weights_map[sid] = probes
	return weights_map, num_probes, top_k


def load_dirs(dirs_npy: str | None, dirs_from_samples: np.ndarray | None, expected: int) -> np.ndarray:
	"""
		加载方向集
		1) --dirs-npy 显式传入
		2) samples.json 中内嵌的 dirsLocal
	"""
	if dirs_npy:
		dirs = np.asarray(np.load(dirs_npy), dtype=np.float64)
	elif dirs_from_samples is not None:
		dirs = dirs_from_samples.astype(np.float64)
	else:
		raise RuntimeError(
			"Directions missing. Provide --dirs-npy (saved during sampling) or re-run sample_surface.py to embed dirsLocal."
		)
	if dirs.ndim != 2 or dirs.shape[1] != 3:
		raise ValueError(f"Directions must be (N,3); got {dirs.shape}")
	if expected and dirs.shape[0] != expected:
		raise ValueError(f"Directions count mismatch: expected {expected}, got {dirs.shape[0]}")
	return dirs


def build_system(samples: List[SampleRadiance], weights: Dict[int, List[Tuple[int, float]]], dirs: np.ndarray, order: int, num_probes: int):
	"""
		构建 SH 线性回归系统 A*x=b
		使用 max(0, dot(d, n)) 的半球权重，仅拟合法线半球方向。
		将方向权重以 sqrt(w) 乘到 A 与 b 上，等价于加权最小二乘。
	"""
	num_dirs = dirs.shape[0]
	C = num_sh_coeffs(order)
	basis = eval_sh_basis(dirs, order)  # 形状：(D, C)
	rows = len(samples) * num_dirs
	A = np.zeros((rows, num_probes * C), dtype=np.float64)
	b = np.zeros((rows, 3), dtype=np.float64)
	row = 0
	for s in samples:
		probe_weights = weights.get(s.sample_id)
		if not probe_weights:
			raise ValueError(f"No weights found for sample_id={s.sample_id}")
		if s.radiance.shape[0] != num_dirs:
			raise ValueError(f"Sample {s.sample_id} radiance dirs {s.radiance.shape[0]} != expected {num_dirs}")
		
		# 方向权重：强调靠近法线方向的拟合精度，抑制背面方向噪声。
		w_d_array = np.maximum(0.0, dirs @ s.normal)
		
		for j in range(num_dirs):
			w_d = w_d_array[j]
			if w_d <= 0.0:
				continue
			sqrt_wd = np.sqrt(w_d)
			Y = sqrt_wd * basis[j]
			for pid, w in probe_weights:
				base = pid * C
				A[row, base : base + C] = w * Y
			b[row] = sqrt_wd * s.radiance[j]
			row += 1
	A = A[:row]
	b = b[:row]
	if row == 0:
		raise ValueError("All rows were filtered out by direction weights; check normals and direction set")
	return A, b, C


def save_outputs(coeffs: np.ndarray, order: int, output_npy: str, output_json: str | None):
	"""
		保存拟合后的 SH 系数。
		npy: 作为后续 pack_probes.py 的机器可读输入。
		json: 便于人工检查与调试。
	"""
	os.makedirs(os.path.dirname(output_npy), exist_ok=True)
	np.save(output_npy, coeffs.astype(np.float32))
	if output_json:
		out = {
			"order": order,
			"num_probes": int(coeffs.shape[0]),
			"coeffs": [
				{
					"probe_id": int(i),
					"coeffs": [
						{"r": float(c[0]), "g": float(c[1]), "b": float(c[2])}
						for c in coeffs[i]
					],
				}
				for i in range(coeffs.shape[0])
			],
		}
		with open(output_json, "w", encoding="utf-8") as f:
			json.dump(out, f, indent=2)


def parse_args():
	"""定义命令行参数。"""
	parser = argparse.ArgumentParser(description="Fit SH coefficients for probes using sampled radiance and weights")
	parser.add_argument("--samples-json", required=True, help="Path to samples JSON produced by sample_surface.py")
	parser.add_argument("--sample-weights", required=True, help="Path to sample_weights.json from export_probes.py")
	parser.add_argument("--order", type=int, default=2, help="SH order (only <=2 supported)")
	parser.add_argument("--lambda-reg", type=float, default=0.1, help="Ridge regularization weight (paper-style default: 0.1)")
	parser.add_argument("--output-npy", required=True, help="Output .npy for probe coeffs (shape: P x C x 3)")
	parser.add_argument("--output-json", default=None, help="Optional JSON dump for inspection")
	parser.add_argument("--dirs-npy", default=None, help="Optional .npy directions used during sampling (overrides dirsLocal)")
	return parser.parse_args()


def main():
	"""脚本入口：读取数据、构建系统、求解并落盘。"""
	args = parse_args()

	# samples 提供每个采样点的目标辐亮度；num_dirs_declared 用于与方向集做一致性检查。
	samples, dirs_from_samples, num_dirs_declared = load_samples(args.samples_json)
	# weights 提供 sample 到 probes 的稀疏混合关系，是构建 A 的核心结构。
	weights, num_probes, top_k = load_sample_weights(args.sample_weights)
	# 方向集决定 SH 基函数的采样点；若方向不一致会直接影响拟合正确性。
	dirs = load_dirs(args.dirs_npy, dirs_from_samples, num_dirs_declared)

	# 构建线性系统。order 决定系数维度 C=(order+1)^2。
	A, b, C = build_system(samples, weights, dirs, args.order, num_probes)
	print(f"[fit_sh] Rows={A.shape[0]}, Cols={A.shape[1]}, Probes={num_probes}, topK={top_k}, Order={args.order}, Lambda={args.lambda_reg}")

	# lambda_reg 默认 0.1，平滑稳定化强度，兼顾拟合精度与抗噪性。
	coeff_vec = solve_ridge(A, b, lambda_reg=args.lambda_reg)  # 形状：(P*C, 3)
	coeffs = coeff_vec.reshape(num_probes, C, 3)
	save_outputs(coeffs, args.order, args.output_npy, args.output_json)
	print(f"[fit_sh] Saved coeffs to {args.output_npy}" + (f" and {args.output_json}" if args.output_json else ""))


if __name__ == "__main__":
	main()

