"""
生成探针分布与 sample-to-probe 权重矩阵 W。

python Offline/export/export_probes.py `
  --samples-json Data/samples/SampleScene_samples_pt.json `
  --mesh-json Data/meshs/SampleScene_mesh.json `
  --probes 16 `
  --top-k-sample 4 `
  --top-k-vertex 2 `
  --output-dir Data/probes

输入
采样点 JSON（例如 Data/samples/<scene>_samples_pt.json）
    期望字段：samples[i].position.{x,y,z}。若缺少 sample_id，则使用索引。
网格 JSON（例如 Data/meshs/<scene>_mesh.json），用于读取顶点位置。
目标探针数量 K。

输出
probes.json: 形如 {"probe_id", "position", "space"} 的列表
sample_weights.json: W 的稀疏行格式（行=sample，列=top-K probes）
    {
        "num_samples": N,
        "num_probes": K,
        "top_k_sample": topK_sample,
        "weights": [ {"sample_id": i, "probes": [{"id": k, "w": w_ik}, ...]} ]
    }
mesh_assoc.json: 每个网格对象的 vertex→probe 关联（top-K 归一化）
    [
        {"mesh_name": name, "top_k_vertex": K, "vertices": [ {"id": vid, "probes": [{"id": k, "w": w_vk}, ...]} ]}
    ]

    关于 W 
    sample_weights 构建矩阵 W，其中 W[i,k] 是 sample i 对 probe k 的权重。
    每行归一化：sum_k W[i,k] = 1（采用 top-K 最近探针与反距离加权）。
    在 SH 求解中，对每个 sample i 和方向 d：f_i(d) ≈ Σ_k W[i,k] * SH_k(d)。
    组装正规方程时，W 作为从 probe SH 到 sample SH 的线性混合矩阵。
    mesh_assoc 同理，用于将顶点权重写入 uv2（top-K 归一化）。
"""

from __future__ import annotations

import argparse
import json
import math
import os
from dataclasses import dataclass
from typing import List, Tuple, Dict, Any

import numpy as np


# ----------------------------- 数据结构 -----------------------------


@dataclass
class Sample:
    idx: int
    pos: np.ndarray  # (3,)


@dataclass
class Probe:
    idx: int
    pos: np.ndarray  # (3,)


@dataclass
class MeshObject:
    name: str
    vertices: np.ndarray  # (V,3)


# ----------------------------- IO 辅助 -----------------------------


def load_samples(path: str) -> List[Sample]:
    """
        从采样 JSON 读取 sample 点位。
        兼容两种字段命名：position 与 pos。
        若缺失 sample_id，则用数组索引回填，保证后续索引稳定。
    """
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    samples_json = data.get("samples", []) if isinstance(data, dict) else data
    samples: List[Sample] = []
    for i, s in enumerate(samples_json):
        p = s.get("position", s.get("pos", {}))
        pos = np.array([float(p.get("x", 0.0)), float(p.get("y", 0.0)), float(p.get("z", 0.0))], dtype=np.float64)
        idx = s.get("sample_id", i)
        samples.append(Sample(idx=idx, pos=pos))
    return samples


def load_mesh_vertices(path: str) -> List[MeshObject]:
    """读取导出网格的顶点位置，仅保留关联探针所需的几何信息。"""
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    objs = []
    for obj in data.get("meshObjects", []):
        positions = obj.get("positions", [])
        verts = []
        for p in positions:
            verts.append([float(p.get("x", 0.0)), float(p.get("y", 0.0)), float(p.get("z", 0.0))])
        verts_np = np.array(verts, dtype=np.float64) if len(verts) > 0 else np.zeros((0, 3), dtype=np.float64)
        name = obj.get("name", "mesh")
        objs.append(MeshObject(name=name, vertices=verts_np))
    return objs


def save_probes(path: str, probes: List[Probe], space: str = "world") -> None:
    """保存聚类后的探针中心。"""
    out = [
        {"probe_id": p.idx, "position": {"x": float(p.pos[0]), "y": float(p.pos[1]), "z": float(p.pos[2])}, "space": space}
        for p in probes
    ]
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)


