"""Validate the work-state model on the generated holdout dataset."""

from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageOps


MODEL = json.loads(Path("models/work-state-model.json").read_text(encoding="utf-8"))
DATASET_ROOT = Path("artifacts/work-state-dataset/test")


def feature(image: Image.Image) -> list[float]:
    fitted = ImageOps.fit(
        image.convert("RGB"),
        (MODEL["featureSize"], MODEL["featureSize"]),
        method=Image.Resampling.BILINEAR,
    )
    pixels = fitted.get_flattened_data() if hasattr(fitted, "get_flattened_data") else fitted.getdata()
    return [value / 255.0 for pixel in pixels for value in pixel]


def distance(left: list[float], right: list[float]) -> float:
    return sum((a - b) ** 2 for a, b in zip(left, right)) / len(left)


def classify(path: Path) -> str:
    vector = feature(Image.open(path))
    working_distance = distance(vector, MODEL["workingCentroid"])
    idle_distance = distance(vector, MODEL["idleCentroid"])
    nearest = min(working_distance, idle_distance)
    margin = (max(working_distance, idle_distance) - nearest) / max(max(working_distance, idle_distance), 1e-6)
    if nearest > MODEL["maxKnownDistance"] or margin < MODEL["minimumMargin"]:
        return "unknown"
    return "working" if working_distance < idle_distance else "idle"


def main() -> None:
    if not DATASET_ROOT.exists():
        raise SystemExit(f"holdout dataset is missing: {DATASET_ROOT}")

    total = 0
    correct = 0
    unknown = 0
    by_label: dict[str, dict[str, int]] = {}
    failures: list[str] = []
    for expected in ("working", "idle"):
        expected_dir = DATASET_ROOT / expected
        stats = {"total": 0, "correct": 0, "unknown": 0}
        for path in sorted(expected_dir.glob("*.png")):
            state = classify(path)
            stats["total"] += 1
            total += 1
            correct += state == expected
            unknown += state == "unknown"
            stats["correct"] += state == expected
            stats["unknown"] += state == "unknown"
            if state != expected and len(failures) < 20:
                failures.append(f"{path.name}: expected={expected}, got={state}")
        by_label[expected] = stats

    accuracy = correct / total if total else 0
    print(json.dumps({
        "accuracy": accuracy,
        "correct": correct,
        "total": total,
        "unknown": unknown,
        "byLabel": by_label,
        "sampleFailures": failures,
    }, ensure_ascii=False, indent=2))
    if accuracy < 0.95 or unknown > total * 0.03:
        raise SystemExit("holdout accuracy gate failed")


if __name__ == "__main__":
    main()
