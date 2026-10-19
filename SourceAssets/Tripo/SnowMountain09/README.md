# SnowMountain_P20_8K

Single snow mountain generated from the original scene reference.

- Model: `P2-20260801`, PBR, `texture_quality=extreme`, one candidate.
- Actual geometry: 25,380 triangles; requested target: 25,000 triangles.
- User-accepted limit: 26,000 triangles (4% tolerance); native mesh retained without decimation.
- Above the requested target: 380 triangles (1.52%).
- Actual Tripo credits: 135.
- FBX conversion is local; source UVs and texture sizes are preserved.
- HDRP MaskMap: R = metallic, G/B = 1, A = 1 - roughness.

Native texture dimensions:

- Material 0 base_color: 8192 x 8192.
- Material 0 normal: 4096 x 4096.
- Material 0 metallic_roughness: 4096 x 4096.

The extreme setting requests 8K color; each PBR map retains its native resolution.
