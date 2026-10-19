#!/usr/bin/env python3
"""Run one planned building and one stage at a time; never batch or fan out.

  python GenerateTripoBuilding.py --id FarPavilion03 --stage reference
  # Inspect the saved reference image before the following command.
  python GenerateTripoBuilding.py --id FarPavilion03 --stage model --reference-reviewed
  # Import locally and install in Unity with the separate scripts, then:
  python GenerateTripoBuilding.py --id FarPavilion03 --stage record

Only IDs in SourceAssets/Tripo/building-plan.json are accepted. Starting a new
building requires every earlier building to have a successful installation.
The model stage uses H3.1 extreme/PBR with exactly one candidate and downloads
the original GLB. It does not run remote conversion, upscaling, or Unity import.
Successful results are reused; interrupted submissions with a task ID resume
watching that task. Ambiguous submissions stop instead of risking another fee.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "SourceAssets/Tripo"
PLAN = SOURCE / "building-plan.json"
PROXY = "http://127.0.0.1:7897"
MODEL = "v3.1-20260211"
TASK_FIELDS = {"task_id", "type", "status", "credits_consumed", "created_at", "completed_at",
               "output_dir", "files", "model_file", "preview", "source_task_id", "credits_breakdown"}
INSTALL_FIELDS = {"status", "scene", "target", "hiddenVisual", "sourcePrefabPreserved", "wrapperPrefab",
                  "fbx", "material", "shader", "keywords", "textures", "modelUpAxis", "rawModelBounds",
                  "yaw", "requestedWorldSize", "oldWorldBounds", "installedWorldBounds", "localPosition",
                  "localScale", "rendererCount", "collidersBefore", "collidersAfter", "collisionUnchanged",
                  "sceneSaved"}


def read_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object: {relative(path)}")
    return value


def relative(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return path.name


def local_path(value: str) -> Path:
    path = Path(value)
    path = (ROOT / path).resolve() if not path.is_absolute() else path.resolve()
    if not path.is_relative_to(ROOT):
        raise ValueError("Artifact paths must remain inside this repository")
    return path


def safe_text(value: str) -> str:
    value = value.replace(str(ROOT) + os.sep, "")
    value = re.sub(r"https?://[^\s\"'<>]+", "[remote-url-omitted]", value)
    value = re.sub(r"\btsk_[A-Za-z0-9_-]+", "[key-omitted]", value)
    value = re.sub(r"(?i)\bBearer\s+\S+", "Bearer [omitted]", value)
    if value.startswith("/") or re.match(r"^[A-Za-z]:[\\/]", value):
        return Path(value).name
    return value


def portable(value):
    if isinstance(value, dict):
        return {key: portable(item) for key, item in value.items()
                if not re.search(r"(?i)(api[_-]?key|authorization|secret|access[_-]?token|signature)", key)}
    if isinstance(value, list):
        return [portable(item) for item in value]
    if isinstance(value, str):
        return safe_text(value)
    return value


def save_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(portable(value), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def task_summary(task: dict) -> dict:
    return portable({key: value for key, value in task.items() if key in TASK_FIELDS})


def installation(path: Path) -> dict:
    """Find the installed report, including Unity CLI's nested JSON-string result."""
    if not path.is_file():
        return {}

    def find(value):
        if isinstance(value, str):
            try:
                return find(json.loads(value))
            except (ValueError, TypeError):
                return None
        if isinstance(value, dict):
            if value.get("status") == "installed":
                return value
            for item in value.values():
                result = find(item)
                if result:
                    return result
        return None

    result = find(read_json(path)) or {}
    return portable({key: value for key, value in result.items() if key in INSTALL_FIELDS})


