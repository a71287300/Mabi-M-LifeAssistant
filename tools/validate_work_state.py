"""Validate the work-state model on the generated holdout dataset."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageOps


def feature(image: Image.Image, feature_size: int) -> list[float]:
    fitted = ImageOps.fit(
        image.convert("RGB"),
        (feature_size, feature_size),
        method=Image.Resampling.BILINEAR,
    )
    pixels = fitted.get_flattened_data() if hasattr(fitted, "get_flattened_data") else fitted.getdata()
    return [value / 255.0 for pixel in pixels for value in pixel]


def distance(left: list[float], right: list[float]) -> float:
    return sum((a - b) ** 2 for a, b in zip(left, right)) / len(left)


def classify(path: Path, model: dict) -> str:
    vector = feature(Image.open(path), model["featureSize"])
    working_distance = distance(vector, model["workingCentroid"])
    idle_distance = distance(vector, model["idleCentroid"])
    nearest = min(working_distance, idle_distance)
    margin = (max(working_distance, idle_distance) - nearest) / max(max(working_distance, idle_distance), 1e-6)
    if nearest > model["maxKnownDistance"] or margin < model["minimumMargin"]:
        return "unknown"
    return "working" if working_distance < idle_distance else "idle"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, default=Path("models/work-state-model.json"))
    parser.add_argument("--dataset", type=Path, default=Path("artifacts/work-state-dataset/test"))
    parser.add_argument("--min-accuracy", type=float, default=0.95)
    parser.add_argument("--max-unknown-rate", type=float, default=0.03)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    model = json.loads(args.model.read_text(encoding="utf-8"))
    if not args.dataset.exists():
        raise SystemExit(f"holdout dataset is missing: {args.dataset}")

    total = 0
    correct = 0
    unknown = 0
    by_label: dict[str, dict[str, int]] = {}
    failures: list[str] = []
    confusion = {expected: {actual: 0 for actual in ("working", "idle", "unknown")} for expected in ("working", "idle")}
    for expected in ("working", "idle"):
        expected_dir = args.dataset / expected
        stats = {"total": 0, "correct": 0, "unknown": 0}
        for path in sorted(expected_dir.glob("*.png")):
            state = classify(path, model)
            confusion[expected][state] += 1
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
        "confusion": confusion,
        "model": str(args.model),
        "dataset": str(args.dataset),
        "sampleFailures": failures,
    }, ensure_ascii=False, indent=2))
    if accuracy < args.min_accuracy or unknown > total * args.max_unknown_rate:
        raise SystemExit("holdout accuracy gate failed")


if __name__ == "__main__":
    main()
