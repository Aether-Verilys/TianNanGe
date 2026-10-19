#!/usr/bin/env python3
"""Generate one Tripo P2 mountain asset, one stage at a time.

The reference stage is deliberately separate from the paid model stage:

  python GenerateTripoMountain.py --stage reference
  # inspect SourceAssets/Tripo/SnowMountain09/snow-mountain-reference.png
  python GenerateTripoMountain.py --stage model --reference-reviewed
  python GenerateTripoMountain.py --stage record

The helper accepts no batch IDs and persists every submission before waiting.
An existing successful task is reused, while an ambiguous submission stops to
avoid a duplicate charge. Paths, URLs, and credentials are scrubbed from the
portable records and logs.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys

from GenerateTripoBuilding import (
    ROOT, SOURCE, generation_lock, portable, read_json, relative,
    result_artifact, run_cli, safe_text, save_json, task_summary,
)

DIRECTORY = SOURCE / "SnowMountain09"
ORIGINAL = ROOT / "4TianNan1_4k_fb78c.jpg"
SLUG = "snow-mountain"
NAME = "SnowMountain_P20_8K"
P2_MODEL = "P2-20260801"
REFERENCE_MODEL = "seedream_v5"
# This asset keeps its native mesh under the user's accepted 4% tolerance.
# The paid generation request remains a 25,000-triangle target.
REQUESTED_TRIANGLES = 25000
ACCEPTED_TRIANGLE_LIMIT = 26000
TRIANGLE_TOLERANCE_PERCENT = 4
REFERENCE_PROMPT = (
    "Create exactly one standalone snow-covered blue-grey Chinese mountain rock formation "
    "for a distant fantasy palace scene, based on the mountain mass behind the architecture "
    "in the source image. It is a broad, stable high stone terrace with a relatively flat top "
    "that can support a large Chinese hall, with a total height about 1.5 times its width. "
    "Use layered blue-grey rock faces, sparse natural snow caps and ledges, believable erosion, "
    "and a coherent complete silhouette. Show the whole formation in a near-front three-quarter "
    "view with generous padding, neutral diffuse studio light and a plain light grey background. "
    "No buildings, roofs, trees, people, dragon, bridge, ropes, water, neighboring assets, "
    "floating pieces, text, watermark or collage. One isolated mountain reference image only."
)


def mountain_installation(path: Path, imported_triangles: int) -> dict:
    """Unwrap the complete mountain report from Unity CLI's JSON/string wrappers."""
    reports = []

    def find(value):
        if isinstance(value, str):
            try:
                find(json.loads(value))
            except (ValueError, TypeError):
                return
        elif isinstance(value, dict):
            if value.get("status") == "installed" and "sourceQuad" in value and "instances" in value:
                reports.append(value)
                return
            for item in value.values():
                find(item)
        elif isinstance(value, list):
            for item in value:
                find(item)

    find(read_json(path))
    if not reports:
        raise ValueError("The Unity installation result has no mountain installation report")
    installed = reports[0]
    if any(report != installed for report in reports[1:]):
        raise ValueError("The Unity installation result contains conflicting mountain reports")
    if installed.get("status") != "installed" or installed.get("sceneSaved") is not True:
        raise ValueError("Install the mountain and save its Unity scene before recording")
    if installed.get("sourceQuad") is not False:
        raise ValueError("Mountain installation must confirm sourceQuad=false")
    if type(installed.get("sourceFaceLimit")) is not int or installed["sourceFaceLimit"] != REQUESTED_TRIANGLES:
        raise ValueError("Mountain installation must confirm the requested sourceFaceLimit=25000")
    if type(installed.get("maxTriangles")) is not int or installed["maxTriangles"] != ACCEPTED_TRIANGLE_LIMIT:
        raise ValueError("Mountain installation must confirm the accepted maxTriangles=26000 (4% tolerance)")
    required_tolerance_fields = {
        "requestedTriangleLimit": REQUESTED_TRIANGLES,
        "triangleTolerance": ACCEPTED_TRIANGLE_LIMIT - REQUESTED_TRIANGLES,
    }
    for field, expected in required_tolerance_fields.items():
        if type(installed.get(field)) is not int or installed[field] != expected:
            raise ValueError(f"Mountain installation must confirm {field}={expected}")
    tolerance_fields = {
        "triangleTolerancePercent": TRIANGLE_TOLERANCE_PERCENT,
        "triangleToleranceAbsolute": ACCEPTED_TRIANGLE_LIMIT - REQUESTED_TRIANGLES,
        "userAcceptedTolerance": True,
    }
    for field, expected in tolerance_fields.items():
        if field in installed and (type(installed[field]) is not type(expected) or installed[field] != expected):
            raise ValueError(f"Mountain installation has inconsistent {field}")
    for field in ("actualTriangles", "importReportTriangles"):
        triangles = installed.get(field)
        if type(triangles) is not int or not 0 < triangles <= ACCEPTED_TRIANGLE_LIMIT or triangles != imported_triangles:
            raise ValueError(f"Mountain {field} must match the import report and remain within the accepted 1–26000 range")
    instances = installed.get("instances")
    if not isinstance(instances, list) or not instances:
        raise ValueError("Mountain installation must contain at least one scene instance")
    for index, instance in enumerate(instances):
        triangles = instance.get("triangles") if isinstance(instance, dict) else None
        if type(triangles) is not int or not 0 < triangles <= ACCEPTED_TRIANGLE_LIMIT or triangles != imported_triangles:
            raise ValueError(f"Mountain instance {index} must match the source mesh within the accepted 1–26000 range")
        if "maxTriangles" in instance and (type(instance["maxTriangles"]) is not int or instance["maxTriangles"] != ACCEPTED_TRIANGLE_LIMIT):
            raise ValueError(f"Mountain instance {index} has an inconsistent accepted triangle limit")
    # Preserve every report/instance field; only the shared credential/URL scrubber applies.
    return portable(installed)


