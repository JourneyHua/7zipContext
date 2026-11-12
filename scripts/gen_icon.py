"""Export the existing artwork; never redraw or download an icon.

Install: python -m pip install -r scripts/requirements-icons.txt
Generate: python scripts/gen_icon.py
Verify: python scripts/gen_icon.py --check
"""
import argparse
import io
import sys
from pathlib import Path

from PIL import Image, __version__ as pillow_version

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets/icon-source.ico"
PINNED_PILLOW = "12.1.1"
SIZES = {(16, 16), (32, 32), (48, 48), (256, 256)}


def outputs():
    if pillow_version != PINNED_PILLOW:
        raise ValueError(f"Pillow {PINNED_PILLOW} required; found {pillow_version}")
    source_bytes = SOURCE.read_bytes()
    with Image.open(io.BytesIO(source_bytes)) as ico:
        if ico.format != "ICO" or ico.ico.sizes() != SIZES:
            raise ValueError("Source ICO must contain exactly 16, 32, 48 and 256 pixel frames")
        master = ico.ico.getimage((256, 256)).convert("RGBA")
        if master.getbbox() is None:
            raise ValueError("Source icon is empty")
    result = {"assets/7zipContext.ico": source_bytes}
    targets = {
        "assets/7zipContext.png": 256,
        "package/Square44x44Logo.png": 44,
        "package/Square150x150Logo.png": 150,
        "package/StoreLogo.png": 50,
    }
    for name, size in targets.items():
        icon = master if size == 256 else master.resize((size, size), Image.Resampling.LANCZOS)
        with io.BytesIO() as data:
            icon.save(data, format="PNG", compress_level=9)
            result[name] = data.getvalue()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Compare all exports without writing files")
    args = parser.parse_args()
    try:
        generated = outputs()  # Validate all inputs before changing any output.
        stale = [name for name, data in generated.items()
                 if not (ROOT / name).is_file() or (ROOT / name).read_bytes() != data]
        if args.check:
            if stale:
                print("Out-of-date icons: " + ", ".join(stale), file=sys.stderr)
                return 1
            print("Icon exports: 5/5 current")
            return 0
        for name in stale:
            target = ROOT / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(generated[name])
        print(f"Icon exports: 5/5 generated ({len(stale)} updated)")
        return 0
    except (OSError, ValueError) as error:
        print(f"Icon generation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())