"""Generate and train the CPU work-state model from labeled screenshots.

The source captures are small, so this script also creates a reproducible
dataset with the kinds of changes that happen in the real game window:
moderate scale changes, small position shifts, brightness/contrast changes,
color changes, rotation, and a little rendering blur.
"""

from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter, ImageOps


FEATURE_SIZE = 32
GAME_CENTER_X = 0.96
GAME_CENTER_Y = 0.94
GAME_CROP_SIDE = 0.145
RANDOM_SEED = 27
TRAIN_VARIANTS_PER_SOURCE = 100
TEST_VARIANTS_PER_SOURCE = 30

DEFAULT_MANIFEST = Path("artifacts/work-state-sources.json")
DEFAULT_DATASET_ROOT = Path("artifacts/work-state-dataset")
DEFAULT_OUTPUT = Path("models/work-state-model.json")


def button_crop(image: Image.Image) -> Image.Image:
    image = image.convert("RGB")
    width, height = image.size
    side = max(32, round(height * GAME_CROP_SIDE))
    side = min(side, width, height)
    center_x = round(width * GAME_CENTER_X)
    center_y = round(height * GAME_CENTER_Y)
    left = max(0, min(width - side, center_x - side // 2))
    top = max(0, min(height - side, center_y - side // 2))
    return image.crop((left, top, left + side, top + side))


def feature(image: Image.Image) -> list[float]:
    fitted = ImageOps.fit(image.convert("RGB"), (FEATURE_SIZE, FEATURE_SIZE), method=Image.Resampling.BILINEAR)
    pixels = fitted.get_flattened_data() if hasattr(fitted, "get_flattened_data") else fitted.getdata()
    return [value / 255.0 for pixel in pixels for value in pixel]


def augmented_image(image: Image.Image, randomizer: random.Random) -> Image.Image:
    source = image.convert("RGB")
    scale = randomizer.uniform(0.88, 1.12)
    scaled_width = max(8, round(source.width * scale))
    scaled_height = max(8, round(source.height * scale))
    scaled = source.resize((scaled_width, scaled_height), Image.Resampling.BICUBIC)

    canvas = Image.new("RGB", source.size, (8, 14, 12))
    shift_x = randomizer.randint(-round(source.width * 0.05), round(source.width * 0.05))
    shift_y = randomizer.randint(-round(source.height * 0.05), round(source.height * 0.05))
    canvas.paste(scaled, ((source.width - scaled_width) // 2 + shift_x,
                          (source.height - scaled_height) // 2 + shift_y))

    canvas = ImageEnhance.Brightness(canvas).enhance(randomizer.uniform(0.75, 1.25))
    canvas = ImageEnhance.Contrast(canvas).enhance(randomizer.uniform(0.82, 1.18))
    canvas = ImageEnhance.Color(canvas).enhance(randomizer.uniform(0.82, 1.18))
    if randomizer.random() < 0.35:
        canvas = canvas.filter(ImageFilter.GaussianBlur(randomizer.uniform(0.0, 0.7)))
    return canvas.rotate(
        randomizer.uniform(-3.0, 3.0),
        resample=Image.Resampling.BILINEAR,
        fillcolor=(8, 14, 12),
    )


def mean(vectors: list[list[float]]) -> list[float]:
    return [sum(vector[index] for vector in vectors) / len(vectors) for index in range(len(vectors[0]))]


def squared_distance(left: list[float], right: list[float]) -> float:
    return sum((a - b) ** 2 for a, b in zip(left, right)) / len(left)


def clear_generated_images(dataset_root: Path) -> None:
    for split in ("train", "test"):
        for label in ("working", "idle"):
            directory = dataset_root / split / label
            directory.mkdir(parents=True, exist_ok=True)
            for path in directory.glob("*.png"):
                path.unlink()


def load_manifest(path: Path) -> list[tuple[str, str, Path, bool]]:
    if not path.exists():
        raise SystemExit(
            f"source manifest is missing: {path}\n"
            "Copy tools/work-state-sources.example.json to artifacts/work-state-sources.json "
            "and update the image paths."
        )

    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise SystemExit(f"source manifest is not valid JSON: {path}: {error}") from error

    sources: list[tuple[str, str, Path, bool]] = []
    for label in ("working", "idle"):
        entries = manifest.get(label, [])
        if not isinstance(entries, list):
            raise SystemExit(f"manifest field '{label}' must be an array")
        for index, entry in enumerate(entries, start=1):
            if isinstance(entry, str):
                image_path = Path(entry)
                name = f"{label}-{index:02d}"
                crop = True
            elif isinstance(entry, dict):
                image_path = Path(str(entry.get("path", "")))
                name = str(entry.get("name", f"{label}-{index:02d}"))
                crop = bool(entry.get("crop", True))
            else:
                raise SystemExit(f"manifest entry {label}[{index - 1}] must be a path or object")

            if not image_path.is_absolute():
                image_path = path.parent / image_path
            if not image_path.exists():
                raise SystemExit(f"source image is missing: {image_path}")
            sources.append((label, name, image_path, crop))

    if not sources or not any(label == "working" for label, *_ in sources) or not any(label == "idle" for label, *_ in sources):
        raise SystemExit("manifest must contain at least one working and one idle source image")
    return sources


def prepare_sources(manifest: Path) -> list[tuple[str, str, Image.Image]]:
    sources: list[tuple[str, str, Image.Image]] = []
    for label, name, path, crop in load_manifest(manifest):
        image = Image.open(path).convert("RGB")
        sources.append((label, name, button_crop(image) if crop else image))
    return sources


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--dataset-root", type=Path, default=DEFAULT_DATASET_ROOT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--holdout-per-label", type=int, default=1,
                        help="Number of complete source images per label reserved for test-only evaluation.")
    parser.add_argument("--train-variants", type=int, default=TRAIN_VARIANTS_PER_SOURCE)
    parser.add_argument("--test-variants", type=int, default=TEST_VARIANTS_PER_SOURCE)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.holdout_per_label < 0:
        raise SystemExit("--holdout-per-label cannot be negative")
    if args.train_variants <= 0 or args.test_variants <= 0:
        raise SystemExit("variant counts must be positive")

    clear_generated_images(args.dataset_root)
    sources = prepare_sources(args.manifest)
    training_samples: dict[str, list[list[float]]] = {"working": [], "idle": []}
    counts = {"train": {"working": 0, "idle": 0}, "test": {"working": 0, "idle": 0}}
    source_names: dict[str, dict[str, list[str]]] = {
        "train": {"working": [], "idle": []},
        "test": {"working": [], "idle": []},
    }
    label_source_counts = {
        label: sum(1 for source_label, *_ in sources if source_label == label)
        for label in ("working", "idle")
    }
    for label, source_count in label_source_counts.items():
        if args.holdout_per_label >= source_count:
            raise SystemExit(
                f"--holdout-per-label must leave at least one {label} source for training "
                f"(sources={source_count}, holdout={args.holdout_per_label})"
            )
    seen_by_label = {"working": 0, "idle": 0}

    for source_index, (label, source_name, source) in enumerate(sources):
        seen_by_label[label] += 1
        is_holdout = seen_by_label[label] > label_source_counts[label] - args.holdout_per_label
        if args.holdout_per_label == 0:
            split_specs = (
                ("train", args.train_variants, 1),
                ("test", args.test_variants, 10001),
            )
        elif is_holdout:
            split_specs = (("test", args.test_variants, 10001),)
        else:
            split_specs = (("train", args.train_variants, 1),)
        for split, count, seed_offset in split_specs:
            source_names[split][label].append(source_name)
            randomizer = random.Random(RANDOM_SEED + source_index * 100000 + seed_offset)
            for variant_index in range(count):
                sample = augmented_image(source, randomizer)
                destination = args.dataset_root / split / label / f"{source_index:02d}_{variant_index:03d}.png"
                sample.save(destination, format="PNG", optimize=True)
                counts[split][label] += 1
                if split == "train":
                    training_samples[label].append(feature(sample))

    working_center = mean(training_samples["working"])
    idle_center = mean(training_samples["idle"])
    working_distances = [squared_distance(sample, working_center) for sample in training_samples["working"]]
    idle_distances = [squared_distance(sample, idle_center) for sample in training_samples["idle"]]
    max_known_distance = max(max(working_distances), max(idle_distances)) * 1.6
    model = {
        "version": 2,
        "featureSize": FEATURE_SIZE,
        "centerX": GAME_CENTER_X,
        "centerY": GAME_CENTER_Y,
        "cropSide": GAME_CROP_SIDE,
        "workingCentroid": working_center,
        "idleCentroid": idle_center,
        "maxKnownDistance": max_known_distance,
        "minimumMargin": 0.01,
        "trainingSamples": {"working": len(training_samples["working"]), "idle": len(training_samples["idle"])},
        "dataset": {"counts": counts, "sourceNames": source_names},
    }
    model["dataset"] = {
        "root": str(args.dataset_root),
        "counts": counts,
        "sourceNames": source_names,
        "holdoutPerLabel": args.holdout_per_label,
        "manifest": str(args.manifest),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(model, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    (args.dataset_root / "dataset-report.json").write_text(json.dumps({
        "seed": RANDOM_SEED,
        "trainVariantsPerSource": args.train_variants,
        "testVariantsPerSource": args.test_variants,
        "counts": counts,
        "sources": source_names,
        "holdoutPerLabel": args.holdout_per_label,
        "manifest": str(args.manifest),
        "model": str(args.output),
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(args.output), "dataset": str(args.dataset_root), "counts": counts,
                      "trainingSamples": model["trainingSamples"], "maxKnownDistance": max_known_distance}, indent=2))


if __name__ == "__main__":
    main()
