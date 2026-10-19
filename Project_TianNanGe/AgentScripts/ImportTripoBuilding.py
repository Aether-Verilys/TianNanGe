#!/usr/bin/env python3
"""Convert one Tripo GLB into Unity FBX and full-resolution HDRP textures.

Requires Python 3.10+, Pillow and Blender 4+/5+. No Tripo or Unity calls are made.
Example (use a Python environment that has Pillow installed):
  python ImportTripoBuilding.py --input building.glb \
    --output-dir Assets/TianNanGe/Tripo/Building --name Building_H31

Embedded source images keep their original pixel dimensions. Metallic/roughness
factors are baked into the extracted maps; baseColorFactor is baked in linear
space before re-encoding sRGB. The normal scale remains in the JSON report.
Multi-material meshes are split by material without repacking or flipping UVs.
The FBX contains static meshes only, with no materials or embedded images.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile


def repository_path(path: Path) -> str:
    """Keep asset reports portable and avoid recording a user's home directory."""
    repository = Path(__file__).resolve().parents[2]
    try:
        return path.resolve().relative_to(repository).as_posix()
    except ValueError:
        return path.name


def read_glb(path: Path) -> tuple[dict, bytes]:
    data = path.read_bytes()
    if len(data) < 20:
        raise ValueError("Input is too short to be a GLB")
    magic, version, length = struct.unpack_from("<4sII", data)
    if magic != b"glTF" or version != 2 or length != len(data):
        raise ValueError("Expected a complete glTF 2.0 binary (.glb)")
    document = None
    binary = b""
    offset = 12
    while offset < length:
        if offset + 8 > length:
            raise ValueError("Truncated GLB chunk header")
        size, kind = struct.unpack_from("<I4s", data, offset)
        offset += 8
        if offset + size > length:
            raise ValueError("Truncated GLB chunk")
        chunk = data[offset:offset + size]
        offset += size
        if kind == b"JSON":
            if document is not None:
                raise ValueError("Multiple JSON chunks in GLB")
            document = json.loads(chunk.rstrip(b" \t\r\n\x00"))
        elif kind == b"BIN\x00":
            if binary:
                raise ValueError("Multiple BIN chunks in GLB")
            binary = chunk
    if document is None:
        raise ValueError("GLB has no JSON chunk")
    if document.get("asset", {}).get("version") != "2.0":
        raise ValueError("Only glTF 2.0 is supported")
    return document, binary


def write_glb(path: Path, document: dict, binary: bytes) -> None:
    encoded = json.dumps(document, separators=(",", ":")).encode("utf-8")
    encoded += b" " * (-len(encoded) % 4)
    binary += b"\x00" * (-len(binary) % 4)
    size = 12 + 8 + len(encoded) + (8 + len(binary) if binary else 0)
    with path.open("wb") as output:
        output.write(struct.pack("<4sII", b"glTF", 2, size))
        output.write(struct.pack("<I4s", len(encoded), b"JSON"))
        output.write(encoded)
        if binary:
            output.write(struct.pack("<I4s", len(binary), b"BIN\x00"))
            output.write(binary)


def embedded_image(document: dict, binary: bytes, index: int) -> bytes:
    image = document["images"][index]
    if "bufferView" in image:
        view = document["bufferViews"][image["bufferView"]]
        if view.get("buffer", 0) != 0 or document["buffers"][0].get("uri"):
            raise ValueError("Images must use the embedded GLB binary buffer")
        start, size = view.get("byteOffset", 0), view["byteLength"]
        if start < 0 or size < 0 or start + size > len(binary):
            raise ValueError(f"Image {index} has an invalid buffer view")
        return binary[start:start + size]
    uri = image.get("uri", "")
    if uri.startswith("data:") and ";base64," in uri:
        return base64.b64decode(uri.split(",", 1)[1], validate=True)
    raise ValueError(f"Image {index} is external; this importer expects embedded PNG/JPEG")


def texture_image_index(document: dict, info: dict | None) -> int | None:
    if not info:
        return None
    texture = document["textures"][info["index"]]
    if "source" not in texture:
        raise ValueError("Compressed texture extensions are not supported; use PNG/JPEG GLB")
    return texture["source"]


