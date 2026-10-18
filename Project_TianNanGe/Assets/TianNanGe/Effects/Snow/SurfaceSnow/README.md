# Surface Snow

Migrated from Snow（小雪） `LitBasicGraph_Snow.ShaderGraph` and upgraded by Unity to the target HDRP version.

- `SurfaceSnow.mat` is a separate material. It does not replace the existing shared `Assets/TianNanGe/Materials/Snow.mat`.
- `Snow_Distortion` controls the vertex displacement / apparent snow thickness. Start around `0.08`; larger values make the layer thicker (source road materials use about `0.54–0.8`, roof materials up to about `1.44`).
- `Snow_Color` controls the snow tint.
- `Snow_Albedo`, `Snow_Normal`, and `Snow_Mask` are pre-bound to the migrated Snow_1 textures.
- `SurfaceSnowMask.prefab` supplies the global HeightField mask required by this shader. Place one instance in a scene before using `SurfaceSnow.mat`; move it or change `coverageRadius` to limit the covered area.

The shader's HeightField mask is global and uses the migrated Dynamic Radial Masks subgraph and cginc files. The controller is intentionally optional and separate from the falling-particle prefab.
