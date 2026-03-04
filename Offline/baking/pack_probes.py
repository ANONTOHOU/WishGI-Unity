"""
Pack probe SH coefficients into a RGBA float texture layout (1D strip).

Input: probes_sh.npy produced by fit_sh.py (shape: P x C x 3, C=(order+1)^2)
Output: .npy texture array of shape (1, width, 4) where width = P * texels_per_probe.
Also emits a small metadata JSON for Unity importer/reference.

Usage (from repo root):

python Offline/baking/pack_probes.py `
  --probes-npy Data/probes/probes_sh.npy `
  --order 2 `
  --output-tex Data/probes/probe_map.npy `
  --output-meta Data/probes/probe_map_meta.json

Texture layout:
- texels_per_probe = ceil((C*3)/4). For order=2 (C=9) this is 7 texels/probe.
- Flatten order per probe: [c0.r, c0.g, c0.b, c1.r, c1.g, c1.b, ...], padded with zeros to 4-float texel blocks.
- Probe p occupies texels [p*texels_per_probe, (p+1)*texels_per_probe-1] on the X axis, Y=0.
"""

from __future__ import annotations

import argparse
import json
import math
import os
from typing import Tuple

import numpy as np


def infer_order(num_basis: int) -> int:
    r = int(round(math.sqrt(num_basis)))
    if r * r != num_basis:
        raise ValueError(f"Cannot infer SH order from basis count={num_basis}")
    return r - 1


def pack_coeffs_to_texture(coeffs: np.ndarray, order: int) -> Tuple[np.ndarray, dict]:
    """Pack (P, C, 3) coeffs into (1, width, 4) texture strip."""
    if coeffs.ndim != 3 or coeffs.shape[2] != 3:
        raise ValueError(f"coeffs must be (P, C, 3); got {coeffs.shape}")

    num_probes, num_basis, _ = coeffs.shape
    if (order + 1) * (order + 1) != num_basis:
        raise ValueError(f"order mismatch: order={order} implies {(order+1)**2} basis, got {num_basis}")

    floats_per_probe = num_basis * 3
    texels_per_probe = math.ceil(floats_per_probe / 4)
    width = texels_per_probe * num_probes
    height = 1

    tex = np.zeros((height, width, 4), dtype=np.float32)
    flat = coeffs.reshape(num_probes, floats_per_probe)

    for p in range(num_probes):
        for t in range(texels_per_probe):
            base_f = t * 4
            src_slice = flat[p, base_f : base_f + 4]
            dst_x = p * texels_per_probe + t
            tex[0, dst_x, : len(src_slice)] = src_slice

    meta = {
        "order": order,
        "num_probes": int(num_probes),
        "num_basis": int(num_basis),
        "floats_per_probe": int(floats_per_probe),
        "texels_per_probe": int(texels_per_probe),
        "width": int(width),
        "height": int(height),
        "layout": "probe p occupies texels [p*texels_per_probe, (p+1)*texels_per_probe-1] on X, Y=0",
        "flatten_order": "[c0.r, c0.g, c0.b, c1.r, c1.g, c1.b, ...], padded to 4 floats per texel",
    }
    return tex, meta


def parse_args():
    parser = argparse.ArgumentParser(description="Pack probe SH coeffs into RGBA float texture strip (.npy)")
    parser.add_argument("--probes-npy", required=True, help="Path to probes_sh.npy from fit_sh.py (shape: P x C x 3)")
    parser.add_argument("--order", type=int, default=None, help="SH order; if omitted, inferred from C")
    parser.add_argument("--output-tex", required=True, help="Output .npy texture file (shape (1, width, 4))")
    parser.add_argument("--output-meta", default=None, help="Optional metadata JSON for importer")
    return parser.parse_args()


def main():
    args = parse_args()
    coeffs = np.load(args.probes_npy)
    if coeffs.ndim != 3:
        raise ValueError(f"Expected coeffs rank-3 (P,C,3); got {coeffs.shape}")
    num_basis = coeffs.shape[1]
    order = args.order if args.order is not None else infer_order(num_basis)

    tex, meta = pack_coeffs_to_texture(coeffs, order)

    os.makedirs(os.path.dirname(args.output_tex), exist_ok=True)
    np.save(args.output_tex, tex.astype(np.float32))
    if args.output_meta:
        with open(args.output_meta, "w", encoding="utf-8") as f:
            json.dump(meta, f, indent=2)
    print(f"[pack_probes] Probes={meta['num_probes']}, order={order}, texels/probe={meta['texels_per_probe']}, width={meta['width']}")
    print(f"[pack_probes] Saved texture npy -> {args.output_tex}" + (f" and meta -> {args.output_meta}" if args.output_meta else ""))


if __name__ == "__main__":
    main()