def extract_textures(document: dict, binary: bytes, directory: Path, name: str) -> list[dict]:
    from PIL import Image

    def load(index: int):
        with Image.open(io.BytesIO(embedded_image(document, binary, index))) as source:
            if source.format not in {"PNG", "JPEG"}:
                raise ValueError(f"Image {index} format {source.format} is not PNG/JPEG")
            source.load()
            return source.copy()

    def dimensions(index: int | None):
        if index is None:
            return None
        with Image.open(io.BytesIO(embedded_image(document, binary, index))) as image:
            return image.size

    def save(image, filename: str) -> dict:
        image.save(directory / filename, format="PNG", compress_level=6)
        return {"file": filename, "width": image.width, "height": image.height}

    def scaled(channel, factor: float):
        if not math.isfinite(factor) or factor < 0 or factor > 1:
            raise ValueError(f"PBR factor outside glTF range [0,1]: {factor}")
        return channel.point([round(i * factor) for i in range(256)])

    def basecolor_lut(factor: float, alpha: bool = False):
        if not math.isfinite(factor) or factor < 0 or factor > 1:
            raise ValueError(f"Base color factor outside glTF range [0,1]: {factor}")
        if alpha:
            return [round(i * factor) for i in range(256)]
        values = []
        for i in range(256):
            srgb = i / 255.0
            linear = srgb / 12.92 if srgb <= 0.04045 else ((srgb + 0.055) / 1.055) ** 2.4
            linear *= factor
            srgb = linear * 12.92 if linear <= 0.0031308 else 1.055 * linear ** (1 / 2.4) - 0.055
            values.append(round(max(0, min(1, srgb)) * 255))
        return values

    materials = document.get("materials", [])
    records = []
    for index, material in enumerate(materials):
        prefix = name if len(materials) == 1 else f"{name}_M{index:02d}"
        pbr = material.get("pbrMetallicRoughness", {})
        infos = {
            "base_color": pbr.get("baseColorTexture"),
            "normal": material.get("normalTexture"),
            "metallic_roughness": pbr.get("metallicRoughnessTexture"),
        }
        indices = {key: texture_image_index(document, value) for key, value in infos.items()}
        sizes = [dimensions(value) for value in indices.values() if value is not None]
        fallback_size = max(sizes, key=lambda size: size[0] * size[1]) if sizes else (1, 1)
        record = {
            "index": index, "source_name": material.get("name", f"Material_{index}"),
            "fbx_mesh_prefix": f"{name}_M{index:02d}", "maps": {},
            "source_image_indices": indices, "normal_scale": material.get("normalTexture", {}).get("scale", 1.0),
            "source_dimensions": {
                key: {"width": size[0], "height": size[1]} if size else None
                for key, image_index in indices.items() for size in [dimensions(image_index)]
            },
            "alpha_mode": material.get("alphaMode", "OPAQUE"),
            "alpha_cutoff": material.get("alphaCutoff", 0.5),
            "double_sided": material.get("doubleSided", False),
            "texture_coordinates": {
                key: {"texcoord": info.get("texCoord", 0),
                      "transform": info.get("extensions", {}).get("KHR_texture_transform")}
                for key, info in infos.items() if info
            },
        }
        base_factor = pbr.get("baseColorFactor", [1.0, 1.0, 1.0, 1.0])
        if len(base_factor) != 4:
            raise ValueError("baseColorFactor must contain four values")
        source = load(indices["base_color"]) if indices["base_color"] is not None else Image.new("RGBA", fallback_size, (255, 255, 255, 255))
        rgba = source.convert("RGBA")
        source.close()
        if base_factor != [1, 1, 1, 1]:
            channels = [channel.point(basecolor_lut(float(base_factor[i]), i == 3)) for i, channel in enumerate(rgba.split())]
            rgba.close()
            rgba = Image.merge("RGBA", channels)
            for channel in channels:
                channel.close()
        record["maps"]["base_color"] = save(rgba, f"{prefix}_BaseColor.png")
        rgba.close()
        source = load(indices["normal"]) if indices["normal"] is not None else Image.new("RGB", fallback_size, (128, 128, 255))
        normal = source.convert("RGB")
        source.close()
        record["maps"]["normal"] = save(normal, f"{prefix}_Normal.png")
        normal.close()
        # glTF metallicRoughness packs roughness in G and metallic in B.
        # This also handles ORM: the occlusion R channel is intentionally ignored.
        if indices["metallic_roughness"] is not None:
            source = load(indices["metallic_roughness"])
            rgb = source.convert("RGB")
            source.close()
            roughness_source = rgb.getchannel("G")
            metallic_source = rgb.getchannel("B")
            rgb.close()
        else:
            roughness_source = Image.new("L", fallback_size, 255)
            metallic_source = Image.new("L", fallback_size, 255)
        roughness = scaled(roughness_source, float(pbr.get("roughnessFactor", 1)))
        metallic = scaled(metallic_source, float(pbr.get("metallicFactor", 1)))
        roughness_source.close()
        metallic_source.close()
        record["maps"]["roughness"] = save(roughness, f"{prefix}_Roughness.png")
        record["maps"]["metallic"] = save(metallic, f"{prefix}_Metallic.png")
        ones = Image.new("L", metallic.size, 255)
        smoothness = roughness.point([255 - i for i in range(256)])
        mask = Image.merge("RGBA", (metallic, ones, ones, smoothness))
        record["maps"]["mask"] = save(mask, f"{prefix}_MaskMap.png")
        for image in (roughness, metallic, ones, smoothness, mask):
            image.close()
        record["baked_factors"] = {
            "base_color": base_factor, "metallic": pbr.get("metallicFactor", 1),
            "roughness": pbr.get("roughnessFactor", 1),
        }
        records.append(record)
    return records


