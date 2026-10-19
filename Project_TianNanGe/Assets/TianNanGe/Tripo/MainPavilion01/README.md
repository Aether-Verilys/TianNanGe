# Main Pavilion H3.1 replacement

This is the first single-building Tripo replacement for TianNanGe.

- Reference: `SourceAssets/Tripo/MainPavilion01/main-pavilion-reference.png`
- Tripo model: `tripo-v3.1` (API wire model `v3.1-20260211`, H3.1)
- Generation: single image-to-model request, PBR enabled, `texture_quality=detailed`, `texture_alignment=original_image`, `orientation=align_image`
- Conversion: FBX, `texture_size=2048`, PNG textures, center-bottom pivot
- Actual cost: 40 credits image-to-model + 10 credits conversion = 50 credits
- Verified outputs: base color, normal, metallic and roughness source maps are 2048×2048. `MainPavilion_MaskMap.png` packs metallic into R and inverted roughness into A for HDRP Mask Map.

The scene instance is `TianNanGe_Blockout/Architecture/MainPavilion/Tripo_MainPavilion_H31`. The previous proxy remains under `MainPavilion/Proxy_ReplaceWithTripo` and is inactive as a fallback. The original `MainPavilion` root transform is preserved.
