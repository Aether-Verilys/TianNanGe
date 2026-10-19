# Reference composition and Tripo mountain placement

The camera remains FristCamera; building roots follow reference-layout.json. MainPavilion is twice its previous root scale. before-reference-layout.json records the original placement.

SnowMountain09 uses the original Tripo P2-20260801 geometry: 25,380 triangles. The generation requested 25,000; the user accepted the 380-triangle overrun and explicitly requested no simplification. Native textures are 8K BaseColor and 4K PBR channels.

Eight scene mountain instances share one independent asset. mountain-layout.json contains world geometry dimensions, positions, yaw and the explicit tolerance. Fitting uses transformed mesh vertices, not rotated renderer bounding boxes. Existing procedural building mountains are disabled in the scene; the original prefab is preserved. The two bridge landing supports remain in place.

Reapply through the running Unity Editor, in this order:

1. Run AgentScripts/AlignReferenceComposition.cs entry AlignReferenceComposition.Apply.
2. Copy mountain-layout.json to Project_TianNanGe/Temp/tripo-mountain-install.json, then run AgentScripts/InstallTripoMountain.cs entry InstallTripoMountain.Install.
3. Run AgentScripts/FitTripoMountainFoundations.cs entry FitTripoMountainFoundations.Apply to refit visible masonry and bridge stairs after placement changes.

The foundations use 81 sample locations each, with a documented 3 mm ray neighborhood. Stone and snow collision boxes correspond to visible solids. The 28 stair treads connect the existing far bridge landing to the raised distant pavilion. validation.json summarizes the final checks. Raw terrain-only verification can report gaps because visible fitted foundations provide the final connection.

All changes are local; no commit or push was performed.