def load_building(building_id: str) -> tuple[list[dict], dict]:
    buildings = read_json(PLAN).get("buildings", [])
    matches = [item for item in buildings if item.get("id") == building_id]
    if len(matches) != 1:
        raise ValueError(f"Unknown or duplicate building ID {building_id!r}; use exactly one ID from building-plan.json")
    item = matches[0]
    for key in ("id", "name", "slug"):
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", item.get(key, "")):
            raise ValueError(f"Invalid plan field {key}")
    if item.get("model") != MODEL or item.get("texture_quality") != "extreme" or item.get("pbr") is not True:
        raise ValueError("This helper only runs planned H3.1 extreme PBR buildings")
    return buildings, item


def require_previous_installed(buildings: list[dict], current: dict) -> None:
    for item in buildings:
        if item["id"] == current["id"]:
            return
        report = installation(SOURCE / item["id"] / "install-result.json")
        if report.get("status") != "installed" or report.get("sceneSaved") is not True:
            raise ValueError(f"Complete and install {item['id']} before starting {current['id']}")


@contextmanager
def generation_lock():
    # One lock for the entire repository, not one lock per building.
    digest = hashlib.sha256(str(ROOT).encode()).hexdigest()[:20]
    path = Path(tempfile.gettempdir()) / f"tiannange-tripo-{digest}.lock"
    with path.open("a+") as handle:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError("Another building helper is running; finish that single building first")
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)


def run_cli(arguments: list[str], log: Path) -> dict:
    executable = shutil.which("tripo")
    if not executable:
        raise RuntimeError("tripo CLI was not found")
    environment = os.environ.copy()
    environment.update({key: PROXY for key in ("HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy")})
    command = [executable, *arguments, "--json", "--yes", "--no-open"]
    log.parent.mkdir(parents=True, exist_ok=True)
    records = []
    with log.open("a", encoding="utf-8") as output:
        output.write(json.dumps({"command": portable(command)}, ensure_ascii=False) + "\n")
        output.flush()
        process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, bufsize=1)
        try:
            assert process.stdout is not None
            for line in process.stdout:
                try:
                    record = json.loads(line)
                except ValueError:
                    record = None
                if isinstance(record, dict):
                    records.append(record)
                    clean = json.dumps(portable(record), ensure_ascii=False)
                else:
                    clean = safe_text(line.rstrip())
                output.write(clean + "\n")
                output.flush()
                if clean:
                    print(clean, file=sys.stderr, flush=True)
            code = process.wait()
        except BaseException:
            process.terminate()
            process.wait(timeout=15)
            raise
    if code:
        raise RuntimeError(f"Tripo exited {code}; inspect {relative(log)}. No automatic paid retry was made.")
    candidates = [record for record in records if not record.get("event") and
                  (record.get("task_id") or "dry_run" in record)]
    if not candidates:
        raise RuntimeError("CLI returned no task or plan JSON; submission state is retained to avoid duplicate charges")
    return candidates[-1]


def result_artifact(result: dict, expected: str) -> Path:
    directory = local_path(result["output_dir"]) if result.get("output_dir") else None
    candidates = []
    if expected == "model.glb" and result.get("model_file"):
        candidates.append(local_path(result["model_file"]))
    if directory:
        candidates.append(directory / expected)
    for raw in result.get("files", []):
        if Path(raw).name == expected:
            candidates.append(local_path(raw) if "/" in raw else directory / raw if directory else ROOT / raw)
    for candidate in candidates:
        if candidate.is_file() and candidate.stat().st_size:
            return candidate
    raise RuntimeError(f"Successful task has no local {expected}; restore it with 'tripo task get <id> --download'. Do not regenerate.")


