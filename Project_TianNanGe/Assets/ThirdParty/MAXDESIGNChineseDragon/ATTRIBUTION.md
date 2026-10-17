# Chinese dragon — MAXDESIGN-3D

**Chinese dragon** by [MAXDESIGN-3D](https://sketchfab.com/MAXDESIGN), downloaded from the author's [Sketchfab model page](https://sketchfab.com/3d-models/chinese-dragon-a5ad5ec06f43461f95bb93e95fe7553b) on 2026-10-18.

Licensed under [Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/). [Full license terms](https://creativecommons.org/licenses/by/4.0/legalcode). The source page identifies the model's license as CC Attribution. Retain the creator, model/source link, license link, and modification notice when sharing this asset or its adaptations. No endorsement by the creator is implied.

## Changes made for TianNanGe

- Copied the original `source/Chinese dragon.fbx` to `ChineseDragon.fbx`; only the filename changed. The FBX and all three PNG textures are byte-for-byte identical to the downloaded source files.
- Rebuilt the material setup for Unity HDRP using the supplied textures, and configured the FBX importer with `globalScale = 0.0025`. These are Unity project/import settings; the source FBX was not edited.
- Retained the complete `Take 001` animation as the locally named `SourceMotion`: frames 0–1725 at 30 fps, spanning 57.5 seconds.
- Created two local preview clips through Unity importer settings: `IdlePreview`, frames 1604–1724, and `SwayPreview`, frames 1422–1542, both at 30 fps with looping/blending enabled. These names and frame selections were chosen for this project; they are not standard action names supplied by the author, and are not evidence of separately authored idle/sway clips.

Original files are preserved under `SourceAssets/MAXDESIGNChineseDragon/` outside Unity's Assets folder. Its README records file hashes and links back to this attribution. This notice applies to this third-party model, textures, and their adaptations, not unrelated project content or Unity software.
