"""Generate and train the CPU work-state model from labeled screenshots.

The source captures are small, so this script also creates a reproducible
dataset with the kinds of changes that happen in the real game window:
moderate scale changes, small position shifts, brightness/contrast changes,
color changes, rotation, and a little rendering blur.
"""

from __future__ import annotations

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

WORK_IMAGE = Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-46e6f67a-16fa-4269-bd31-f9fe8c1b38a7.png")
WORK_FULL_IMAGES = [
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-6f92fc03-ae0d-457b-8965-c1d0227528e0.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-23222bcc-9b48-4e53-8e63-9e9cd1530a69.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-5e9b79dc-5416-4ce7-8bc6-0423a9f12b53.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-c4d40c53-09a5-4366-9338-745074e52f97.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-95036f9c-26e0-4ee6-9100-4e051420f2af.png"),
]
IDLE_FULL_IMAGES = [
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-e0d6039a-de39-468c-820b-61b7c4656313.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-929cd60a-bcd7-4564-a006-b38fcc715675.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-8ed6ab17-f3a5-4953-89a9-e4c794218ba0.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-b03fb0ef-362b-4257-80b6-37bd673aacfd.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-f77150a2-cb52-441d-97df-b8786267b8cd.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-9171dcd4-a164-4dd1-8b5b-c9e183752d6e.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-b7acdf48-c852-423c-87c7-9e8a54eb28b2.png"),
]
IDLE_CROP_IMAGES = [
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-334bb987-a135-4196-b061-7ed71fec3b13.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-5a335130-3940-4b98-b5db-d9c77626215d.png"),
    Path(r"C:\Users\RenKai\AppData\Local\Temp\codex-clipboard-99eaaabe-5dc3-4410-94d9-9a6d3f6d0c20.png"),
]
DATASET_ROOT = Path("artifacts/work-state-dataset")
OUTPUT = Path("models/work-state-model.json")


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


def clear_generated_images() -> None:
    for split in ("train", "test"):
        for label in ("working", "idle"):
            directory = DATASET_ROOT / split / label
            directory.mkdir(parents=True, exist_ok=True)
            for path in directory.glob("*.png"):
                path.unlink()


def prepare_sources() -> list[tuple[str, str, Image.Image]]:
    sources: list[tuple[str, str, Image.Image]] = [("working", "work-crop", Image.open(WORK_IMAGE).convert("RGB"))]
    sources.extend(("working", f"work-full-{index:02d}", button_crop(Image.open(path)))
                   for index, path in enumerate(WORK_FULL_IMAGES, start=1))
    sources.extend(("idle", f"idle-full-{index:02d}", button_crop(Image.open(path)))
                   for index, path in enumerate(IDLE_FULL_IMAGES, start=1))
    sources.extend(("idle", f"idle-crop-{index:02d}", Image.open(path).convert("RGB"))
                   for index, path in enumerate(IDLE_CROP_IMAGES, start=1))
    return sources


def main() -> None:
    all_paths = [WORK_IMAGE, *WORK_FULL_IMAGES, *IDLE_FULL_IMAGES, *IDLE_CROP_IMAGES]
    if not all(path.exists() for path in all_paths):
        missing = [str(path) for path in all_paths if not path.exists()]
        raise SystemExit("training screenshots are missing:\n" + "\n".join(missing))

    clear_generated_images()
    sources = prepare_sources()
    training_samples: dict[str, list[list[float]]] = {"working": [], "idle": []}
    counts = {"train": {"working": 0, "idle": 0}, "test": {"working": 0, "idle": 0}}
    source_names: dict[str, list[str]] = {"working": [], "idle": []}

    for source_index, (label, source_name, source) in enumerate(sources):
        source_names[label].append(source_name)
        for split, count, seed_offset in (("train", TRAIN_VARIANTS_PER_SOURCE, 1),
                                           ("test", TEST_VARIANTS_PER_SOURCE, 10001)):
            randomizer = random.Random(RANDOM_SEED + source_index * 100000 + seed_offset)
            for variant_index in range(count):
                sample = augmented_image(source, randomizer)
                destination = DATASET_ROOT / split / label / f"{source_index:02d}_{variant_index:03d}.png"
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
        "dataset": {"root": str(DATASET_ROOT), "counts": counts, "sourceNames": source_names},
    }
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(model, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    (DATASET_ROOT / "dataset-report.json").write_text(json.dumps({
        "seed": RANDOM_SEED,
        "trainVariantsPerSource": TRAIN_VARIANTS_PER_SOURCE,
        "testVariantsPerSource": TEST_VARIANTS_PER_SOURCE,
        "counts": counts,
        "sources": source_names,
        "model": str(OUTPUT),
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(OUTPUT), "dataset": str(DATASET_ROOT), "counts": counts,
                      "trainingSamples": model["trainingSamples"], "maxKnownDistance": max_known_distance}, indent=2))


if __name__ == "__main__":
    main()
