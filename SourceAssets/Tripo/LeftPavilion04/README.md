# LeftPavilion_H31_8K

Single-building Tripo H3.1 replacement generated from the original scene reference.

- Building ID: `LeftPavilion04`
- Scene target: `TianNanGe_Blockout/Architecture/LeftPavilion`
- Model: `v3.1-20260211`, PBR, `texture_quality=extreme`, one candidate.
- Actual Tripo credits: 55 (reference 5 + model 50).
- FBX conversion is local; mesh UVs and source texture dimensions are preserved.
- HDRP MaskMap: R = metallic, G/B = 1, A = 1 − roughness.

Native texture dimensions:

- Material 0 base_color: 8192 × 8192.
- Material 0 normal: 4096 × 4096.
- Material 0 metallic_roughness: 4096 × 4096.

The extreme setting requests 8K color; report each delivered PBR map's native dimensions rather than upscaling it.

Geometry: 1,441,582 triangles.

Reproduction records:

- `SourceAssets/Tripo/LeftPavilion04/generation-record.json`
- `Project_TianNanGe/Assets/TianNanGe/Tripo/LeftPavilion04/LeftPavilion_H31_8K_ImportReport.json`