def save_weights(path: str, weights, num_samples: int, num_probes: int, top_k: int) -> None:
    """保存 sample->probe 稀疏权重矩阵（JSON 友好格式）。"""
    out = {
        "num_samples": num_samples,
        "num_probes": num_probes,
        "top_k_sample": top_k,
        "weights": weights,
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)


def save_mesh_assoc(path: str, assoc_list: List[Dict[str, Any]], top_k_vertex: int) -> None:
    """保存 mesh 顶点到 probe 的关联，并写入 top_k 元信息。"""
    for entry in assoc_list:
        entry["top_k_vertex"] = top_k_vertex
    with open(path, "w", encoding="utf-8") as f:
        json.dump(assoc_list, f, indent=2)


# ----------------------------- 聚类（K 中心点法） -----------------------------


def kmedoids(points: np.ndarray, k: int, iters: int = 20, seed: int = 42) -> np.ndarray:
    """
        在 Nx3 点集上执行 K-Medoids。
        这里选择 K-Medoids 而不是 K-Means：
        代表点来自真实样本，几何解释更稳定。
        对离群点鲁棒性更好，有助于减少 probe 漏光风险。
    """
    rng = np.random.default_rng(seed)
    n = points.shape[0]
    if n == 0:
        raise ValueError("no points provided for kmedoids")
    if k <= 0:
        raise ValueError("probe count must be > 0")
    k = min(k, n)
    medoid_idx = rng.choice(n, size=k, replace=False)
    
    for _ in range(iters):
        dists = np.linalg.norm(points[:, None, :] - points[medoid_idx][None, :, :], axis=2)
        assign = np.argmin(dists, axis=1)
        new_medoids = medoid_idx.copy()
        
        for j in range(k):
            mask = assign == j
            if not np.any(mask):
                new_medoids[j] = rng.integers(0, n)
            else:
                cluster_pts_idx = np.where(mask)[0]
                cluster_pts = points[cluster_pts_idx]
                intra_dists = np.linalg.norm(cluster_pts[:, None, :] - cluster_pts[None, :, :], axis=2)
                cost = np.sum(intra_dists, axis=1)
                best_idx_in_cluster = np.argmin(cost)
                new_medoids[j] = cluster_pts_idx[best_idx_in_cluster]
                
        if np.array_equal(medoid_idx, new_medoids):
            break
        medoid_idx = new_medoids
        
    return points[medoid_idx].copy()


def topk_inverse_distance(points: np.ndarray, centers: np.ndarray, top_k: int, eps: float = 1e-8):
    """
        计算 top-K 反距离权重。
        每一行都会归一化到 1，保证可直接作为线性混合系数使用。
    """
    n = points.shape[0]
    k_centers = centers.shape[0]
    if k_centers == 0:
        raise ValueError("No probe centers available")
    if top_k <= 0:
        raise ValueError("top_k must be > 0")
    top_k = min(top_k, k_centers)
    dists = np.linalg.norm(points[:, None, :] - centers[None, :, :], axis=2)  # (n,k)

    weights_out = []
    for i in range(n):
        row = dists[i]
        top_idx = np.argpartition(row, top_k - 1)[:top_k]
        top_idx = top_idx[np.argsort(row[top_idx])]
        inv = 1.0 / (row[top_idx] + eps)
        w = inv / np.sum(inv)
        weights_out.append({
            "id": int(i),
            "probes": [
                {"id": int(pid), "w": float(wj)}
                for pid, wj in zip(top_idx, w)
            ]
        })
    return weights_out


def compute_vertex_assoc(meshes: List[MeshObject], centers: np.ndarray, top_k_vertex: int):
    """构建每个网格顶点到 probe 的稀疏关联。"""
    assoc_list = []
    for m in meshes:
        if m.vertices.shape[0] == 0:
            assoc_list.append({"mesh_name": m.name, "vertices": []})
            continue
        rows = topk_inverse_distance(m.vertices, centers, top_k=top_k_vertex)
        # 将 id 重命名为 vertex_id
        for r in rows:
            r["vertex_id"] = r.pop("id")
        assoc_list.append({
            "mesh_name": m.name,
            "vertex_count": int(m.vertices.shape[0]),
            "vertices": rows,
        })
    return assoc_list


# ----------------------------- 权重（构建 W） -----------------------------


