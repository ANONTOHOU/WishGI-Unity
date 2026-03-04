# WishGI Pipeline Handbook (Steps 1-5 implemented)

This document condenses the current offline + Unity pipeline so any engineer or AI can pick up and run it. It follows the numbered steps from 《每环节规划》.

## 0) Global conventions
- SH order L=2 (9 coeffs/channel, 27 floats per probe).
- Probe texture: RGBAFloat 1D strip; 7 texels/probe when L=2.
- Mesh assoc: top-2 probes per vertex stored in uv2 = (i0_norm, w0, i1_norm, w1), indices normalized by (probeCount-1).
- All math in linear color space.

## 1) Surface sampling + path tracing (inputs for SH fit)
- Code: [Offline/sampling/sample_surface.py](Offline/sampling/sample_surface.py)
- Purpose: surface Poisson sampling, cosine directions, simple BVH path tracing (Lambert, direct + diffuse bounces).
- Inputs: mesh JSON (positions/normals/indices), scene lights JSON.
- Outputs: `Data/samples/<scene>_samples_pt.json` + optional `Data/samples/<scene>_dirs.npy`.
- Key fields in samples JSON:
  - `numSamples`, `numDirections`, `seed`.
  - `dirsLocal`: array of hemisphere dirs (x,y,z) (present when using updated script or pass --dirs-out).
  - `samples[]`: position, normal, triangleIndex, barycentric, visibilityPerLight, irradiancePerLight, `radiancePerDir` (D x 3).
- How to run (example):
```
python Offline/sampling/sample_surface.py \
  --mesh-json Data/meshs/SampleScene_mesh.json \
  --scene-json Data/scenes/SampleScene_lights.json \
  --output Data/samples/SampleScene_samples_pt.json \
  --min-dist 0.1 --num-samples 200 --directions 64 \
  --bounces 3 --albedo 0.8 --seed 42 \
  --dirs-out Data/samples/SampleScene_dirs.npy
```

## 2) Probe clustering + weights (samples→probes, vertices→probes)
- Code: [Offline/export/export_probes.py](Offline/export/export_probes.py)
- Purpose: K-means cluster probes, build W for samples, build vertex→probe assoc.
- Inputs: samples JSON (positions), mesh JSON (vertices), probe count K.
- Outputs (to chosen output dir):
  - `probes.json`: list of {probe_id, position{x,y,z}, space}.
  - `sample_weights.json`: sparse rows per sample: {sample_id, probes:[{id,w}]}, plus num_samples/num_probes/top_k_sample.
  - `mesh_assoc.json`: per mesh name, per vertex {vertex_id, probes:[{id,w}]}, plus top_k_vertex.
- Weighting: top-K nearest probes by inverse-distance, rows normalized.
- How to run (example):
```
python Offline/export/export_probes.py \
  --samples-json Data/samples/SampleScene_samples_pt.json \
  --mesh-json Data/meshs/SampleScene_mesh.json \
  --probes 16 \
  --top-k-sample 4 \
  --top-k-vertex 2 \
  --output-dir Data/probes
```

## 3) SH fitting (per-probe coefficients)
- Code: [Offline/baking/fit_sh.py](Offline/baking/fit_sh.py), uses [Offline/baking/sh_basis.py](Offline/baking/sh_basis.py) and [Offline/baking/loss.py](Offline/baking/loss.py).
- Purpose: solve SH coeffs for each probe via ridge-regularized least squares.
- Inputs: samples JSON (with radiancePerDir), sample_weights.json, directions (either dirsLocal in samples JSON or --dirs-npy), SH order.
- Outputs: `probes_sh.npy` (shape P x C x 3, float32), optional `probes_sh.json` for inspection.
- How to run (example L=2, λ=1e-4):
```
python Offline/baking/fit_sh.py \
  --samples-json Data/samples/SampleScene_samples_pt.json \
  --sample-weights Data/probes/sample_weights.json \
  --order 2 --lambda-reg 1e-4 \
  --output-npy Data/probes/probes_sh.npy \
  --output-json Data/probes/probes_sh.json \
  --dirs-npy Data/samples/SampleScene_dirs.npy
```
- Solver detail: builds design matrix A (rows = samples×dirs, cols = probes×coeffs); solves (A^T A + λI)x = A^T b per RGB.

