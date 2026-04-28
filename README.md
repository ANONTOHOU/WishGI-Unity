# WishGI-Unity

基于论文《WishGI: Lightweight Static Global Illumination Baking via Spherical Harmonics Fitting》的 Unity URP 工程化实现。

## 项目目标

- 在离线阶段完成静态全局光照采样、Probe 分布、SH 拟合与数据打包。
- 在 Unity 中一键完成“场景数据导出 -> 离线计算 -> ProbeMap 纹理生成 -> 材质与 Mesh 自动绑定”的全流程。
- 在保证画质可用的前提下，使用球谐函数（SH）和低频离线探针极大地降低内存占用与运行时开销。

## 技术栈

- Unity URP（C# Editor 集成工具、Shader）
- Python + NumPy（离线采样、拟合、打包，支持 Pillow 读取纹理反照率）
- HLSL（SH 运行时重建与 BaseColor 调制）

## 代码结构

- `Offline/sampling/`：表面采样与路径追踪数据生成（支持材质/纹理图采样和回退）
- `Offline/export/`：Probe 聚类与 sample/vertex 权重导出
- `Offline/baking/`：SH 基函数、线性回归拟合、ProbeMap 打包
- `Data/`：离线输入输出（mesh、lights、samples、probes）
- `UnityProjectBake/Assets/WishGI/Editor/`：Unity 统一烘焙面板与集成验证工具
- `UnityProjectBake/Assets/WishGI/Shaders/`：URP 运行时 Shader (`WishGIUnlit.shader`)
- `Docs/`：流程说明文档

## 🎮 全流程使用指南（从搭建场景到烘焙回写）

借助于最新集成的 `GI Baking Tool` 面板，现在可以在 Unity 内一键完成所有操作。

### 1. 场景准备 (Scene Setup)
1. **放置模型与光照**：在场景中放置你的 3D 模型和光源（支持 Directional Light 和 Point Light）。
2. **标记静态**：将需要参与 GI 烘焙（既产生反弹也接收 GI）的 GameObject 的 `Static` 标志勾选上（至少需要开启 `Contribute GI`）。
3. **分配材质**：为物体分配 `WishGI/Unlit` Shader 的材质。
   - 调整材质的 `Base Map` 和 `Base Color`。
   - 在烘焙追踪阶段，离线脚本会自动读取这里的 Base Color 和贴图作为反照率（Albedo）进行光线反弹计算。

### 2. 打开烘焙面板 (Baking Tool)
- 在 Unity 顶部菜单栏点击 **`GI -> Baking Tool`** 打开统一控制面板。
- 在面板中可以配置：
  - **Quality Preset**：选择 `Low`、`High` 或 `Custom`（自定义采样点、连线和探针数量）。
  - **Shared Parameters**：设置最大反弹次数（Bounces）、光线追踪随机种子（Seed）和未找到贴图时的默认反照率（Default Albedo）。
  - 面板下方会根据当前参数实时显示**预计耗时 (Estimated Time)**。

### 3. 一键执行烘焙 (Run All Pipeline)
在 Tool 的 **Integration** 板块，保持相关选项勾选：
- `Run Step0: Generate UV2`：为没有 UV2 的网格自动展开并生成光照贴图 UV。
- `Run Step1: Export Scene Lights`：将当前场景灯光导出为 JSON。
- `Run Step2: Export Mesh Bake Data`：将场景中的静态模型（包含材质和 UV0 信息）导出为 JSON。
- `Auto Apply To Unity After Bake`：离线 Python 计算完成后，自动将成果应用回调回 Unity。
- `Auto Assign ProbeMap To GI Materials`：回写完成后，自动为所有参与 GI 的材质分配生成的 ProbeMap，并填入正确的 Probe 参数。