def compute_sample_weights(points: np.ndarray, centers: np.ndarray, top_k: int):
    """构建 sample->probe 的权重矩阵 W（稀疏表示）。"""
    rows = topk_inverse_distance(points, centers, top_k=top_k)
    # 为清晰起见，将 id 重命名为 sample_id
    for r in rows:
        r["sample_id"] = r.pop("id")
    return rows


def run_all(samples_path: str, mesh_path: str, probes_count: int, top_k_sample: int, top_k_vertex: int, output_dir: str, seed: int = 42):
    """执行完整导出流程：聚类探针、生成 W、生成顶点关联并写文件。"""
    # 1) 加载采样点
    samples = load_samples(samples_path)
    if len(samples) == 0:
        raise ValueError("samples-json does not contain any samples")
    points = np.stack([s.pos for s in samples], axis=0)

    # 2) 探针聚类（K-Medoids）
    # probes_count 由上层质量档位控制，作为“容量预算”；seed 固定可复现。
    centers = kmedoids(points, probes_count, iters=30, seed=seed)
    actual_probes = centers.shape[0]
    probes = [Probe(idx=i, pos=centers[i]) for i in range(actual_probes)]

    # 3) 采样点 -> 探针 的 W 矩阵（top-K 反距离加权，行和为 1）
    # top_k_sample 常设为 2~4，可在表达能力与存储开销之间平衡。
    weights_sparse = compute_sample_weights(points, centers, top_k=top_k_sample)

    # 4) 网格顶点 -> 探针 关联（top-K 反距离加权，行和为 1）
    # top_k_vertex 通常更小（如 2），因为运行时每顶点读取探针数越少越快。
    meshes = load_mesh_vertices(mesh_path)
    mesh_assoc = compute_vertex_assoc(meshes, centers, top_k_vertex=top_k_vertex)

    # 5) 保存输出
    os.makedirs(output_dir, exist_ok=True)
    save_probes(os.path.join(output_dir, "probes.json"), probes, space="world")
    save_weights(os.path.join(output_dir, "sample_weights.json"), weights_sparse, num_samples=len(samples), num_probes=actual_probes, top_k=min(top_k_sample, actual_probes))
    save_mesh_assoc(os.path.join(output_dir, "mesh_assoc.json"), mesh_assoc, top_k_vertex=min(top_k_vertex, actual_probes))

    print(f"[export_probes] Samples: {len(samples)}, Probes: {actual_probes}, topK_sample={min(top_k_sample, actual_probes)}, topK_vertex={min(top_k_vertex, actual_probes)}")
    print(f"[export_probes] Saved probes.json, sample_weights.json, mesh_assoc.json to {output_dir}")


# ----------------------------- 主流程 -----------------------------


def run(samples_path: str, probes_count: int, top_k: int, output_dir: str, seed: int = 42):
    """兼容旧接口的保护函数，防止误调用过期签名。"""
    raise RuntimeError("run() signature changed; use run_all() instead")


# ----------------------------- 命令行接口 -----------------------------


def parse_args():
    """定义命令行参数。"""
    parser = argparse.ArgumentParser(description="Probe clustering and sample-to-probe weight matrix (W) export")
    parser.add_argument("--samples-json", required=True, help="Path to surface samples JSON (contains samples[].position)")
    parser.add_argument("--mesh-json", required=True, help="Path to mesh JSON (contains meshObjects[].positions for vertices)")
    parser.add_argument("--probes", type=int, required=True, help="Number of probes (K)")
    parser.add_argument("--top-k-sample", type=int, default=4, help="Top-K nearest probes per sample for W (recommend 2-4)")
    parser.add_argument("--top-k-vertex", type=int, default=2, help="Top-K nearest probes per mesh vertex for assoc")
    parser.add_argument("--output-dir", required=True, help="Output directory (e.g., Data/probes)")
    parser.add_argument("--seed", type=int, default=42, help="Random seed for K-means init")
    return parser.parse_args()


def main():
    """命令行入口。"""
    args = parse_args()
    run_all(
        samples_path=args.samples_json,
        mesh_path=args.mesh_json,
        probes_count=args.probes,
        top_k_sample=args.top_k_sample,
        top_k_vertex=args.top_k_vertex,
        output_dir=args.output_dir,
        seed=args.seed,
    )


if __name__ == "__main__":
    main()