## 4) Pack probe coefficients to texture
- Code: [Offline/baking/pack_probes.py](Offline/baking/pack_probes.py).
- Purpose: flatten `probes_sh.npy` into a 1D RGBA float strip (saved as .npy) and emit metadata.
- Inputs: probes_sh.npy, SH order (optional; inferred if omitted).
- Outputs: `probe_map.npy` (shape 1 x width x 4), `probe_map_meta.json` (order, num_probes, texels_per_probe, width, layout notes).
- Layout: per probe, flattened [c0.r, c0.g, c0.b, c1.r, ...] padded to 4-float texels; probe p spans texels [p*texelsPerProbe, (p+1)*texelsPerProbe-1] on X, Y=0. For L=2, texelsPerProbe=7.
- How to run (example):
```
python Offline/baking/pack_probes.py \
  --probes-npy Data/probes/probes_sh.npy \
  --order 2 \
  --output-tex Data/probes/probe_map.npy \
  --output-meta Data/probes/probe_map_meta.json
```

## 5) Unity import (to be executed in Editor)
- Inputs needed: `probes.json` (positions if needed for debugging), `probe_map.npy` + `probe_map_meta.json`, `mesh_assoc.json`.
- Expected actions (Editor scripts not yet included in repo):
  - Load `probe_map.npy` and create `Texture2D` RGBAFloat width = num_probes * texelsPerProbe, height = 1. Fill pixel-by-pixel from the numpy array.
  - Read `probe_map_meta.json` to know texelsPerProbe/order/probe count.
  - Load `mesh_assoc.json`, for each mesh write uv2 = (i0_norm, w0, i1_norm, w1) where i_norm = probeIndex/(probeCount-1).
  - Save assets under Assets/WishGI/Resources (or desired path).
- Sanity check: read back first probe texels and compare to `probes_sh.npy` values.

## 6) Runtime shader (URP ShaderGraph or HLSL)
- Inputs at runtime: ProbeMap (RGBAFloat), probeCount, texelsPerProbe, per-vertex uv2.
- Per-pixel steps: decode i0/i1 from uv2, fetch 7 texels per probe (L=2) → 27 floats, combine by weights, evaluate SH basis for surface normal, output emission.
- Optional optimization: do probe fetch in vertex stage, interpolate SH to fragment.

## Data format quick reference
- `samples_pt.json`: contains dirsLocal (optional), numDirections, and samples[]. Each sample has radiancePerDir (D x 3 floats), position/normal/triangleIndex/barycentric, visibilityPerLight, irradiancePerLight.
- `sample_weights.json`: num_samples, num_probes, top_k_sample, weights[] with {sample_id, probes:[{id,w}]}, rows normalized.
- `mesh_assoc.json`: per mesh {mesh_name, vertex_count, top_k_vertex, vertices:[{vertex_id, probes:[{id,w}]}]}, rows normalized.
- `probes_sh.npy`: array (P, C, 3) float32, C=(order+1)^2.
- `probe_map.npy`: array (1, width, 4) float32; width = P * texelsPerProbe.
- `probe_map_meta.json`: order, num_probes, texels_per_probe, width, height, layout strings.

## End-to-end minimal command chain (SampleScene example)
1) Sampling:
```
python Offline/sampling/sample_surface.py --mesh-json Data/meshs/SampleScene_mesh.json --scene-json Data/scenes/SampleScene_lights.json --output Data/samples/SampleScene_samples_pt.json --min-dist 0.1 --num-samples 200 --directions 64 --bounces 3 --albedo 0.8 --seed 42 --dirs-out Data/samples/SampleScene_dirs.npy
```
2) Probes + weights:
```
python Offline/export/export_probes.py --samples-json Data/samples/SampleScene_samples_pt.json --mesh-json Data/meshs/SampleScene_mesh.json --probes 16 --top-k-sample 4 --top-k-vertex 2 --output-dir Data/probes
```
3) SH fit:
```
python Offline/baking/fit_sh.py --samples-json Data/samples/SampleScene_samples_pt.json --sample-weights Data/probes/sample_weights.json --order 2 --lambda-reg 1e-4 --output-npy Data/probes/probes_sh.npy --output-json Data/probes/probes_sh.json --dirs-npy Data/samples/SampleScene_dirs.npy
```
4) Pack texture:
```
python Offline/baking/pack_probes.py --probes-npy Data/probes/probes_sh.npy --order 2 --output-tex Data/probes/probe_map.npy --output-meta Data/probes/probe_map_meta.json
```
5) Unity import: use Editor utility (to be added) to load probe_map.npy/meta and mesh_assoc.json, write uv2 and create RGBAFloat texture.

## Notes and next steps
- Editor importer and runtime shader are described in 《每环节规划》，but implementation is not yet in repo. Follow Section 6/7 there to finish the Unity side.
- Regularization λ can be tuned; increase if fit is noisy.
- texelsPerProbe scales with SH order: texelsPerProbe = ceil(((order+1)^2 * 3)/4).
- All scripts are pure Python + numpy (no extra deps).
