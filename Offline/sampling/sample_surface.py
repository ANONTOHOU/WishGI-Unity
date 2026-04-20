"""
在三角形网格上使用蓝噪声进行表面采样，再加上简单的直接光光线查询。输入严格遵循从 SceneLightExporter（灯光）和 MeshBakeExporter（网格几何）导出的 JSON 格式。
使用方法：
python Offline/sampling/sample_surface.py `
--mesh-json Data/meshs/SampleScene_mesh.json `
--scene-json Data/scenes/SampleScene_lights.json `
--output Data/samples/SampleScene_samples_pt.json `
--min-dist 0.1 `
--num-samples 200 `
--directions 64 `
--bounces 3 `
--albedo 0.8 `
--seed 42 `
--dirs-out Data/samples/SampleScene_dirs.npy
使用极简的 CPU 光线追踪器输出每个样本的位置/法线/三角形索引/重心坐标以及每个光源的可见度和辐照度估计值的 JSON 数据。
依赖项：Python标准库、numpy
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence, Tuple

import numpy as np

try:
	from PIL import Image
	HAS_PIL = True
except Exception:
	HAS_PIL = False


# ----------------------------- 数据类 -----------------------------


@dataclass
class Vec3:
	x: float
	y: float
	z: float

	def to_np(self) -> np.ndarray:
		"""转换为 numpy 向量，便于后续几何与光照计算。"""
		return np.array([self.x, self.y, self.z], dtype=np.float32)

	@staticmethod
	def from_dict(d: dict) -> "Vec3":
		"""从 Unity 风格字典读取向量。

		兼容 {x,y,z} 与 {r,g,b} 两种键，减少上下游格式耦合。
		"""
		# 支持 Unity JSON 导出的 {x,y,z} 和 {r,g,b} 风格的键。
		if "x" in d:
			return Vec3(float(d.get("x", 0.0)), float(d.get("y", 0.0)), float(d.get("z", 0.0)))
		if "r" in d:
			return Vec3(float(d.get("r", 0.0)), float(d.get("g", 0.0)), float(d.get("b", 0.0)))
		# 如果键缺失，则回退为零
		return Vec3(0.0, 0.0, 0.0)


@dataclass
class Light:
	name: str
	type: str  # 定向 / 点 / 聚光（聚光视为点光源+锥体）
	position: Vec3
	direction: Vec3
	color: Vec3
	intensity: float
	range: float
	spot_angle: float
	inner_spot_angle: float


@dataclass
class Triangle:
	v0: np.ndarray
	v1: np.ndarray
	v2: np.ndarray
	normal: np.ndarray
	index: int
	uv0: np.ndarray
	uv1: np.ndarray
	uv2: np.ndarray
	material_slot: int
	base_color: np.ndarray
	main_tex_asset_path: str


@dataclass
class Sample:
	position: np.ndarray
	normal: np.ndarray
	tri_index: int
	barycentric: Tuple[float, float, float]
	visibility: List[float]
	irradiance: List[float]


# ----------------------------- 几何工具 -----------------------------


def load_mesh_triangles(mesh_json: str) -> List[Triangle]:
	"""读取网格 JSON 并展开为三角形列表（包含材质与 UV0 信息）。"""
	with open(mesh_json, "r", encoding="utf-8") as f:
		data = json.load(f)

	tris: List[Triangle] = []
	for obj in data.get("meshObjects", []):
		positions = [Vec3.from_dict(p).to_np() for p in obj.get("positions", [])]
		normals = [Vec3.from_dict(n).to_np() for n in obj.get("normals", [])]
		uv0s = [
			np.array([float(uv.get("x", 0.0)), float(uv.get("y", 0.0))], dtype=np.float32)
			for uv in obj.get("uv0", [])
		]

		materials = obj.get("materials", [])
		mat_base_colors: Dict[int, np.ndarray] = {}
		mat_tex_paths: Dict[int, str] = {}
		for m in materials:
			slot = int(m.get("slot", 0))
			base = m.get("baseColor", {"r": 1.0, "g": 1.0, "b": 1.0})
			mat_base_colors[slot] = np.array(
				[
					float(base.get("r", 1.0)),
					float(base.get("g", 1.0)),
					float(base.get("b", 1.0)),
				],
				dtype=np.float32,
			)
			mat_tex_paths[slot] = str(m.get("mainTexAssetPath", "") or "")

		indices = obj.get("indices", [])
		triangle_material_ids = obj.get("triangleMaterialIds", [])
		for t in range(0, len(indices), 3):
			i0, i1, i2 = indices[t : t + 3]
			v0, v1, v2 = positions[i0], positions[i1], positions[i2]

			# 使用提供的每顶点法线；如果缺失则回退为几何法线
			if len(normals) > max(i0, i1, i2):
				n = (normals[i0] + normals[i1] + normals[i2]) / 3.0
			else:
				n = np.cross(v1 - v0, v2 - v0)
			if np.linalg.norm(n) < 1e-6:
				n = np.cross(v1 - v0, v2 - v0)
			n = n / (np.linalg.norm(n) + 1e-8)

			zero_uv = np.zeros(2, dtype=np.float32)
			uv0 = uv0s[i0] if len(uv0s) > i0 else zero_uv
			uv1 = uv0s[i1] if len(uv0s) > i1 else zero_uv
			uv2 = uv0s[i2] if len(uv0s) > i2 else zero_uv

			tri_id = t // 3
			mat_slot = int(triangle_material_ids[tri_id]) if tri_id < len(triangle_material_ids) else 0
			base_color = mat_base_colors.get(mat_slot, np.array([1.0, 1.0, 1.0], dtype=np.float32))
			tex_path = mat_tex_paths.get(mat_slot, "")

			tris.append(
				Triangle(
					v0=v0,
					v1=v1,
					v2=v2,
					normal=n,
					index=len(tris),
					uv0=uv0,
					uv1=uv1,
					uv2=uv2,
					material_slot=mat_slot,
					base_color=base_color,
					main_tex_asset_path=tex_path,
				)
			)
	return tris


def load_lights(scene_json: str) -> List[Light]:
	"""读取场景灯光 JSON 并转换为统一 Light 结构。"""
	with open(scene_json, "r", encoding="utf-8") as f:
		data = json.load(f)
	lights: List[Light] = []
	for l in data.get("lights", []):
		lights.append(
			Light(
				name=l.get("name", "Light"),
				type=l.get("type", "Point"),
				position=Vec3.from_dict(l.get("position", {"x": 0, "y": 0, "z": 0})),
				direction=Vec3.from_dict(l.get("direction", {"x": 0, "y": -1, "z": 0})),
				color=Vec3.from_dict(l.get("color", {"r": 1, "g": 1, "b": 1})),
				intensity=float(l.get("intensity", 1.0)),
				range=float(l.get("range", 10.0)),
				spot_angle=float(l.get("spotAngle", 30.0)),
				inner_spot_angle=float(l.get("innerSpotAngle", 20.0)),
			)
		)
	return lights


# ----------------------------- 网格上的蓝噪声泊松圆盘采样 -----------------------------


def triangle_areas(tris: Sequence[Triangle]) -> np.ndarray:
	"""计算每个三角形面积，用于按面积重要性采样。"""
	areas = []
	for tri in tris:
		areas.append(0.5 * np.linalg.norm(np.cross(tri.v1 - tri.v0, tri.v2 - tri.v0)))
	return np.array(areas, dtype=np.float64)


def sample_point_on_triangle(tri: Triangle) -> Tuple[np.ndarray, Tuple[float, float, float]]:
	"""在单个三角形上均匀采样点并返回重心坐标。"""
	r1 = random.random()
	r2 = random.random()
	sqrt_r1 = math.sqrt(r1)
	u = 1 - sqrt_r1
	v = r2 * sqrt_r1
	w = 1 - u - v
	p = u * tri.v0 + v * tri.v1 + w * tri.v2
	return p, (u, v, w)


def build_spatial_hash(samples: List[np.ndarray], cell_size: float):
	"""构建简单空间哈希，用于加速最小距离检测。"""
	grid = {}
	for idx, p in enumerate(samples):
		key = tuple((p / cell_size).astype(int))
		grid.setdefault(key, []).append(idx)
	return grid


def is_far_enough(candidate: np.ndarray, samples: List[np.ndarray], grid, cell_size: float, min_dist: float) -> bool:
	"""判断候选点是否满足蓝噪声泊松圆盘最小间距约束。"""
	key = tuple((candidate / cell_size).astype(int))
	for dx in (-1, 0, 1):
		for dy in (-1, 0, 1):
			for dz in (-1, 0, 1):
				neigh = (key[0] + dx, key[1] + dy, key[2] + dz)
				if neigh not in grid:
					continue
				for idx in grid[neigh]:
					if np.linalg.norm(candidate - samples[idx]) < min_dist:
						return False
	return True


def blue_noise_sample(tris: Sequence[Triangle], target_count: int, min_dist: float) -> List[Tuple[np.ndarray, Triangle, Tuple[float, float, float]]]:
	"""在三角网格表面执行蓝噪声（泊松圆盘）采样。"""
	areas = triangle_areas(tris)
	cdf = np.cumsum(areas)
	total_area = cdf[-1]
	samples: List[np.ndarray] = []
	outputs = []
	cell_size = min_dist / math.sqrt(3)  # 为 3D 泊松圆盘采样设置更紧密的网格
	grid = {}
	max_trials = target_count * 50  # 投掷飞镖的最大尝试次数，以避免死循环

	for _ in range(max_trials):
		r = random.random() * total_area
		tri_idx = int(np.searchsorted(cdf, r))
		tri = tris[tri_idx]
		p, bary = sample_point_on_triangle(tri)

		if is_far_enough(p, samples, grid, cell_size, min_dist):
			samples.append(p)
			key = tuple((p / cell_size).astype(int))
			grid.setdefault(key, []).append(len(samples) - 1)
			outputs.append((p, tri, bary))
			if len(outputs) >= target_count:
				break
	return outputs


# ----------------------------- 光线追踪（BVH + Möller–Trumbore） -----------------------------


@dataclass
class BVHNode:
	bounds_min: np.ndarray
	bounds_max: np.ndarray
	left: Optional["BVHNode"]
	right: Optional["BVHNode"]
	tri_indices: List[int]


def build_bvh(tris: List[Triangle], indices: Optional[List[int]] = None, depth: int = 0) -> BVHNode:
	"""递归构建 BVH，加速遮挡和命中查询。"""
	if indices is None:
		indices = list(range(len(tris)))

	tris_np = np.array([np.stack([tris[i].v0, tris[i].v1, tris[i].v2], axis=0) for i in indices])
	bmin = tris_np.min(axis=(0, 1))
	bmax = tris_np.max(axis=(0, 1))

	if len(indices) <= 4:
		return BVHNode(bmin, bmax, None, None, indices)

	extents = bmax - bmin
	axis = int(np.argmax(extents))
	centers = tris_np.mean(axis=1)
	median = np.median(centers[:, axis])
	left_idx = [idx for idx, c in zip(indices, centers[:, axis]) if c <= median]
	right_idx = [idx for idx, c in zip(indices, centers[:, axis]) if c > median]
	if len(left_idx) == 0 or len(right_idx) == 0:
		mid = len(indices) // 2
		left_idx = indices[:mid]
		right_idx = indices[mid:]

	left = build_bvh(tris, left_idx, depth + 1)
	right = build_bvh(tris, right_idx, depth + 1)
	return BVHNode(bmin, bmax, left, right, [])


def ray_aabb_intersect(orig, dir, bmin, bmax) -> bool:
	"""射线与 AABB 求交测试。"""
	inv = 1.0 / (dir + 1e-12)
	t0 = (bmin - orig) * inv
	t1 = (bmax - orig) * inv
	tmin = np.maximum.reduce(np.minimum(t0, t1))
	tmax = np.minimum.reduce(np.maximum(t0, t1))
	return tmax >= max(tmin, 0.0)


def ray_triangle_intersect(orig, dir, tri: Triangle, t_min=1e-4, t_max=1e9) -> Optional[float]:
	"""Moller-Trumbore 射线三角形求交，返回参数 t。"""
	v0, v1, v2 = tri.v0, tri.v1, tri.v2
	e1 = v1 - v0
	e2 = v2 - v0
	h = np.cross(dir, e2)
	a = np.dot(e1, h)
	if -1e-8 < a < 1e-8:
		return None
	f = 1.0 / a
	s = orig - v0
	u = f * np.dot(s, h)
	if u < 0.0 or u > 1.0:
		return None
	q = np.cross(s, e1)
	v = f * np.dot(dir, q)
	if v < 0.0 or u + v > 1.0:
		return None
	t = f * np.dot(e2, q)
	if t_min < t < t_max:
		return t
	return None


def ray_triangle_intersect_closest(orig, dir, tri: Triangle, t_min=1e-4, t_max=1e9) -> Optional[float]:
	"""与 ray_triangle_intersect 相同，保留独立函数名便于语义区分。"""
	return ray_triangle_intersect(orig, dir, tri, t_min, t_max)


def bvh_intersect(node: BVHNode, tris: List[Triangle], orig, dir, t_min=1e-4, t_max=1e9) -> bool:
	"""返回是否存在任意遮挡命中。"""
	if not ray_aabb_intersect(orig, dir, node.bounds_min, node.bounds_max):
		return False
	if node.left is None and node.right is None:
		for idx in node.tri_indices:
			if ray_triangle_intersect(orig, dir, tris[idx], t_min, t_max) is not None:
				return True
		return False
	hit_left = node.left and bvh_intersect(node.left, tris, orig, dir, t_min, t_max)
	if hit_left:
		return True
	hit_right = node.right and bvh_intersect(node.right, tris, orig, dir, t_min, t_max)
	return bool(hit_right)


def bvh_first_hit(node: BVHNode, tris: List[Triangle], orig, dir, t_min=1e-4, t_max=1e9) -> Optional[Tuple[float, int]]:
	"""返回最近命中点的 (t, tri_index)，若无命中则返回 None。"""
	# 返回最近命中点的 (t, tri_index)，若无命中则返回 None。
	if not ray_aabb_intersect(orig, dir, node.bounds_min, node.bounds_max):
		return None
	if node.left is None and node.right is None:
		closest_t = None
		closest_idx = -1
		for idx in node.tri_indices:
			t = ray_triangle_intersect_closest(orig, dir, tris[idx], t_min, t_max if closest_t is None else closest_t)
			if t is not None and (closest_t is None or t < closest_t):
				closest_t = t
				closest_idx = idx
		if closest_t is None:
			return None
		return closest_t, closest_idx
	hit_left = bvh_first_hit(node.left, tris, orig, dir, t_min, t_max) if node.left else None
	hit_right = bvh_first_hit(node.right, tris, orig, dir, t_min, t_max) if node.right else None
	if hit_left is None:
		return hit_right
	if hit_right is None:
		return hit_left
	return hit_left if hit_left[0] <= hit_right[0] else hit_right


# ----------------------------- 光照 -----------------------------


def cosine_hemisphere_samples(num_dirs: int) -> np.ndarray:
	"""生成一组余弦加权半球方向，用于离线多方向采样。"""
	# 使用同心映射的分层余弦加权采样
	out = []
	m = int(math.sqrt(num_dirs))
	if m * m < num_dirs:
		m += 1
	for i in range(m):
		for j in range(m):
			if len(out) >= num_dirs:
				break
			u = (i + random.random()) / m
			v = (j + random.random()) / m
			sx = 2 * u - 1
			sy = 2 * v - 1
			if sx == 0 and sy == 0:
				r = 0
				theta = 0
			else:
				if abs(sx) > abs(sy):
					r = sx
					theta = (math.pi / 4) * (sy / sx)
				else:
					r = sy
					theta = (math.pi / 2) - (math.pi / 4) * (sx / sy)
			dx = r * math.cos(theta)
			dy = r * math.sin(theta)
			dz = math.sqrt(max(0.0, 1 - dx * dx - dy * dy))
			out.append(np.array([dx, dy, dz], dtype=np.float32))
	return np.stack(out, axis=0)


def sample_cosine_hemisphere() -> np.ndarray:
	"""随机生成单个余弦加权半球方向。"""
	# 在半球上生成单个随机余弦加权样本
	u1 = random.random()
	u2 = random.random()
	r = math.sqrt(u1)
	theta = 2 * math.pi * u2
	x = r * math.cos(theta)
	y = r * math.sin(theta)
	z = math.sqrt(max(0.0, 1 - u1))
	return np.array([x, y, z], dtype=np.float32)


def orthonormal_basis(n: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
	"""根据法线构造切线-副切线正交基。"""
	if abs(n[0]) > abs(n[2]):
		tangent = np.array([-n[1], n[0], 0.0])
	else:
		tangent = np.array([0.0, -n[2], n[1]])
	tangent /= np.linalg.norm(tangent) + 1e-8
	bitangent = np.cross(n, tangent)
	return tangent, bitangent


def world_from_tangent(normal: np.ndarray, local_dir: np.ndarray) -> np.ndarray:
	"""将切线空间方向变换到世界空间。"""
	t, b = orthonormal_basis(normal)
	return local_dir[0] * t + local_dir[1] * b + local_dir[2] * normal


def barycentric_from_point(p: np.ndarray, tri: Triangle) -> Tuple[float, float, float]:
	"""由三角面与命中点重建重心坐标。"""
	v0 = tri.v1 - tri.v0
	v1 = tri.v2 - tri.v0
	v2 = p - tri.v0
	d00 = float(np.dot(v0, v0))
	d01 = float(np.dot(v0, v1))
	d11 = float(np.dot(v1, v1))
	d20 = float(np.dot(v2, v0))
	d21 = float(np.dot(v2, v1))
	denom = d00 * d11 - d01 * d01
	if abs(denom) < 1e-10:
		return 1.0, 0.0, 0.0
	v = (d11 * d20 - d01 * d21) / denom
	w = (d00 * d21 - d01 * d20) / denom
	u = 1.0 - v - w
	return u, v, w


def srgb_to_linear(c: np.ndarray) -> np.ndarray:
	"""将 sRGB 颜色转换到线性空间。"""
	c = np.clip(c, 0.0, 1.0)
	return np.where(c <= 0.04045, c / 12.92, np.power((c + 0.055) / 1.055, 2.4))


def load_texture_linear(mesh_json_path: str, asset_rel_path: str, cache: Dict[str, np.ndarray]) -> Optional[np.ndarray]:
	"""加载贴图并缓存为线性 RGB 数组（H, W, 3）。"""
	if not asset_rel_path:
		return None
	if asset_rel_path in cache:
		return cache[asset_rel_path]
	if not HAS_PIL:
		cache[asset_rel_path] = None
		return None

	workspace_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(mesh_json_path))))
	abs_path = os.path.join(workspace_root, "UnityProjectBake", asset_rel_path.replace("/", os.sep))
	if not os.path.exists(abs_path):
		cache[asset_rel_path] = None
		return None

	try:
		with Image.open(abs_path) as img:
			rgb = np.asarray(img.convert("RGB"), dtype=np.float32) / 255.0
			linear = srgb_to_linear(rgb)
			cache[asset_rel_path] = linear
			return linear
	except Exception:
		cache[asset_rel_path] = None
		return None


def sample_albedo(tri: Triangle, bary: Tuple[float, float, float], mesh_json_path: str, default_albedo_rgb: np.ndarray, tex_cache: Dict[str, np.ndarray], stats: Dict[str, int]) -> np.ndarray:
	"""按三角面材质与 UV0 采样反射率，失败时回退默认 albedo。"""
	base = np.clip(tri.base_color, 0.0, 1.0)
	tex = load_texture_linear(mesh_json_path, tri.main_tex_asset_path, tex_cache)
	if tex is None:
		if tri.main_tex_asset_path:
			stats["textureFallbackCount"] += 1
		if np.all(np.isfinite(base)):
			return base
		stats["defaultFallbackCount"] += 1
		return default_albedo_rgb

	stats["textureSampleCount"] += 1
	u, v, w = bary
	uv = u * tri.uv0 + v * tri.uv1 + w * tri.uv2
	uu = float(np.clip(uv[0], 0.0, 1.0))
	vv = float(np.clip(uv[1], 0.0, 1.0))

	h, tw, _ = tex.shape
	fx = uu * max(tw - 1, 1)
	fy = vv * max(h - 1, 1)
	x0 = int(np.floor(fx))
	y0 = int(np.floor(fy))
	x1 = min(x0 + 1, tw - 1)
	y1 = min(y0 + 1, h - 1)
	tx = fx - x0
	ty = fy - y0

	c00 = tex[y0, x0]
	c10 = tex[y0, x1]
	c01 = tex[y1, x0]
	c11 = tex[y1, x1]
	c0 = c00 * (1.0 - tx) + c10 * tx
	c1 = c01 * (1.0 - tx) + c11 * tx
	tex_col = c0 * (1.0 - ty) + c1 * ty
	return np.clip(tex_col * base, 0.0, 1.0)


def path_trace(
	pos: np.ndarray,
	normal: np.ndarray,
	barycentric: Tuple[float, float, float],
	tri_index: int,
	view_dir_local: np.ndarray,
	lights: List[Light],
	bvh: BVHNode,
	tris: List[Triangle],
	mesh_json_path: str,
	max_bounces: int = 3,
	default_albedo_rgb: np.ndarray | None = None,
	tex_cache: Dict[str, np.ndarray] | None = None,
	stats: Dict[str, int] | None = None,
) -> np.ndarray:
	"""简化路径追踪器，使用按面材质+纹理采样的反射率推进多次反弹。"""
	if default_albedo_rgb is None:
		default_albedo_rgb = np.array([0.8, 0.8, 0.8], dtype=np.float32)
	if tex_cache is None:
		tex_cache = {}
	if stats is None:
		stats = {"textureSampleCount": 0, "textureFallbackCount": 0, "defaultFallbackCount": 0}

	# 带有下一个事件估计（直接光线）和余弦采样的简单兰伯特路径追踪器。
	throughput = np.array([1.0, 1.0, 1.0], dtype=np.float32)
	radiance = np.zeros(3, dtype=np.float32)

	# 将输入点视为首次碰到的表面点
	hit_pos = pos
	hit_normal = normal
	hit_bary = barycentric
	hit_tri_index = tri_index
	ray_dir = world_from_tangent(normal, view_dir_local)

	for bounce in range(max_bounces):
		hit_tri = tris[hit_tri_index]
		hit_albedo = sample_albedo(hit_tri, hit_bary, mesh_json_path, default_albedo_rgb, tex_cache, stats)

		# 当前的直射光强度达到峰值
		Ld = direct_radiance(hit_pos, hit_normal, lights, bvh, tris)
		radiance += throughput * hit_albedo * Ld

		# 经过两次回转后
		if bounce >= 2:
			p = 0.9
			if random.random() > p:
				break
			throughput /= p

		# 采样新的漫反射方向（余弦加权）
		new_dir_local = sample_cosine_hemisphere()
		new_dir_world = world_from_tangent(hit_normal, new_dir_local)

		# 跟踪到下一个表面
		ray_origin = hit_pos + hit_normal * 1e-4
		hit = bvh_first_hit(bvh, tris, ray_origin, new_dir_world, 1e-4, 1e9)
		if hit is None:
			break  # 逃逸到环境（未建模）
		t_hit, tri_idx = hit
		tri = tris[tri_idx]
		hit_pos = ray_origin + new_dir_world * t_hit
		hit_normal = tri.normal
		hit_bary = barycentric_from_point(hit_pos, tri)
		hit_tri_index = tri_idx

		# 更新漫反射反弹的通量
		throughput *= hit_albedo

	return radiance


def compute_direct_lighting(sample: Sample, lights: List[Light], bvh: BVHNode, tris: List[Triangle]) -> None:
	"""计算样本点对各灯光的可见性与直接辐照度。"""
	pos = sample.position
	n = sample.normal
	vis_list = []
	irr_list = []

	for l in lights:
		if l.type.lower().startswith("dir"):
			dir_to_light = -l.direction.to_np()
			dir_to_light = dir_to_light / (np.linalg.norm(dir_to_light) + 1e-8)
			visible = not bvh_intersect(bvh, tris, pos + n * 1e-4, dir_to_light, 1e-4, 1e9)
			ndotl = max(0.0, float(np.dot(n, dir_to_light)))
			irr = ndotl * l.intensity if visible else 0.0
		else:  # 目前对这些点/区域的处理方式相同（未应用角度衰减效果）
			to_light = l.position.to_np() - pos
			dist = np.linalg.norm(to_light)
			if dist <= 1e-6 or dist > l.range:
				vis_list.append(0.0)
				irr_list.append(0.0)
				continue
			dir_to_light = to_light / dist
			visible = not bvh_intersect(bvh, tris, pos + n * 1e-4, dir_to_light, 1e-4, dist)
			ndotl = max(0.0, float(np.dot(n, dir_to_light)))
			attenuation = 1.0 / max(dist * dist, 1e-6)
			irr = ndotl * l.intensity * attenuation if visible else 0.0
		vis_list.append(1.0 if visible else 0.0)
		irr_list.append(float(irr))

	sample.visibility = vis_list
	sample.irradiance = irr_list


def direct_radiance(pos: np.ndarray, normal: np.ndarray, lights: List[Light], bvh: BVHNode, tris: List[Triangle]) -> np.ndarray:
	"""估计一个表面点的直接光辐亮度（RGB）。"""
	# 返回表面点的 RGB 直接光照（兰伯特，无纹理），并进行阴影检查。
	n = normal
	radiance = np.zeros(3, dtype=np.float32)
	for l in lights:
		if l.type.lower().startswith("dir"):
			dir_to_light = -l.direction.to_np()
			dir_to_light = dir_to_light / (np.linalg.norm(dir_to_light) + 1e-8)
			visible = not bvh_intersect(bvh, tris, pos + n * 1e-4, dir_to_light, 1e-4, 1e9)
			ndotl = max(0.0, float(np.dot(n, dir_to_light)))
			if visible and ndotl > 0.0:
				Li = l.color.to_np() * float(l.intensity)
				radiance += Li * ndotl
		else:
			to_light = l.position.to_np() - pos
			dist = np.linalg.norm(to_light)
			if dist <= 1e-6 or dist > l.range:
				continue
			dir_to_light = to_light / dist
			visible = not bvh_intersect(bvh, tris, pos + n * 1e-4, dir_to_light, 1e-4, dist)
			ndotl = max(0.0, float(np.dot(n, dir_to_light)))
			if visible and ndotl > 0.0:
				attenuation = 1.0 / max(dist * dist, 1e-6)
				Li = l.color.to_np() * float(l.intensity) * attenuation
				radiance += Li * ndotl
	return radiance


# ----------------------------- 管线 -----------------------------


def run_sampling(mesh_json: str, scene_json: str, output_path: str, min_dist: float, num_samples: int, num_dirs: int, max_bounces: int, default_albedo: float, seed: int, dirs_out: str | None):
	"""执行离线采样主流程：采样点生成、路径追踪、写出 JSON。"""
	# 固定种子，以便在 SH 拟合中采样、方向和路径追踪可重复
	random.seed(seed)
	np.random.seed(seed)
	print(f"[GI] Loading mesh from {mesh_json}")
	tris = load_mesh_triangles(mesh_json)
	print(f"[GI] Triangles: {len(tris)}")

	print(f"[GI] Loading lights from {scene_json}")
	lights = load_lights(scene_json)
	print(f"[GI] Lights: {len(lights)}")

	print(f"[GI] Building BVH for ray queries...")
	bvh = build_bvh(tris)
	tex_cache: Dict[str, np.ndarray] = {}
	albedo_stats = {"textureSampleCount": 0, "textureFallbackCount": 0, "defaultFallbackCount": 0}
	default_albedo_rgb = np.array([default_albedo, default_albedo, default_albedo], dtype=np.float32)

	# min_dist 决定样本空间密度；num_samples 是目标上限，两者共同控制质量与耗时。
	print(f"[GI] Blue-noise sampling on surface: target {num_samples}, minDist {min_dist}")
	pts = blue_noise_sample(tris, target_count=num_samples, min_dist=min_dist)
	print(f"[GI] Accepted samples: {len(pts)}")

	# num_dirs 决定每个采样点的方向分辨率；方向越多，SH 拟合数据越充分但成本更高。
	print(f"[GI] Generating cosine-weighted directions: {num_dirs} (seed={seed})")
	dirs_local = cosine_hemisphere_samples(num_dirs)
	if dirs_out:
		dirs_dir = os.path.dirname(dirs_out)
		if dirs_dir:
			os.makedirs(dirs_dir, exist_ok=True)
		print(f"[GI] Saving directions -> {dirs_out}")
		np.save(dirs_out, dirs_local.astype(np.float32))

	samples: List[Sample] = []
	for p, tri, bary in pts:
		s = Sample(position=p, normal=tri.normal, tri_index=tri.index, barycentric=bary, visibility=[], irradiance=[])
		compute_direct_lighting(s, lights, bvh, tris)
		s.radiance_dirs = []  # 类型：忽略[属性已定义]

		# 基于预先计算的局部方向集路径追踪。
		# max_bounces 与 default_albedo 的初始化是经验折中：
		# - bounces=3 可覆盖基础间接光而不过度增加时长。
		# - default_albedo=0.8 在缺失材质数据时提供稳定回退。
		radiance_per_dir = []
		for d_local in dirs_local:
			radiance = path_trace(
				p,
				tri.normal,
				bary,
				tri.index,
				d_local,
				lights,
				bvh,
				tris,
				mesh_json,
				max_bounces=max_bounces,
				default_albedo_rgb=default_albedo_rgb,
				tex_cache=tex_cache,
				stats=albedo_stats,
			)
			radiance_per_dir.append([float(radiance[0]), float(radiance[1]), float(radiance[2])])
		s.radiance_dirs = radiance_per_dir
		samples.append(s)

	out_dir = os.path.dirname(output_path)
	if out_dir:
		os.makedirs(out_dir, exist_ok=True)
	print(f"[GI] Writing samples -> {output_path}")
	out_data = {
		"mesh": os.path.basename(mesh_json),
		"scene": os.path.basename(scene_json),
		"minDist": min_dist,
		"numSamples": len(samples),
		"numDirections": num_dirs,
		"seed": seed,
		"defaultAlbedo": default_albedo,
		"textureSupport": bool(HAS_PIL),
		"albedoStats": {
			"textureSampleCount": int(albedo_stats["textureSampleCount"]),
			"textureFallbackCount": int(albedo_stats["textureFallbackCount"]),
			"defaultFallbackCount": int(albedo_stats["defaultFallbackCount"]),
		},
		"dirsLocal": [
			{"x": float(d[0]), "y": float(d[1]), "z": float(d[2])}
			for d in dirs_local
		],
		"samples": [
			{
				"position": {"x": float(s.position[0]), "y": float(s.position[1]), "z": float(s.position[2])},
				"normal": {"x": float(s.normal[0]), "y": float(s.normal[1]), "z": float(s.normal[2])},
				"triangleIndex": int(s.tri_index),
				"barycentric": list(map(float, s.barycentric)),
				"visibilityPerLight": s.visibility,
				"irradiancePerLight": s.irradiance,
				"radiancePerDir": getattr(s, "radiance_dirs", []),
			}
			for s in samples
		],
	}
	with open(output_path, "w", encoding="utf-8") as f:
		json.dump(out_data, f, indent=2)
	print("[GI] Done.")


# ----------------------------- 命令行接口 -----------------------------


def parse_args():
	"""定义命令行参数。"""
	parser = argparse.ArgumentParser(description="Surface blue-noise sampling + direct-light ray queries")
	parser.add_argument("--mesh-json", required=True, help="Path to Data/meshs/<scene>_mesh.json")
	parser.add_argument("--scene-json", required=True, help="Path to Data/scenes/<scene>_lights.json")
	parser.add_argument("--output", required=True, help="Output samples JSON path, e.g., Data/samples/<scene>_samples.json")
	parser.add_argument("--min-dist", type=float, default=0.1, help="Poisson disk minimum distance")
	parser.add_argument("--num-samples", type=int, default=2000, help="Target sample count")
	parser.add_argument("--directions", type=int, default=64, help="Number of cosine-weighted hemisphere directions (per-sample outputs)")
	parser.add_argument("--bounces", type=int, default=3, help="Max path bounces for indirect lighting")
	parser.add_argument("--albedo", type=float, default=0.8, help="Default fallback albedo (legacy alias of --default-albedo)")
	parser.add_argument("--default-albedo", type=float, default=None, help="Fallback albedo when material/texture data is missing")
	parser.add_argument("--seed", type=int, default=42, help="Random seed for sampling, directions, and tracer")
	parser.add_argument("--dirs-out", type=str, default=None, help="Optional .npy file to store the direction set for SH fitting")
	return parser.parse_args()


def main():
	"""命令行入口。"""
	args = parse_args()
	default_albedo = args.default_albedo if args.default_albedo is not None else args.albedo
	run_sampling(
		mesh_json=args.mesh_json,
		scene_json=args.scene_json,
		output_path=args.output,
		min_dist=args.min_dist,
		num_samples=args.num_samples,
		num_dirs=args.directions,
		max_bounces=args.bounces,
		default_albedo=default_albedo,
		seed=args.seed,
		dirs_out=args.dirs_out,
	)


if __name__ == "__main__":
	main()