def generate(buildings: list[dict], item: dict, stage: str, reviewed: bool) -> dict:
    directory = SOURCE / item["id"]
    result_path = directory / f"{stage}-result.json"
    result = read_json(result_path) if result_path.is_file() else {}
    # Enforce the plan order even when this stage's result already exists.
    # Reuse never bypasses the one-building-at-a-time workflow gate.
    require_previous_installed(buildings, item)
    if result.get("status") == "success":
        print(f"Reusing successful {stage} task {result.get('task_id')}; no generation submitted", file=sys.stderr)
    else:
        directory.mkdir(parents=True, exist_ok=True)
        log = directory / f"{stage}-progress.log"
        if result.get("status") in {"failed", "cancelled", "banned", "expired"}:
            raise RuntimeError(f"Existing {stage} task ended as {result['status']}; inspect it before explicitly preparing a new attempt")
        if result and not result.get("task_id"):
            raise RuntimeError(f"An earlier {stage} submission has uncertain state. Check Tripo history and recover its task ID before retrying")
        if not result:
            if stage == "reference":
                prompt = item["reference_prompt"]
                if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > 1024:
                    raise ValueError("reference_prompt must contain 1–1024 characters")
                image = local_path(item["source"])
                if not image.is_file():
                    raise ValueError(f"Missing original reference: {relative(image)}")
                prompt_file = directory / "reference-prompt.txt"
                if prompt_file.exists() and prompt_file.read_text(encoding="utf-8").strip() != prompt.strip():
                    raise ValueError("Saved reference prompt differs from plan; review it before generating")
                if not prompt_file.exists():
                    prompt_file.write_text(prompt + "\n", encoding="utf-8")
                arguments = ["generate", "image-to-image", relative(image), "--model", "seedream_v5",
                             "--prompt", prompt, "--name", f"tiannange-{item['slug']}-reference"]
            else:
                if not reviewed:
                    raise ValueError("Inspect this building's reference image, then pass --reference-reviewed to run the model stage")
                reference_result = read_json(directory / "reference-result.json")
                reference = directory / f"{item['slug']}-reference.png"
                if reference_result.get("status") != "success" or not reference.is_file():
                    raise ValueError("Run the reference stage and inspect its single-building image first")
                arguments = ["make", relative(reference), "--model", "tripo-v3.1", "--candidates", "1",
                             "-p", "texture=true", "-p", "pbr=true", "-p", "texture_quality=extreme",
                             "-p", "texture_alignment=original_image", "-p", "orientation=align_image",
                             "--name", f"tiannange-{item['slug']}-h31-8k"]
                plan = run_cli([*arguments, "--dry-run"], log)
                save_json(directory / "model-plan.json", plan)
                if plan.get("valid") is not True or plan.get("candidates") != 1 or len(plan.get("steps", [])) != 1:
                    raise RuntimeError("Model dry-run must be valid and contain exactly one generation task and one candidate")
            # Persist the intent before a paid submission. A crash without a task ID
            # is treated as ambiguous, never as permission to create a second task.
            save_json(result_path, {"status": "submitting", "stage": stage, "building_id": item["id"]})
            result = task_summary(run_cli([*arguments, "--no-wait"], log))
            result.setdefault("status", "queued")
            save_json(result_path, result)
        artifacts = directory / "artifacts" / stage
        result = task_summary(run_cli(["task", "watch", result["task_id"], "--download", "--out",
                                       relative(artifacts), "--timeout", "1800"], log))
        save_json(result_path, result)
        # CLI task.json intentionally omits output URLs. Sanitize any future
        # versions' metadata too, while retaining useful local input parameters.
        for path in artifacts.rglob("task.json"):
            save_json(path, read_json(path))
        if result.get("status") != "success":
            raise RuntimeError(f"Task did not succeed: {result.get('status')}")
    if stage == "reference":
        image = result_artifact(result, "generated_image.png")
        target = directory / f"{item['slug']}-reference.png"
        if not target.exists():
            shutil.copy2(image, target)
        elif hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(image.read_bytes()).digest():
            raise RuntimeError("Existing canonical reference differs from successful task output; left unchanged")
        return {"status": "reference_ready", "reference": relative(target),
                "next": "Inspect the single-building image before model --reference-reviewed", **task_summary(result)}
    model = result_artifact(result, "model.glb")
    return {"status": "model_ready", "local_glb": relative(model), **task_summary(result)}


