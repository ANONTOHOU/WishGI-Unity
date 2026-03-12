# WishGI 128-Probe Baking Command Reference

如果您的场景较为复杂（或希望提升全局光照的细节精度），可以增加探针的数量。这里提供了一份将探针数量提升到 **128** 个并相应增加采样点密度的离线烘焙指令，您可以直接在命令行工具（位于工程根目录）中复制运行。

由于探针数量大幅增加，下面指令中也同步提高了对该场景的采样点数量 `--num-samples`，以确保充足的高质量信息来支撑 128 个探针的 SH 权重拟合。

```powershell
# 1) 采样 (增加至 1024 个采样点以匹配更多探针的信息量）
python Offline/sampling/sample_surface.py --mesh-json Data/meshs/SampleScene_mesh.json --scene-json Data/scenes/SampleScene_lights.json --output Data/samples/SampleScene_samples_pt.json --min-dist 0.05 --num-samples 1024 --directions 64 --bounces 3 --albedo 0.8 --seed 42 --dirs-out Data/samples/SampleScene_dirs.npy

# 2) 导出 Probe 与权重 (指定 128 个 Probe)
python Offline/export/export_probes.py --samples-json Data/samples/SampleScene_samples_pt.json --mesh-json Data/meshs/SampleScene_mesh.json --probes 128 --top-k-sample 4 --top-k-vertex 2 --output-dir Data/probes

# 3) SH 拟合 (对所有 128 个探针带有余弦权重的全局线性回归求解)
python Offline/baking/fit_sh.py --samples-json Data/samples/SampleScene_samples_pt.json --sample-weights Data/probes/sample_weights.json --order 2 --lambda-reg 1e-4 --output-npy Data/probes/probes_sh.npy --output-json Data/probes/probes_sh.json --dirs-npy Data/samples/SampleScene_dirs.npy

# 4) 打包 ProbeMap (生成更长的一维包含所有探针浮点精度的贴图)
python Offline/baking/pack_probes.py --probes-npy Data/probes/probes_sh.npy --order 2 --output-tex Data/probes/probe_map.npy --output-meta Data/probes/probe_map_meta.json
```

完成上述执行后，回到 Unity 工程中打开 `WishGI/Probe Importer` 重新执行导入即可。生成的 `probe_map.npy` 的贴图宽度会自动延长适应（$128 \times 7 = 896$ Texels）。