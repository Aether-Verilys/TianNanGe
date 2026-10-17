# NSSM Chinese Dragon — attribution and modifications

Chinese Dragon — NPX Team, published by NPXcoot in **Nssm-models-textures-sounds**.

- Source repository: https://github.com/NPXcoot/Nssm-models-textures-sounds
- Frozen source commit: `0664d4fe7a7ea5bf45c14fd822393215d8037a7d`.
- Original files: `past or various models/chinese_dragon.blend` and `past or various models/chinese_dragon.png`.
- License: [Creative Commons Attribution-ShareAlike 4.0 International](https://creativecommons.org/licenses/by-sa/4.0/).
- Full license terms: https://creativecommons.org/licenses/by-sa/4.0/legalcode
- [Upstream license at the frozen commit](https://github.com/NPXcoot/Nssm-models-textures-sounds/blob/0664d4fe7a7ea5bf45c14fd822393215d8037a7d/License.txt); `LICENSE.txt` in this folder is an exact copy of that notice.
- Original Blender file and texture are preserved unchanged at `SourceAssets/NSSMChineseDragon/` relative to the TianNanGe workspace root.

## Changes made for TianNanGe on 2026-10-18

- Converted the original Blender model and armature to `ChineseDragon.fbx`, exporting the selected mesh and armature, deform bones only, without extra leaf bones; applied mesh modifiers on export and baked animation without curve simplification. Export axes: forward -Z, up Y.
- Reconnected the original PNG to a Principled BSDF material for export and created `NSSM_OriginalRed.mat` for Unity HDRP using the original texture. The PNG remains byte-for-byte identical to the upstream file.
- Configured Unity Generic animation import. `SourceMotion` preserves frames 1–270 at the source rate of 24 fps. The project also defines `IdlePreview` (120–210) and `Fly` (230–270), with looping/blending enabled for those preview clips. These two names/ranges are local preview selections, not verified original animation labels.
- Retained this asset as a temporary fallback pending a later model replacement.

The upstream model/texture and adaptations of them retain CC BY-SA 4.0. When sharing those adaptations, retain attribution, the license link, and this modification notice, and apply the required share-alike terms. This notice applies to this third-party asset and does not relabel unrelated project content or Unity software. No endorsement by the original creators is implied.