def blender_worker(argv: list[str]) -> None:
    import bpy
    from mathutils import Vector

    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--fbx", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--triangle-limit", type=int)
    args = parser.parse_args(argv)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.gltf(filepath=args.input, import_pack_images=False, merge_vertices=False)
    source_meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if not source_meshes:
        raise ValueError("GLB contains no mesh objects")
    # Separate material islands, preserving the UV loops and all world transforms.
    for obj in source_meshes:
        if len({polygon.material_index for polygon in obj.data.polygons}) > 1:
            bpy.ops.object.select_all(action="DESELECT")
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
            bpy.ops.object.mode_set(mode="EDIT")
            bpy.ops.mesh.select_all(action="SELECT")
            bpy.ops.mesh.separate(type="MATERIAL")
            bpy.ops.object.mode_set(mode="OBJECT")
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH" and obj.data.polygons]
    source_triangle_counts = []
    for obj in meshes:
        obj.data.calc_loop_triangles()
        source_triangle_counts.append(len(obj.data.loop_triangles))
    source_triangles = sum(source_triangle_counts)
    simplification = None
    if args.triangle_limit is not None and source_triangles > args.triangle_limit:
        ratio = args.triangle_limit / source_triangles
        for obj in meshes:
            bpy.ops.object.select_all(action="DESELECT")
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
            modifier = obj.modifiers.new("LocalTriangleBudget", "DECIMATE")
            modifier.decimate_type = "COLLAPSE"
            modifier.ratio = ratio
            modifier.use_collapse_triangulate = True
            # No merge-by-distance or UV repacking: disconnected UV boundaries
            # remain separate and Blender interpolates surviving loop UVs.
            bpy.ops.object.modifier_apply(modifier=modifier.name)
        actual_triangles = 0
        for obj in meshes:
            obj.data.calc_loop_triangles()
            actual_triangles += len(obj.data.loop_triangles)
        if actual_triangles > args.triangle_limit:
            raise ValueError(f"Decimation produced {actual_triangles} triangles; limit is {args.triangle_limit}")
        simplification = {
            "method": "Blender Decimate / COLLAPSE", "source_triangles": source_triangles,
            "target_triangles": args.triangle_limit, "actual_triangles": actual_triangles,
            "ratio": ratio, "vertices_merged_by_distance": False, "uvs_repacked": False,
            "uv_handling": "Retain disconnected UV boundaries; interpolate surviving loop UVs",
            "textures_modified": False,
        }
    records, all_points = [], []
    for index, obj in enumerate(meshes):
        slot = obj.data.polygons[0].material_index
        material_name = obj.data.materials[slot].name if slot < len(obj.data.materials) and obj.data.materials[slot] else None
        match = re.match(r"TRIPO_M(\d+)$", material_name or "")
        if not match:
            raise ValueError(f"Cannot map material for mesh {obj.name!r}: {material_name!r}")
        material_index = int(match.group(1))
        original_name = obj.name
        obj.name = f"{args.name}_M{material_index:02d}_{index:03d}"
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world
        obj.data.materials.clear()
        for polygon in obj.data.polygons:
            polygon.material_index = 0
        obj.data.calc_loop_triangles()
        points = [obj.matrix_world @ Vector(point) for point in obj.bound_box]
        all_points.extend(points)
        uv_hash = hashlib.sha256()
        for layer in obj.data.uv_layers:
            for item in layer.data:
                uv_hash.update(struct.pack("<ff", item.uv.x, item.uv.y))
        records.append({
            "name": obj.name, "source_name": original_name, "material_index": material_index,
            "vertices": len(obj.data.vertices), "triangles": len(obj.data.loop_triangles),
            "uv_layers": [layer.name for layer in obj.data.uv_layers], "uv_sha256": uv_hash.hexdigest(),
        })
    if not meshes:
        raise ValueError("GLB contains no polygon geometry")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.export_scene.fbx(
        filepath=args.fbx, use_selection=True, object_types={"MESH"},
        axis_forward="-Z", axis_up="Y", global_scale=1.0,
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE", use_space_transform=True,
        bake_space_transform=False, use_mesh_modifiers=False, mesh_smooth_type="FACE",
        use_triangles=True, use_tspace=True, add_leaf_bones=False, bake_anim=False,
        path_mode="STRIP", embed_textures=False, use_custom_props=False,
    )

    def bounds(points):
        low = [min(point[i] for point in points) for i in range(3)]
        high = [max(point[i] for point in points) for i in range(3)]
        return {"min": low, "max": high, "size": [high[i] - low[i] for i in range(3)],
                "center": [(high[i] + low[i]) / 2 for i in range(3)]}

    report = {
        "blender_version": bpy.app.version_string, "mesh_count": len(meshes),
        "triangles": sum(item["triangles"] for item in records),
        "vertices": sum(item["vertices"] for item in records), "meshes": records,
        "bounds_blender_z_up": bounds(all_points),
        "bounds_fbx_y_up": bounds([Vector((p.x, p.z, -p.y)) for p in all_points]),
        "fbx_axes": {"forward": "-Z", "up": "Y"},
        "fbx_materials": 0, "fbx_embedded_textures": 0,
    }
    if simplification is not None:
        report["simplification"] = simplification
    Path(args.report).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