def ensure_prompt() -> None:
    DIRECTORY.mkdir(parents=True, exist_ok=True)
    prompt_file = DIRECTORY / "reference-prompt.txt"
    if prompt_file.exists() and prompt_file.read_text(encoding="utf-8").strip() != REFERENCE_PROMPT:
        raise ValueError("Saved reference prompt differs from this helper; review it before generating")
    if not prompt_file.exists():
        prompt_file.write_text(REFERENCE_PROMPT + "\n", encoding="utf-8")


def run_stage(stage: str, reviewed: bool) -> dict:
    if stage == "reference":
        result_path = DIRECTORY / "reference-result.json"
        result = read_json(result_path) if result_path.is_file() else {}
        if result.get("status") == "success":
            print(f"Reusing successful reference task {result.get('task_id')}; no generation submitted", file=sys.stderr)
        else:
            if result.get("status") in {"failed", "cancelled", "banned", "expired"}:
                raise RuntimeError(f"Existing reference task ended as {result['status']}; inspect it before retrying")
            if result and not result.get("task_id"):
                raise RuntimeError("An earlier reference submission has uncertain state; recover its task ID before retrying")
            if not ORIGINAL.is_file():
                raise ValueError(f"Missing original reference: {relative(ORIGINAL)}")
            ensure_prompt()
            log = DIRECTORY / "reference-progress.log"
            if not result:
                arguments = ["generate", "image-to-image", relative(ORIGINAL), "--model", REFERENCE_MODEL,
                             "--prompt", REFERENCE_PROMPT, "--name", "tiannange-snow-mountain-reference"]
                save_json(result_path, {"status": "submitting", "stage": "reference", "asset": "SnowMountain09"})
                result = task_summary(run_cli([*arguments, "--no-wait"], log))
                result.setdefault("status", "queued")
                save_json(result_path, result)
            artifacts = DIRECTORY / "artifacts" / "reference"
            result = task_summary(run_cli(["task", "watch", result["task_id"], "--download", "--out",
                                           relative(artifacts), "--timeout", "1800"], log))
            save_json(result_path, result)
            for path in artifacts.rglob("task.json"):
                save_json(path, read_json(path))
            if result.get("status") != "success":
                raise RuntimeError(f"Reference task did not succeed: {result.get('status')}")
        image = result_artifact(result, "generated_image.png")
        target = DIRECTORY / f"{SLUG}-reference.png"
        if not target.exists():
            shutil.copy2(image, target)
        elif hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(image.read_bytes()).digest():
            raise RuntimeError("Existing canonical reference differs from successful task output; left unchanged")
        return {"status": "reference_ready", "reference": relative(target),
                "next": "Inspect the single-mountain image before model --reference-reviewed", "task": task_summary(result)}

    if stage == "model":
        if not reviewed:
            raise ValueError("Inspect the single-mountain reference image, then pass --reference-reviewed")
        reference_result = read_json(DIRECTORY / "reference-result.json")
        reference = DIRECTORY / f"{SLUG}-reference.png"
        if reference_result.get("status") != "success" or not reference.is_file():
            raise ValueError("Run and inspect the reference stage first")
        result_path = DIRECTORY / "model-result.json"
        result = read_json(result_path) if result_path.is_file() else {}
        if result.get("status") == "success":
            print(f"Reusing successful model task {result.get('task_id')}; no generation submitted", file=sys.stderr)
        else:
            if result.get("status") in {"failed", "cancelled", "banned", "expired"}:
                raise RuntimeError(f"Existing model task ended as {result['status']}; inspect it before retrying")
            if result and not result.get("task_id"):
                raise RuntimeError("An earlier model submission has uncertain state; recover its task ID before retrying")
            log = DIRECTORY / "model-progress.log"
            arguments = ["make", relative(reference), "--model", "tripo-p2", "--candidates", "1",
                         "-p", "face_limit=25000", "-p", "quad=false", "-p", "texture=true",
                         "-p", "pbr=true", "-p", "texture_quality=extreme",
                         "-p", "texture_alignment=original_image", "-p", "orientation=align_image",
                         "-p", "export_uv=true", "--name", "tiannange-snow-mountain-p2-8k"]
            plan = run_cli([*arguments, "--dry-run"], log)
            save_json(DIRECTORY / "model-plan.json", plan)
            if plan.get("valid") is not True or plan.get("model") != P2_MODEL or plan.get("candidates") != 1 or len(plan.get("steps", [])) != 1:
                raise RuntimeError("P2 model dry-run must be valid with one candidate and one generation step")
            if not result:
                save_json(result_path, {"status": "submitting", "stage": "model", "asset": "SnowMountain09"})
                result = task_summary(run_cli([*arguments, "--no-wait"], log))
                result.setdefault("status", "queued")
                save_json(result_path, result)
            artifacts = DIRECTORY / "artifacts" / "model"
            result = task_summary(run_cli(["task", "watch", result["task_id"], "--download", "--out",
                                           relative(artifacts), "--timeout", "1800"], log))
            save_json(result_path, result)
            for path in artifacts.rglob("task.json"):
                save_json(path, read_json(path))
            if result.get("status") != "success":
                raise RuntimeError(f"Model task did not succeed: {result.get('status')}")
        model = result_artifact(result, "model.glb")
        return {"status": "model_ready", "local_glb": relative(model), "task": task_summary(result)}

    reference = read_json(DIRECTORY / "reference-result.json")
    model = read_json(DIRECTORY / "model-result.json")
    if reference.get("status") != "success" or model.get("status") != "success":
        raise ValueError("Both reference and model must have successful results before recording")
    unity = ROOT / "Project_TianNanGe/Assets/TianNanGe/Tripo/SnowMountain09"
    imported = read_json(unity / f"{NAME}_ImportReport.json")
    triangles = imported.get("geometry", {}).get("triangles")
    if type(triangles) is not int or not 0 < triangles <= ACCEPTED_TRIANGLE_LIMIT:
        raise ValueError("Import report must verify 1–26000 triangles under the accepted 4% tolerance")
    installed = mountain_installation(DIRECTORY / "install-result.json", triangles)
    costs = []
    for label, result in (("reference", reference), ("model", model)):
        value = result.get("credits_consumed")
        if not isinstance(value, (int, float)) or isinstance(value, bool) or value < 0:
            raise ValueError(f"Missing actual {label} credits_consumed")
        costs.append({"stage": label, "task_id": result["task_id"], "credits_consumed": value})
    record = {
        "asset_id": "SnowMountain09", "name": NAME, "reference_source": relative(ORIGINAL),
        "reference_prompt": REFERENCE_PROMPT, "reference_image": relative(DIRECTORY / f"{SLUG}-reference.png"),
        "reference_generation": task_summary(reference),
        "model_generation": {"model": P2_MODEL, "face_limit": REQUESTED_TRIANGLES, "quad": False, "texture": True,
                             "pbr": True, "texture_quality": "extreme", "candidates": 1, **task_summary(model)},
        "local_glb": relative(result_artifact(model, "model.glb")),
        "geometry_constraint": "25,000-triangle request; user accepted native geometry up to 26,000 triangles (4% tolerance)",
        "actual_triangles": triangles, "requested_triangles": REQUESTED_TRIANGLES,
        "accepted_triangle_limit": ACCEPTED_TRIANGLE_LIMIT,
        "triangle_tolerance_percent": TRIANGLE_TOLERANCE_PERCENT,
        "triangle_tolerance_absolute": ACCEPTED_TRIANGLE_LIMIT - REQUESTED_TRIANGLES,
        "actual_over_requested_triangles": max(0, triangles - REQUESTED_TRIANGLES),
        "actual_over_requested_percent": round(max(0, triangles - REQUESTED_TRIANGLES) / REQUESTED_TRIANGLES * 100, 4),
        "within_requested_target": triangles <= REQUESTED_TRIANGLES,
        "within_accepted_tolerance": triangles <= ACCEPTED_TRIANGLE_LIMIT,
        "user_accepted_tolerance": True,
        "import": imported, "installation": installed, "costs": costs,
        "total_credits": sum(cost["credits_consumed"] for cost in costs),
    }
    save_json(DIRECTORY / "generation-record.json", record)
    lines = [f"# {NAME}", "", "Single snow mountain generated from the original scene reference.", "",
             f"- Model: `{P2_MODEL}`, PBR, `texture_quality=extreme`, one candidate.",
             f"- Actual geometry: {triangles:,} triangles; requested target: {REQUESTED_TRIANGLES:,} triangles.",
             f"- User-accepted limit: {ACCEPTED_TRIANGLE_LIMIT:,} triangles (4% tolerance); native mesh retained without decimation.",
             f"- Above the requested target: {record['actual_over_requested_triangles']:,} triangles ({record['actual_over_requested_percent']:g}%).",
             f"- Actual Tripo credits: {record['total_credits']}.",
             "- FBX conversion is local; source UVs and texture sizes are preserved.",
             "- HDRP MaskMap: R = metallic, G/B = 1, A = 1 - roughness.", "", "Native texture dimensions:", ""]
    for material in imported.get("materials", []):
        for channel, size in material.get("source_dimensions", {}).items():
            if size:
                lines.append(f"- Material {material['index']} {channel}: {size['width']} x {size['height']}.")
    lines += ["", "The extreme setting requests 8K color; each PBR map retains its native resolution.", ""]
    for path in (DIRECTORY / "README.md", unity / "README.md"):
        path.write_text("\n".join(lines), encoding="utf-8")
    return {"status": "recorded", "record": relative(DIRECTORY / "generation-record.json")}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--stage", required=True, choices=("reference", "model", "record"))
    parser.add_argument("--reference-reviewed", action="store_true")
    args = parser.parse_args()
    with generation_lock():
        result = run_stage(args.stage, args.reference_reviewed)
    print(json.dumps(portable(result), indent=2, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, KeyError) as error:
        print(f"ERROR: {safe_text(str(error))}", file=sys.stderr)
        raise SystemExit(1)
