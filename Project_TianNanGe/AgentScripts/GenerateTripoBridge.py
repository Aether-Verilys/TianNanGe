#!/usr/bin/env python3
"""Generate one H3.1 winding bridge with supports, one stage at a time.

  python GenerateTripoBridge.py --stage reference
  # Inspect the complete isolated bridge image, then:
  python GenerateTripoBridge.py --stage model --reference-reviewed
  # Alternatively use a reviewed local PNG, without a Tripo reference task:
  python GenerateTripoBridge.py --stage model --reference-image bridge.png --reference-reviewed
  # Replace a reviewed reference BEFORE model planning, preserving its history:
  python GenerateTripoBridge.py --stage reference --adopt-reference bridge-v2.png --reference-reviewed
  # Adopt a separately completed Tripo reference task, including its actual cost:
  python GenerateTripoBridge.py --stage reference --adopt-reference bridge-v2.png \
      --reference-provider tripo --reference-task-result v2-result.json --reference-reviewed
  # Import locally, install in Unity and save the scene before:
  python GenerateTripoBridge.py --stage record

Successful tasks are reused. Interrupted tasks resume by ID; ambiguous paid
submissions stop. This helper never generates several assets or candidates,
remeshes, decimates, imports into Unity, or performs a remote conversion.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import sys

from GenerateTripoBuilding import (
    ROOT, SOURCE, generation_lock, portable, read_json, relative,
    result_artifact, run_cli, safe_text, save_json, task_summary,
)

ASSET_ID = "WindingBridge10"
DIRECTORY = SOURCE / ASSET_ID
ORIGINAL = ROOT / "4TianNan1_4k_fb78c.jpg"
SLUG = "winding-bridge"
NAME = "WindingBridge_H31_8K"
MODEL = "v3.1-20260211"
REFERENCE_MODEL = "seedream_v5"
REFERENCE = DIRECTORY / f"{SLUG}-reference.png"
REFERENCE_PROMPT = (
    "Extract and reconstruct ONE complete ancient Chinese mountain bridge from the source image. "
    "Long narrow walkable stone deck with a moderate shallow S bend in PLAN VIEW, "
    "two gentle changes of direction; not a straight road, not a steep humpback. "
    "Continuous dark weathered timber balustrades and stone posts on both sides, pale snow on "
    "the deck, caps and rails. Clearly show the complete load-bearing structure BELOW the deck: "
    "several tall blue-grey masonry piers connected by sturdy stone arches, with visible arch openings. "
    "Piers descend well below the deck. One coherent connected bridge, open walkable entrance and exit. "
    "Show the entire asset, both endpoints and all supports in an elevated three-quarter side view "
    "with enough top view to read the bend, generous padding, realistic PBR materials, "
    "neutral diffuse light and plain light grey background. "
    "No mountains, ground, buildings, people, dragon, trees, water, text, watermark or collage."
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def reference_info(path: Path) -> dict:
    if not path.is_file():
        raise ValueError(f"Missing reference PNG: {relative(path)}")
    with path.open("rb") as handle:
        header = handle.read(24)
    if len(header) != 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise ValueError("The reviewed reference must be a PNG image")
    width, height = struct.unpack(">II", header[16:24])
    if width <= 0 or height <= 0:
        raise ValueError("Reference PNG has invalid dimensions")
    return {"reference_image": relative(path), "sha256": sha256(path), "width": width, "height": height}


def ensure_prompt() -> None:
    if not REFERENCE_PROMPT.strip() or len(REFERENCE_PROMPT) > 1024:
        raise ValueError("reference_prompt must contain 1–1024 characters")
    DIRECTORY.mkdir(parents=True, exist_ok=True)
    prompt_file = DIRECTORY / "reference-prompt.txt"
    if prompt_file.exists() and prompt_file.read_text(encoding="utf-8").strip() != REFERENCE_PROMPT:
        raise ValueError("Saved reference prompt differs from this helper; review it before generating")
    if not prompt_file.exists():
        prompt_file.write_text(REFERENCE_PROMPT + "\n", encoding="utf-8")


def copy_reference(source: Path) -> None:
    reference_info(source)
    DIRECTORY.mkdir(parents=True, exist_ok=True)
    if REFERENCE.exists():
        if sha256(source) != sha256(REFERENCE):
            raise ValueError("Canonical reference differs from the supplied image; left unchanged")
    else:
        shutil.copy2(source, REFERENCE)


def reference_provenance() -> dict:
    info = reference_info(REFERENCE)
    source_path = DIRECTORY / "reference-source.json"
    source = read_json(source_path) if source_path.is_file() else {}
    # An explicitly adopted edit supersedes the earlier Tripo task,
    # whose original result remains on disk and in the archived provenance.
    if source.get("adopted_reference") is True:
        if source.get("sha256") != info["sha256"]:
            raise ValueError("Adopted reference changed after its provenance was recorded")
        return source
    result_path = DIRECTORY / "reference-result.json"
    if result_path.is_file():
        result = read_json(result_path)
        if result.get("status") != "success":
            raise ValueError("Resolve the existing reference task before using its canonical image")
        artifact = result_artifact(result, "generated_image.png")
        if sha256(artifact) != info["sha256"]:
            raise ValueError("Canonical reference differs from the successful reference task output")
        return {"provider": "tripo", "model": REFERENCE_MODEL, **info, **task_summary(result)}
    if source and source.get("sha256") != info["sha256"]:
        raise ValueError("Local reference changed after its provenance was recorded")
    source = {"provider": "local", "status": "provided", "reference_source": relative(ORIGINAL), **info}
    save_json(source_path, source)
    return source


def adopt_reference(source: Path, reviewed: bool, prompt_file: Path | None = None,
                    provider: str = "builtin_image_gen", task_result: Path | None = None) -> dict:
    """Adopt a completed image edit locally, without submitting a remote task."""
    if not reviewed:
        raise ValueError("Inspect the new isolated bridge image, then pass --reference-reviewed")
    if any((DIRECTORY / name).exists() for name in ("model-input.json", "model-result.json")):
        raise ValueError("A model input or result already exists; reference replacement is forbidden")
    if (DIRECTORY / "reference-adoption-pending.json").exists():
        raise ValueError("A reference adoption was interrupted; reconcile its pending provenance before adopting again")
    if provider not in {"builtin_image_gen", "tripo", "local"}:
        raise ValueError("Unsupported adopted reference provider")
    new_info = reference_info(source)
    if not REFERENCE.is_file():
        raise ValueError("Adoption requires an existing reference to preserve in history")
    old = reference_provenance()
    if old["sha256"] == new_info["sha256"]:
        if old.get("adopted_reference") is not True or old.get("provider") != provider:
            raise ValueError("The new reference is unchanged; retain its original provider")
        return {"status": "reference_ready", "reference": relative(REFERENCE),
                "reference_generation": old, "paid_submission": False, "adoption_reused": True}
    new_task = None
    if provider == "tripo":
        if task_result is None:
            raise ValueError("Adopting a Tripo reference requires --reference-task-result")
        new_task = read_json(task_result)
        credits = new_task.get("credits_consumed")
        if (new_task.get("status") != "success" or not new_task.get("task_id") or
                not isinstance(credits, (int, float)) or isinstance(credits, bool) or credits < 0):
            raise ValueError("The adopted Tripo reference result must be successful with its actual task ID and credits")
        if sha256(result_artifact(new_task, "generated_image.png")) != new_info["sha256"]:
            raise ValueError("The adopted image does not match the supplied Tripo reference task output")
    elif task_result is not None:
        raise ValueError("--reference-task-result is supported only for --reference-provider tripo")
    if prompt_file is not None and not prompt_file.is_file():
        raise ValueError("The adopted reference prompt file does not exist")
    new_prompt = prompt_file.read_text(encoding="utf-8").strip() if prompt_file else None
    archive = DIRECTORY / "reference-history" / old["sha256"]
    archive.mkdir(parents=True, exist_ok=True)

    def archive_copy(path: Path, name: str) -> Path | None:
        if not path.is_file():
            return None
        target = archive / name
        if target.exists() and sha256(target) != sha256(path):
            raise ValueError(f"Existing reference archive differs: {relative(target)}")
        if not target.exists():
            shutil.copy2(path, target)
        return target

    old_image = archive_copy(REFERENCE, "reference.png")
    old_result_path = DIRECTORY / ("reference-adopted-task-result.json" if old.get("adopted_reference") else "reference-result.json")
    old_result = archive_copy(old_result_path, "reference-result.json") if old.get("provider") == "tripo" else None
    old_prompt_path = DIRECTORY / ("reference-adopted-prompt.txt" if old.get("adopted_reference") else "reference-prompt.txt")
    old_prompt = archive_copy(old_prompt_path, "reference-prompt.txt")
    old_source = archive_copy(DIRECTORY / "reference-source.json", "reference-source.json")
    old_entry = {key: value for key, value in old.items() if key != "previous_references"}
    old_entry.update({"reference_image": relative(old_image), "superseded": True})
    if old_result:
        old_entry["archived_task_result"] = relative(old_result)
    if old_prompt:
        old_entry["reference_prompt"] = old_prompt.read_text(encoding="utf-8").strip()
        old_entry["archived_prompt"] = relative(old_prompt)
    if old_source:
        old_entry["archived_provenance"] = relative(old_source)
    save_json(archive / "provenance.json", old_entry)
    previous = [*old.get("previous_references", []), old_entry]
    adopted = {"provider": provider, "status": "adopted", "adopted_reference": True, "reference_reviewed": True,
               "reference_source": relative(ORIGINAL), **new_info, "reference_image": relative(REFERENCE),
               "previous_references": previous, "adoption_submitted_tripo_task": False}
    if new_task is not None:
        adopted.update({"model": REFERENCE_MODEL, **task_summary(new_task)})
    if new_prompt:
        adopted["reference_prompt"] = new_prompt
    # Keep both images and the new provenance before changing the canonical path.
    # A partial filesystem failure cannot cause any automatic remote generation.
    staging = DIRECTORY / "reference-adoption-staging.png"
    shutil.copy2(source, staging)
    save_json(DIRECTORY / "reference-adoption-pending.json", adopted)
    staging.replace(REFERENCE)
    save_json(DIRECTORY / "reference-source.json", adopted)
    if new_task is not None:
        save_json(DIRECTORY / "reference-adopted-task-result.json", new_task)
    if new_prompt:
        (DIRECTORY / "reference-adopted-prompt.txt").write_text(new_prompt + "\n", encoding="utf-8")
    (DIRECTORY / "reference-adoption-pending.json").unlink()
    return {"status": "reference_ready", "reference": relative(REFERENCE),
            "reference_generation": adopted, "paid_submission": False,
            "next": "Run model --reference-reviewed with this adopted image"}


def existing_task(stage: str) -> dict:
    path = DIRECTORY / f"{stage}-result.json"
    result = read_json(path) if path.is_file() else {}
    if result.get("status") in {"failed", "cancelled", "banned", "expired"}:
        raise RuntimeError(f"Existing {stage} task ended as {result['status']}; inspect before preparing a new attempt")
    if result and not result.get("task_id"):
        raise RuntimeError(f"Earlier {stage} submission has uncertain state; recover its task ID before retrying")
    return result


def submit_or_resume(stage: str, result: dict, arguments: list[str]) -> dict:
    result_path = DIRECTORY / f"{stage}-result.json"
    if result.get("status") == "success":
        print(f"Reusing successful {stage} task {result.get('task_id')}; no generation submitted", file=sys.stderr)
        return result
    log = DIRECTORY / f"{stage}-progress.log"
    if not result:
        # Write before network I/O: a crash cannot silently authorize another fee.
        save_json(result_path, {"status": "submitting", "stage": stage, "asset_id": ASSET_ID})
        result = task_summary(run_cli([*arguments, "--no-wait"], log))
        result.setdefault("status", "queued")
        save_json(result_path, result)
    artifacts = DIRECTORY / "artifacts" / stage
    result = task_summary(run_cli(["task", "watch", result["task_id"], "--download", "--out",
                                   relative(artifacts), "--timeout", "1800"], log))
    save_json(result_path, result)
    for path in artifacts.rglob("task.json"):
        save_json(path, read_json(path))
    if result.get("status") != "success":
        raise RuntimeError(f"{stage.capitalize()} task did not succeed: {result.get('status')}")
    return result


def validate_plan(plan: dict) -> None:
    if (plan.get("valid") is not True or plan.get("model") != MODEL or
            plan.get("candidates") != 1 or len(plan.get("steps", [])) != 1):
        raise RuntimeError("H3.1 dry-run must be valid with one candidate and one generation step")
    step = plan["steps"][0]
    payload = step.get("payload", {})
    expected = {"model": MODEL, "texture": True, "pbr": True, "texture_quality": "extreme",
                "texture_alignment": "original_image", "orientation": "align_image"}
    if step.get("task_type") != "image_to_model" or any(payload.get(key) != value for key, value in expected.items()):
        raise RuntimeError("Dry-run did not preserve H3.1 image-to-model extreme PBR parameters")
    if "face_limit" in payload:
        raise RuntimeError("This bridge preserves native H3.1 geometry; no face_limit is requested")


def bridge_installation(path: Path, imported_triangles: int) -> dict:
    """Keep the full bridge report, including route, supports and collision checks."""
    reports = []

    def find(value):
        if isinstance(value, str):
            try:
                find(json.loads(value))
            except (ValueError, TypeError):
                return
        elif isinstance(value, dict):
            if value.get("status") == "installed" and "sceneSaved" in value:
                reports.append(value)
                return
            for item in value.values():
                find(item)
        elif isinstance(value, list):
            for item in value:
                find(item)

    find(read_json(path))
    if not reports:
        raise ValueError("The Unity result contains no complete installation report")
    installed = reports[0]
    if any(report != installed for report in reports[1:]):
        raise ValueError("The Unity result contains conflicting installation reports")
    if installed.get("sceneSaved") is not True:
        raise ValueError("Save the bridge scene before recording")
    for field in ("actualTriangles", "importReportTriangles"):
        if field in installed and (type(installed[field]) is not int or installed[field] != imported_triangles):
            raise ValueError(f"Bridge {field} does not match the native imported geometry")
    # Do not use the building helper's field whitelist: the bridge report also
    # contains its curved route, supports and new collision verification.
    return portable(installed)


def create_record() -> dict:
    reference = reference_provenance()
    model = read_json(DIRECTORY / "model-result.json")
    if model.get("status") != "success":
        raise ValueError("Model generation must succeed before recording")
    model_input = read_json(DIRECTORY / "model-input.json")
    if model_input.get("sha256") != reference["sha256"]:
        raise ValueError("The current reference differs from the image used for model generation")
    plan = read_json(DIRECTORY / "model-plan.json")
    validate_plan(plan)
    unity = ROOT / "Project_TianNanGe/Assets/TianNanGe/Tripo" / ASSET_ID
    imported = read_json(unity / f"{NAME}_ImportReport.json")
    triangles = imported.get("geometry", {}).get("triangles")
    if type(triangles) is not int or triangles <= 0:
        raise ValueError("Import report must include the bridge's actual native triangle count")
    installed = bridge_installation(DIRECTORY / "install-result.json", triangles)
    costs = []
    stages = []
    for index, historical in enumerate(reference.get("previous_references", []), 1):
        if historical.get("provider") == "tripo":
            stages.append((f"reference_previous_{index}", historical))
    if reference["provider"] == "tripo":
        stages.append(("reference", reference))
    stages.append(("model", model))
    recorded_tasks = set()
    for label, result in stages:
        if result.get("task_id") in recorded_tasks:
            continue
        value = result.get("credits_consumed")
        if not isinstance(value, (int, float)) or isinstance(value, bool) or value < 0:
            raise ValueError(f"Missing actual {label} credits_consumed; no estimated fee will be recorded")
        costs.append({"stage": label, "task_id": result["task_id"], "credits_consumed": value})
        recorded_tasks.add(result["task_id"])
    prompt_path = DIRECTORY / "reference-prompt.txt"
    prompt = (reference.get("reference_prompt") if reference.get("adopted_reference") else
              prompt_path.read_text(encoding="utf-8").strip() if prompt_path.is_file() else None)
    record = {
        "asset_id": ASSET_ID, "name": NAME, "reference_source": relative(ORIGINAL),
        "reference_image": relative(REFERENCE), "reference_prompt": prompt,
        "reference_generation": reference,
        "reference_history": reference.get("previous_references", []),
        "design": "One continuous gently winding bridge with balustrades and load-bearing arches/piers below the deck",
        "model_generation": {"model": MODEL, "texture": True, "pbr": True, "texture_quality": "extreme",
                             "candidates": 1, **task_summary(model)},
        "local_glb": relative(result_artifact(model, "model.glb")),
        "geometry_policy": "Native H3.1 mesh preserved; no face limit or decimation requested",
        "actual_triangles": triangles, "import": imported, "installation": installed,
        "costs": costs, "total_credits": sum(cost["credits_consumed"] for cost in costs),
        "local_conversion": {"tool": "Blender", "remote_credits": 0,
                             "report": relative(unity / f"{NAME}_ImportReport.json")},
    }
    save_json(DIRECTORY / "generation-record.json", record)
    lines = [f"# {NAME}", "", "One winding bridge with visible load-bearing supports, generated from a single isolated reference.", "",
             f"- Model: `{MODEL}`, PBR, `texture_quality=extreme`, one candidate.",
             f"- Native geometry: {triangles:,} triangles; no face limit or decimation requested.",
             f"- Actual Tripo credits: {record['total_credits']}.",
             "- FBX conversion is local; source UVs and native texture dimensions are preserved.",
             "- HDRP MaskMap: R = metallic, G/B = 1, A = 1 - roughness.", "", "Native texture dimensions:", ""]
    for material in imported.get("materials", []):
        for channel, size in material.get("source_dimensions", {}).items():
            if size:
                lines.append(f"- Material {material['index']} {channel}: {size['width']} x {size['height']}.")
    lines += ["", "The extreme setting requests 8K color; each PBR map retains its delivered native resolution.",
              "The generation record preserves the complete Unity installation report and route/support checks.", ""]
    if reference["provider"] == "local":
        lines += ["Reference image supplied locally; the Tripo credit total covers model generation only.", ""]
    if reference.get("adopted_reference"):
        lines += [f"The adopted reference provider is `{reference['provider']}`; local adoption submitted no remote task.",
                  "The Tripo total includes all earlier paid reference tasks retained in reference history.", ""]
    for path in (DIRECTORY / "README.md", unity / "README.md"):
        path.write_text("\n".join(lines), encoding="utf-8")
    return {"status": "recorded", "record": relative(DIRECTORY / "generation-record.json"),
            "total_credits": record["total_credits"]}


def run_stage(stage: str, reviewed: bool, local_reference: str | None = None,
              adopted_reference: str | None = None, adopt_prompt: str | None = None,
              reference_provider: str | None = None, reference_task_result: str | None = None) -> dict:
    if adopted_reference:
        if stage != "reference" or local_reference:
            raise ValueError("Use --adopt-reference only with --stage reference, without --reference-image")
        return adopt_reference(Path(adopted_reference).expanduser().resolve(), reviewed,
                               Path(adopt_prompt).expanduser().resolve() if adopt_prompt else None,
                               reference_provider or "builtin_image_gen",
                               Path(reference_task_result).expanduser().resolve() if reference_task_result else None)
    if adopt_prompt or reference_provider or reference_task_result:
        raise ValueError("Adoption options require --adopt-reference")
    if (DIRECTORY / "reference-adoption-pending.json").exists():
        raise ValueError("A local reference adoption was interrupted; reconcile its pending provenance before generating")
    if local_reference:
        if stage != "model":
            raise ValueError("--reference-image is supported only for the model stage")
        if not reviewed:
            raise ValueError("Inspect the local single-bridge PNG, then pass --reference-reviewed")
        copy_reference(Path(local_reference).expanduser().resolve())
    if stage == "reference":
        source_path = DIRECTORY / "reference-source.json"
        if source_path.is_file() and read_json(source_path).get("adopted_reference") is True:
            return {"status": "reference_ready", "reference": relative(REFERENCE),
                    "reference_generation": reference_provenance(), "paid_submission": False}
        result = existing_task(stage)
        if not result and REFERENCE.is_file():
            return {"status": "reference_ready", "reference": relative(REFERENCE),
                    "next": "Inspect the existing single-bridge image before model --reference-reviewed",
                    "reference_generation": reference_provenance()}
        ensure_prompt()
        if not ORIGINAL.is_file():
            raise ValueError(f"Missing original reference: {relative(ORIGINAL)}")
        arguments = ["generate", "image-to-image", relative(ORIGINAL), "--model", REFERENCE_MODEL,
                     "--prompt", REFERENCE_PROMPT, "--name", f"tiannange-{SLUG}-reference"]
        result = submit_or_resume(stage, result, arguments)
        copy_reference(result_artifact(result, "generated_image.png"))
        return {"status": "reference_ready", "reference": relative(REFERENCE),
                "next": "Inspect the single winding bridge and its supports before model --reference-reviewed",
                "reference_generation": reference_provenance()}

    if stage == "model":
        if not reviewed:
            raise ValueError("Inspect the single-bridge reference image, then pass --reference-reviewed")
        reference = reference_provenance()
        result = existing_task(stage)
        input_path = DIRECTORY / "model-input.json"
        saved_input = read_json(input_path) if input_path.is_file() else {}
        if saved_input and saved_input.get("sha256") != reference["sha256"]:
            raise ValueError("Reference changed after model planning; do not reuse or resubmit that task")
        if result and not saved_input:
            raise ValueError("Existing model task has no reference binding; recover model-input.json before resuming")
        arguments = ["make", relative(REFERENCE), "--model", "tripo-v3.1", "--candidates", "1",
                     "-p", "texture=true", "-p", "pbr=true", "-p", "texture_quality=extreme",
                     "-p", "texture_alignment=original_image", "-p", "orientation=align_image",
                     "--name", f"tiannange-{SLUG}-h31-8k"]
        if not result:
            plan = run_cli([*arguments, "--dry-run"], DIRECTORY / "model-progress.log")
            validate_plan(plan)
            save_json(DIRECTORY / "model-plan.json", plan)
            save_json(input_path, {"reference_image": relative(REFERENCE), "sha256": reference["sha256"],
                                   "reference_reviewed": True, "model": MODEL})
        else:
            validate_plan(read_json(DIRECTORY / "model-plan.json"))
        result = submit_or_resume(stage, result, arguments)
        return {"status": "model_ready", "local_glb": relative(result_artifact(result, "model.glb")),
                "task": task_summary(result)}
    return create_record()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--stage", required=True, choices=("reference", "model", "record"))
    parser.add_argument("--reference-reviewed", action="store_true", help="The current isolated bridge image was visually checked")
    parser.add_argument("--reference-image", help="Optional local reviewed PNG to copy into the bridge source folder")
    parser.add_argument("--adopt-reference", help="Explicitly adopt a completed reviewed reference before model planning, preserving all prior references")
    parser.add_argument("--adopt-prompt-file", help="Optional text file containing the exact image edit prompt")
    parser.add_argument("--reference-provider", choices=("builtin_image_gen", "tripo", "local"), help="Adopted image provider; defaults to builtin_image_gen")
    parser.add_argument("--reference-task-result", help="Successful task-result JSON required when adopting a Tripo reference")
    args = parser.parse_args()
    with generation_lock():
        result = run_stage(args.stage, args.reference_reviewed, args.reference_image,
                           args.adopt_reference, args.adopt_prompt_file,
                           args.reference_provider, args.reference_task_result)
    print(json.dumps(portable(result), indent=2, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, KeyError) as error:
        print(f"ERROR: {safe_text(str(error))}", file=sys.stderr)
        raise SystemExit(1)