def main(argv: list[str]) -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", type=Path, required=True, help="One downloaded Tripo .glb")
    parser.add_argument("--output-dir", type=Path, required=True, help="Unity asset directory")
    parser.add_argument("--name", required=True, help="Output basename (letters, digits, underscores, hyphens)")
    parser.add_argument("--blender", help="Blender executable (or BLENDER_PATH environment variable)")
    parser.add_argument("--triangle-limit", type=int, help="Optional local Blender collapse limit; source textures and UV islands remain in place")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", args.name):
        parser.error("--name must use only letters, digits, underscores and hyphens")
    if args.triangle_limit is not None and args.triangle_limit < 100:
        parser.error("--triangle-limit must be at least 100")
    source = args.input.expanduser().resolve(strict=True)
    destination = args.output_dir.expanduser().resolve()
    if source.suffix.lower() != ".glb":
        parser.error("--input must be a .glb file")
    executable = args.blender or os.environ.get("BLENDER_PATH") or shutil.which("blender")
    if not executable and Path("/Applications/Blender.app/Contents/MacOS/Blender").is_file():
        executable = "/Applications/Blender.app/Contents/MacOS/Blender"
    if not executable:
        parser.error("Blender not found; set --blender or BLENDER_PATH")
    try:
        import PIL  # noqa: F401
    except ImportError:
        parser.error("Pillow is required; run this script with a Python environment containing Pillow")
    document, binary = read_glb(source)
    # Give every primitive a material, including glTF's implicit default material.
    materials = document.setdefault("materials", [])
    default_index = None
    for mesh in document.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            if primitive.get("mode", 4) != 4:
                raise ValueError("Only triangle-list glTF primitives are supported")
            if "material" not in primitive:
                if default_index is None:
                    default_index = len(materials)
                    materials.append({"name": "DefaultMaterial"})
                primitive["material"] = default_index
    with tempfile.TemporaryDirectory(prefix="tripo-building-") as scratch:
        staging = Path(scratch)
        textures = extract_textures(document, binary, staging, args.name)
        for index, material in enumerate(materials):
            material["name"] = f"TRIPO_M{index}"
        prepared = staging / "input.glb"
        write_glb(prepared, document, binary)
        fbx = staging / f"{args.name}.fbx"
        geometry_report = staging / "geometry.json"
        command = [str(executable), "--background", "--factory-startup", "--disable-autoexec",
                   "--python-exit-code", "1", "--python", str(Path(__file__).resolve()), "--",
                   "--_blender-worker", "--input", str(prepared), "--fbx", str(fbx),
                   "--report", str(geometry_report), "--name", args.name]
        if args.triangle_limit is not None:
            command.extend(["--triangle-limit", str(args.triangle_limit)])
        completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        if completed.returncode != 0 or not fbx.is_file() or not geometry_report.is_file():
            raise RuntimeError(f"Blender import/export failed ({completed.returncode}):\n{completed.stdout[-12000:]}")
        report = {
            "source": repository_path(source), "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "fbx": fbx.name, "materials": textures,
            "hdrp_mask_channels": {"R": "metallic", "G": "1 (unoccluded)", "B": "1", "A": "1 - roughness"},
            "normal_convention": "glTF tangent space / OpenGL (+Y); source channels preserved",
            "images_resized": False, "uvs_repacked": False,
            "geometry": json.loads(geometry_report.read_text(encoding="utf-8")),
        }
        filename = f"{args.name}_ImportReport.json"
        (staging / filename).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        deliverables = [fbx, staging / filename]
        for material in textures:
            deliverables.extend(staging / value["file"] for value in material["maps"].values())
        destination.mkdir(parents=True, exist_ok=True)
        for file in deliverables:
            shutil.copy2(file, destination / file.name)
        print(json.dumps({"output_dir": str(destination), "report": str(destination / filename), **report}, indent=2))


if __name__ == "__main__":
    try:
        arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
        if arguments and arguments[0] == "--_blender-worker":
            blender_worker(arguments[1:])
        else:
            main(arguments)
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
