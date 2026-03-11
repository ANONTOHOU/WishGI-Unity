# WishGI-Unity

基于论文《WishGI: Lightweight Static Global Illumination Baking via Spherical Harmonics Fitting》的 Unity URP 工程化实现。

## 项目目标

- 在离线阶段完成静态全局光照采样、Probe 分布、SH 拟合与数据打包。
- 在 Unity 中导入 ProbeMap 和顶点关联数据（uv2），并在 URP Shader 中实时重建 GI。
- 在保证画质可用的前提下降低内存占用与运行时开销。

## 技术栈

- Unity URP（C# Editor 工具、Shader）
- Python + NumPy（离线采样、拟合、打包）
- HLSL（SH 重建）

## 代码结构

- `Offline/sampling/`：表面采样与路径追踪数据生成
- `Offline/export/`：Probe 聚类与 sample/vertex 权重导出
- `Offline/baking/`：SH 基函数、线性回归拟合、ProbeMap 打包
- `Data/`：离线输入输出（mesh、samples、probes）
- `UnityProjectBake/Assets/WishGI/Editor/`：Unity 导入与验证工具
- `UnityProjectBake/Assets/WishGI/Shaders/`：URP 运行时 Shader
- `Docs/`：流程说明文档

## 流程概览

1. 采样：从网格表面采样点并计算每方向辐亮度。
2. Probe 分布：K-means 生成 probe，并得到 sample/vertex 到 probe 的 top-k 权重。
3. SH 拟合：用带正则的最小二乘求每个 probe 的 SH 系数。
4. 打包：将系数写入 1D RGBAFloat 布局的 ProbeMap（`.npy`）。
5. Unity 导入：生成 Texture2D、写入 mesh uv2。
6. 运行时：Shader 解码 probe 系数并计算最终 GI 发光项。

## 快速开始

以下命令在仓库根目录执行。

### 1) 采样

```powershell
python Offline/sampling/sample_surface.py `
	--mesh-json Data/meshs/SampleScene_mesh.json `
	--scene-json Data/scenes/SampleScene_lights.json `
	--output Data/samples/SampleScene_samples_pt.json `
	--min-dist 0.1 --num-samples 200 --directions 64 `
	--bounces 3 --albedo 0.8 --seed 42 `
	--dirs-out Data/samples/SampleScene_dirs.npy
```

### 2) 导出 Probe 与权重

```powershell
python Offline/export/export_probes.py `
	--samples-json Data/samples/SampleScene_samples_pt.json `
	--mesh-json Data/meshs/SampleScene_mesh.json `
	--probes 16 --top-k-sample 4 --top-k-vertex 2 `
	--output-dir Data/probes
```

### 3) SH 拟合

```powershell
python Offline/baking/fit_sh.py `
	--samples-json Data/samples/SampleScene_samples_pt.json `
	--sample-weights Data/probes/sample_weights.json `
	--order 2 --lambda-reg 1e-4 `
	--output-npy Data/probes/probes_sh.npy `
	--output-json Data/probes/probes_sh.json `
	--dirs-npy Data/samples/SampleScene_dirs.npy
```

### 4) 打包 ProbeMap

```powershell
python Offline/baking/pack_probes.py `
	--probes-npy Data/probes/probes_sh.npy `
	--order 2 `
	--output-tex Data/probes/probe_map.npy `
	--output-meta Data/probes/probe_map_meta.json
```

## Unity 使用说明（URP）

1. 打开 `UnityProjectBake` 工程。
2. 菜单 `WishGI/Probe Importer`：导入 `probe_map.npy` + `probe_map_meta.json` 生成 Probe 纹理资产。
3. 在同一窗口导入 `mesh_assoc.json`，将顶点关联写入目标 Mesh 的 uv2。
4. 菜单 `WishGI/UV2 Inspector` 可验证 uv2 是否写入正确。
5. 使用 `WishGI/UnlitProbe` Shader 或接入 Shader Graph 的 Custom Function。

## 关键输出格式

- `sample_weights.json`：每个采样点对 probe 的稀疏权重（行归一化）。
- `mesh_assoc.json`：每顶点 top-k probe 索引与权重，用于 uv2。
- `probes_sh.npy`：形状 `(probeCount, 9, 3)`（L=2）。
- `probe_map.npy`：形状 `(1, width, 4)`，其中 `width = probeCount * texelsPerProbe`。

## 常见问题

- 看到明暗条纹：优先检查 Probe 纹理是否为 `RGBAFloat + Point + Clamp`，并确认 `_ProbeCount` 与 `_TexelsPerProbe` 参数正确。
- uv2 看起来不对：使用 `WishGI/UV2 Inspector` 检查解码索引与权重是否合理。

## 参考文档

- 完整流程手册：`Docs/pipeline_handbook.md`
- 论文摘要：`Docs/论文md.md`