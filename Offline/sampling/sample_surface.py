"""
Surface sampling with blue-noise (Poisson disk) on triangle meshes plus simple
direct-light ray queries. Inputs strictly follow the exported JSON from
SceneLightExporter (lights) and MeshBakeExporter (mesh geometry).

Usage (from repo root):
	python Offline/sampling/sample_surface.py `
  --mesh-json Data/meshs/SampleScene_mesh.json `
  --scene-json Data/scenes/SampleScene_lights.json `
  --output Data/samples/SampleScene_samples_pt.json `
  --min-dist 0.1 `
  --num-samples 200 `
  --directions 64 `
  --bounces 3 `
  --albedo 0.8

Outputs JSON with per-sample position/normal/triangle index/barycentric and a
per-light visibility & irradiance estimate using a minimalist CPU ray tracer.

Dependencies: only Python stdlib + numpy. (No external raytrace libs needed.)
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
from dataclasses import dataclass
from typing import List, Optional, Sequence, Tuple

import numpy as np


# ----------------------------- Data classes -----------------------------


@dataclass
class Vec3:
	x: float
	y: float
	z: float

	def to_np(self) -> np.ndarray:
		return np.array([self.x, self.y, self.z], dtype=np.float32)

	@staticmethod
	def from_dict(d: dict) -> "Vec3":
		# Support both {x,y,z} and {r,g,b} style keys from Unity JSON exports.
		if "x" in d:
			return Vec3(float(d.get("x", 0.0)), float(d.get("y", 0.0)), float(d.get("z", 0.0)))
		if "r" in d:
			return Vec3(float(d.get("r", 0.0)), float(d.get("g", 0.0)), float(d.get("b", 0.0)))
		# Fallback to zeros if keys are missing
		return Vec3(0.0, 0.0, 0.0)


@dataclass
class Light:
	name: str
	type: str  # Directional / Point / Spot (Spot treated as point+cone)
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


@dataclass
class Sample:
	position: np.ndarray
	normal: np.ndarray
	tri_index: int
	barycentric: Tuple[float, float, float]
	visibility: List[float]
	irradiance: List[float]


# ----------------------------- Geometry utils -----------------------------


def load_mesh_triangles(mesh_json: str) -> List[Triangle]:
	with open(mesh_json, "r", encoding="utf-8") as f:
		data = json.load(f)

	tris: List[Triangle] = []
	for obj in data.get("meshObjects", []):
		positions = [Vec3.from_dict(p).to_np() for p in obj["positions"]]
		normals = [Vec3.from_dict(n).to_np() for n in obj["normals"]]
		indices = obj["indices"]
		for t in range(0, len(indices), 3):
			i0, i1, i2 = indices[t : t + 3]
			v0, v1, v2 = positions[i0], positions[i1], positions[i2]
			# Use provided per-vertex normals; fallback to geometric
			n = (normals[i0] + normals[i1] + normals[i2]) / 3.0
			if np.linalg.norm(n) < 1e-6:
				n = np.cross(v1 - v0, v2 - v0)
			n = n / (np.linalg.norm(n) + 1e-8)
			tris.append(Triangle(v0, v1, v2, n, len(tris)))
	return tris


def load_lights(scene_json: str) -> List[Light]:
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


# ----------------------------- Poisson disk on mesh -----------------------------


def triangle_areas(tris: Sequence[Triangle]) -> np.ndarray:
	areas = []
	for tri in tris:
		areas.append(0.5 * np.linalg.norm(np.cross(tri.v1 - tri.v0, tri.v2 - tri.v0)))
	return np.array(areas, dtype=np.float64)


def sample_point_on_triangle(tri: Triangle) -> Tuple[np.ndarray, Tuple[float, float, float]]:
	r1 = random.random()
	r2 = random.random()
	sqrt_r1 = math.sqrt(r1)
	u = 1 - sqrt_r1
	v = r2 * sqrt_r1
	w = 1 - u - v
	p = u * tri.v0 + v * tri.v1 + w * tri.v2
	return p, (u, v, w)


def build_spatial_hash(samples: List[np.ndarray], cell_size: float):
	grid = {}
	for idx, p in enumerate(samples):
		key = tuple((p / cell_size).astype(int))
		grid.setdefault(key, []).append(idx)
	return grid


def is_far_enough(candidate: np.ndarray, samples: List[np.ndarray], grid, cell_size: float, min_dist: float) -> bool:
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
	areas = triangle_areas(tris)
	cdf = np.cumsum(areas)
	total_area = cdf[-1]
	samples: List[np.ndarray] = []
	outputs = []
	cell_size = min_dist / math.sqrt(3)  # tighter grid for 3D Poisson
	grid = {}
	max_trials = target_count * 50  # dart throwing budget

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


# ----------------------------- Ray tracing (BVH + Möller–Trumbore) -----------------------------


@dataclass
class BVHNode:
	bounds_min: np.ndarray
	bounds_max: np.ndarray
	left: Optional["BVHNode"]
	right: Optional["BVHNode"]
	tri_indices: List[int]


def build_bvh(tris: List[Triangle], indices: Optional[List[int]] = None, depth: int = 0) -> BVHNode:
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
	inv = 1.0 / (dir + 1e-12)
	t0 = (bmin - orig) * inv
	t1 = (bmax - orig) * inv
	tmin = np.maximum.reduce(np.minimum(t0, t1))
	tmax = np.minimum.reduce(np.maximum(t0, t1))
	return tmax >= max(tmin, 0.0)


def ray_triangle_intersect(orig, dir, tri: Triangle, t_min=1e-4, t_max=1e9) -> Optional[float]:
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
	return ray_triangle_intersect(orig, dir, tri, t_min, t_max)


def bvh_intersect(node: BVHNode, tris: List[Triangle], orig, dir, t_min=1e-4, t_max=1e9) -> bool:
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
	# Returns (t, tri_index) of the closest hit, or None.
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


# ----------------------------- Lighting -----------------------------


def cosine_hemisphere_samples(num_dirs: int) -> np.ndarray:
	# Stratified cosine-weighted samples using concentric mapping
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
	# Single random cosine-weighted sample on hemisphere
	u1 = random.random()
	u2 = random.random()
	r = math.sqrt(u1)
	theta = 2 * math.pi * u2
	x = r * math.cos(theta)
	y = r * math.sin(theta)
	z = math.sqrt(max(0.0, 1 - u1))
	return np.array([x, y, z], dtype=np.float32)


def orthonormal_basis(n: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
	if abs(n[0]) > abs(n[2]):
		tangent = np.array([-n[1], n[0], 0.0])
	else:
		tangent = np.array([0.0, -n[2], n[1]])
	tangent /= np.linalg.norm(tangent) + 1e-8
	bitangent = np.cross(n, tangent)
	return tangent, bitangent


def world_from_tangent(normal: np.ndarray, local_dir: np.ndarray) -> np.ndarray:
	t, b = orthonormal_basis(normal)
	return local_dir[0] * t + local_dir[1] * b + local_dir[2] * normal


def path_trace(pos: np.ndarray, normal: np.ndarray, view_dir_local: np.ndarray, lights: List[Light], bvh: BVHNode, tris: List[Triangle], max_bounces: int = 3, albedo: float = 0.8) -> np.ndarray:
	# Simple Lambertian path tracer with next-event estimation (direct light) and cosine sampling.
	throughput = np.array([albedo, albedo, albedo], dtype=np.float32)
	radiance = np.zeros(3, dtype=np.float32)

	# Treat the input point as the first surface hit
	hit_pos = pos
	hit_normal = normal
	ray_dir = world_from_tangent(normal, view_dir_local)

	for bounce in range(max_bounces):
		# Direct lighting at current hit
		Ld = direct_radiance(hit_pos, hit_normal, lights, bvh, tris)
		radiance += throughput * Ld

		# Russian roulette (after 2 bounces)
		if bounce >= 2:
			p = 0.9
			if random.random() > p:
				break
			throughput /= p

		# Sample new diffuse direction (cosine-weighted)
		new_dir_local = sample_cosine_hemisphere()
		new_dir_world = world_from_tangent(hit_normal, new_dir_local)

		# Trace to next surface
		ray_origin = hit_pos + hit_normal * 1e-4
		hit = bvh_first_hit(bvh, tris, ray_origin, new_dir_world, 1e-4, 1e9)
		if hit is None:
			break  # escaped to environment (not modeled)
		t_hit, tri_idx = hit
		tri = tris[tri_idx]
		hit_pos = ray_origin + new_dir_world * t_hit
		hit_normal = tri.normal

		# Update throughput for diffuse bounce
		throughput *= albedo

	return radiance


def compute_direct_lighting(sample: Sample, lights: List[Light], bvh: BVHNode, tris: List[Triangle]) -> None:
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
		else:  # Point/Spot treated similarly for now (no angle falloff applied)
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
	# Returns RGB direct lighting at a surface point (Lambert, no texture), with shadow checks.
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


# ----------------------------- Pipeline -----------------------------


def run_sampling(mesh_json: str, scene_json: str, output_path: str, min_dist: float, num_samples: int, num_dirs: int, max_bounces: int, albedo: float):
	print(f"[WishGI] Loading mesh from {mesh_json}")
	tris = load_mesh_triangles(mesh_json)
	print(f"[WishGI] Triangles: {len(tris)}")

	print(f"[WishGI] Loading lights from {scene_json}")
	lights = load_lights(scene_json)
	print(f"[WishGI] Lights: {len(lights)}")

	print(f"[WishGI] Building BVH for ray queries...")
	bvh = build_bvh(tris)

	print(f"[WishGI] Blue-noise sampling on surface: target {num_samples}, minDist {min_dist}")
	pts = blue_noise_sample(tris, target_count=num_samples, min_dist=min_dist)
	print(f"[WishGI] Accepted samples: {len(pts)}")

	print(f"[WishGI] Generating cosine-weighted directions: {num_dirs}")
	dirs_local = cosine_hemisphere_samples(num_dirs)

	samples: List[Sample] = []
	for p, tri, bary in pts:
		s = Sample(position=p, normal=tri.normal, tri_index=tri.index, barycentric=bary, visibility=[], irradiance=[])
		compute_direct_lighting(s, lights, bvh, tris)
		s.radiance_dirs = []  # type: ignore[attr-defined]

		# Path tracing per precomputed local direction set
		radiance_per_dir = []
		for d_local in dirs_local:
			radiance = path_trace(p, tri.normal, d_local, lights, bvh, tris, max_bounces=max_bounces, albedo=albedo)
			radiance_per_dir.append([float(radiance[0]), float(radiance[1]), float(radiance[2])])
		s.radiance_dirs = radiance_per_dir
		samples.append(s)

	os.makedirs(os.path.dirname(output_path), exist_ok=True)
	print(f"[WishGI] Writing samples -> {output_path}")
	out_data = {
		"mesh": os.path.basename(mesh_json),
		"scene": os.path.basename(scene_json),
		"minDist": min_dist,
		"numSamples": len(samples),
		"numDirections": num_dirs,
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
	print("[WishGI] Done.")


# ----------------------------- CLI -----------------------------


def parse_args():
	parser = argparse.ArgumentParser(description="Surface blue-noise sampling + direct-light ray queries")
	parser.add_argument("--mesh-json", required=True, help="Path to Data/meshs/<scene>_mesh.json")
	parser.add_argument("--scene-json", required=True, help="Path to Data/scenes/<scene>_lights.json")
	parser.add_argument("--output", required=True, help="Output samples JSON path, e.g., Data/samples/<scene>_samples.json")
	parser.add_argument("--min-dist", type=float, default=0.1, help="Poisson disk minimum distance")
	parser.add_argument("--num-samples", type=int, default=2000, help="Target sample count")
	parser.add_argument("--directions", type=int, default=64, help="Number of cosine-weighted hemisphere directions (per-sample outputs)")
	parser.add_argument("--bounces", type=int, default=3, help="Max path bounces for indirect lighting")
	parser.add_argument("--albedo", type=float, default=0.8, help="Lambert albedo (0-1), gray")
	return parser.parse_args()


def main():
	args = parse_args()
	run_sampling(
		mesh_json=args.mesh_json,
		scene_json=args.scene_json,
		output_path=args.output,
		min_dist=args.min_dist,
		num_samples=args.num_samples,
		num_dirs=args.directions,
		max_bounces=args.bounces,
		albedo=args.albedo,
	)


if __name__ == "__main__":
	main()