def create_record(item: dict) -> dict:
    directory = SOURCE / item["id"]
    unity = ROOT / "Project_TianNanGe/Assets/TianNanGe/Tripo" / item["id"]
    reference = read_json(directory / "reference-result.json")
    model = read_json(directory / "model-result.json")
    if reference.get("status") != "success" or model.get("status") != "success":
        raise ValueError("Both reference and model must have successful results before recording")
    imported = read_json(unity / f"{item['name']}_ImportReport.json")
    installed = installation(directory / "install-result.json")
    if installed.get("status") != "installed" or installed.get("sceneSaved") is not True:
        raise ValueError("Install the building and save its Unity scene before recording")
    costs = []
    for label, result in (("reference", reference), ("model", model)):
        value = result.get("credits_consumed")
        if not isinstance(value, (int, float)) or isinstance(value, bool) or value < 0:
            raise ValueError(f"Missing actual {label} credits_consumed; no estimated price will be recorded")
        costs.append({"stage": label, "task_id": result["task_id"], "credits_consumed": value})
    prompt_path = directory / "reference-prompt.txt"
    prompt = prompt_path.read_text(encoding="utf-8").strip() if prompt_path.exists() else item["reference_prompt"]
    record = portable({
        "building_id": item["id"], "reference_source": item["source"],
        "reference_image": relative(directory / f"{item['slug']}-reference.png"),
        "reference_prompt": prompt, "reference_generation": task_summary(reference),
        "model_generation": {"model": MODEL, "texture_quality": "extreme", "pbr": True,
                             "candidates": 1, **task_summary(model)},
        "costs": costs, "total_credits": sum(value["credits_consumed"] for value in costs),
        "local_conversion": {"tool": "Blender", "remote_credits": 0, "report": relative(unity / f"{item['name']}_ImportReport.json")},
        "import": imported, "installation": installed,
    })
    lines = [f"# {item['name']}", "", "Single-building Tripo H3.1 replacement generated from the original scene reference.", "",
             f"- Building ID: `{item['id']}`", f"- Scene target: `{item['target']}`",
             f"- Model: `{MODEL}`, PBR, `texture_quality=extreme`, one candidate.",
             f"- Actual Tripo credits: {record['total_credits']} (reference {costs[0]['credits_consumed']} + model {costs[1]['credits_consumed']}).",
             "- FBX conversion is local; mesh UVs and source texture dimensions are preserved.",
             "- HDRP MaskMap: R = metallic, G/B = 1, A = 1 − roughness.", "", "Native texture dimensions:", ""]
    for material in imported.get("materials", []):
        for channel, size in material.get("source_dimensions", {}).items():
            if size:
                lines.append(f"- Material {material['index']} {channel}: {size['width']} × {size['height']}.")
    lines += ["", "The extreme setting requests 8K color; report each delivered PBR map's native dimensions rather than upscaling it.", "",
              f"Geometry: {imported.get('geometry', {}).get('triangles', 'unknown'):,} triangles." if isinstance(imported.get('geometry', {}).get('triangles'), int) else "Geometry counts are in the import report.",
              "", "Reproduction records:", "", f"- `{relative(directory / 'generation-record.json')}`",
              f"- `{record['local_conversion']['report']}`", ""]
    save_json(directory / "generation-record.json", record)
    (directory / "README.md").write_text("\n".join(lines), encoding="utf-8")
    (unity / "README.md").write_text("\n".join(lines), encoding="utf-8")
    return {"status": "recorded", "record": relative(directory / "generation-record.json"),
            "total_credits": record["total_credits"]}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--id", required=True, help="Exactly one ID from building-plan.json")
    parser.add_argument("--stage", required=True, choices=("reference", "model", "record"))
    parser.add_argument("--reference-reviewed", action="store_true", help="The current single-building reference was visually checked")
    args = parser.parse_args()
    buildings, item = load_building(args.id)
    with generation_lock():
        result = create_record(item) if args.stage == "record" else generate(buildings, item, args.stage, args.reference_reviewed)
    print(json.dumps(portable(result), indent=2, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, KeyError) as error:
        print(f"ERROR: {safe_text(str(error))}", file=sys.stderr)
        raise SystemExit(1)