**点击 `0. Run All (Step0-4 + Apply)` 按钮**：
1. 进度条会平滑显示当前所处的阶段（导出光源 -> 导出网格 -> 表面采样与追踪 -> 探针聚类 -> SH 拟合 -> 纹理打包 -> 回写资源）。
2. 在此过程中，原始网格会被克隆为同名的 `_Baked.asset` 存放到 `Assets/WishGI/Resources/BakedMeshes` 下，修改这类克隆网格的 UV2 以保证持久化，不破坏原始导入模型。

### 4. 运行时着色器设置 (Shader & Material)
烘焙结束后，如果勾选了自动绑定，材质会自动刷新，场景效果立即更新。如果需要手动调整：
- **Shader**：确保使用 `WishGI/Unlit`。
- **Texture**: `_ProbeMap` 会被赋值为刚刚生成的 `.asset` 后缀的 1D RGBAFloat 探针纹理。
- **Parameters**: 
  - `_ProbeCount`：场景生成的探针总数。
  - `_TexelsPerProbe`：每个探针占用的像素宽（固定为 9，对应 9 个 SH 系数）。
- **GI 强度**：调节材质的 `GI Intensity` 来控制 GI 乘算效果的亮度。

---

## 手动离线执行参考

如果你想彻底脱离 Unity Editor，或者在构建机上运行 Python 脚本，可以按照以下顺序手动调用命令行，这是 `Run All` 按钮在后台所执行的流线。

### 1) 采样 (Sample Surface & Raytracing)
```powershell
python Offline/sampling/sample_surface.py --mesh-json Data/meshs/SampleScene_mesh.json --scene-json Data/scenes/SampleScene_lights.json --output Data/samples/SampleScene_samples_pt.json --min-dist 0.05 --num-samples 1024 --directions 960 --bounces 3 --default-albedo 0.8 --seed 42 --dirs-out Data/samples/SampleScene_dirs.npy
```

### 2) 导出 Probe 与权重 (Clustering)
```powershell
python Offline/export/export_probes.py --samples-json Data/samples/SampleScene_samples_pt.json --mesh-json Data/meshs/SampleScene_mesh.json --probes 128 --top-k-sample 4 --top-k-vertex 2 --output-dir Data/probes
```

### 3) SH 拟合 (SH Fitting)
```powershell
python Offline/baking/fit_sh.py --samples-json Data/samples/SampleScene_samples_pt.json --sample-weights Data/probes/sample_weights.json --order 2 --lambda-reg 0.1 --output-npy Data/probes/probes_sh.npy --output-json Data/probes/probes_sh.json --dirs-npy Data/samples/SampleScene_dirs.npy
```

### 4) 打包 ProbeMap (Packing)
```powershell
python Offline/baking/pack_probes.py --probes-npy Data/probes/probes_sh.npy --order 2 --output-tex Data/probes/probe_map.npy --output-meta Data/probes/probe_map_meta.json
```

## 关键输出格式

- `sample_weights.json`：每个采样点对 probe 的稀疏权重（行归一化）。
- `mesh_assoc.json`：每顶点 top-k probe 索引与权重，用于 uv2 绑定。
- `probes_sh.npy`：探针的 SH 系数张量，形状 `(probeCount, 9, 3)`（对于 2 阶 SH）。
- `probe_map.npy`：最终用于着色器采样的探针贴图张量，形状 `(1, width, 4)`，其中 `width = probeCount * texelsPerProbe`。

## 常见问题

- **看到明暗条纹或极度不自然的色块**：优先检查 Probe 纹理导入 Unity 后是否保持了 `RGBAFloat / Point / Clamp` 格式并禁用了 sRGB（工具已自动处理，但手动改动可能破坏）。同时确认材质上的 `_ProbeCount` 和 `_TexelsPerProbe` 参数与 `_meta.json` 一致。
- **重新打开 Unity 后 GI 效果丢失**：现在插件会自动把网格克隆为 `_Baked.asset` 存于工程目录内。如果仍有丢失，请检查场景保存状态，以及是否意外重置了 `MeshFilter.sharedMesh` 引用。
