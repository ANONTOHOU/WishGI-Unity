python Offline/sampling/sample_surface.py --mesh-json Data/meshs/SampleScene_mesh.json --scene-json Data/scenes/SampleScene_lights.json --output Data/samples/SampleScene_samples_pt.json --min-dist 0.05 --num-samples 256 --directions 64 --bounces 3 --albedo 0.8 --seed 42 --dirs-out Data/samples/SampleScene_dirs.npy

python Offline/export/export_probes.py --samples-json Data/samples/SampleScene_samples_pt.json --mesh-json Data/meshs/SampleScene_mesh.json --probes 16 --top-k-sample 4 --top-k-vertex 2 --output-dir Data/probes

python Offline/baking/fit_sh.py --samples-json Data/samples/SampleScene_samples_pt.json --sample-weights Data/probes/sample_weights.json --order 2 --lambda-reg 1e-4 --output-npy Data/probes/probes_sh.npy --output-json Data/probes/probes_sh.json --dirs-npy Data/samples/SampleScene_dirs.npy

python Offline/baking/pack_probes.py --probes-npy Data/probes/probes_sh.npy --order 2 --output-tex Data/probes/probe_map.npy --output-meta Data/probes/probe_map_meta.json
```