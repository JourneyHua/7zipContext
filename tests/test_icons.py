"""Isolated icon export checks; no installed files, network or desktop operations."""
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent


class IconExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="7zipContext-icons-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in ("scripts/gen_icon.py", "assets/icon-source.ico"):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.assertEqual(self.run_generator().returncode, 0)

    def run_generator(self, *args):
        return subprocess.run([sys.executable, str(self.root / "scripts/gen_icon.py"), *args],
                              capture_output=True, text=True)

    def test_check_is_read_only(self):
        before = {p: (p.read_bytes(), p.stat().st_mtime_ns) for p in self.root.rglob("*") if p.is_file()}
        self.assertEqual(self.run_generator("--check").returncode, 0)
        self.assertTrue(all((p.read_bytes(), p.stat().st_mtime_ns) == data for p, data in before.items()))

    def test_all_dimensions_and_transparency(self):
        for name, size in (("assets/7zipContext.png", 256), ("package/Square44x44Logo.png", 44),
                           ("package/Square150x150Logo.png", 150), ("package/StoreLogo.png", 50)):
            with Image.open(self.root / name) as image:
                self.assertEqual(image.size, (size, size))
                self.assertEqual(image.mode, "RGBA")
                self.assertEqual(image.getextrema()[3], (0, 255))

    def test_ico_frames_preserved(self):
        self.assertEqual((self.root / "assets/icon-source.ico").read_bytes(),
                         (self.root / "assets/7zipContext.ico").read_bytes())
        with Image.open(self.root / "assets/7zipContext.ico") as image:
            self.assertEqual(image.ico.sizes(), {(16, 16), (32, 32), (48, 48), (256, 256)})

    def test_missing_and_stale_exports_detected_without_repair(self):
        target = self.root / "package/StoreLogo.png"
        target.write_bytes(b"broken fixture")
        self.assertEqual(self.run_generator("--check").returncode, 1)
        self.assertEqual(target.read_bytes(), b"broken fixture")
        target.unlink()
        self.assertEqual(self.run_generator("--check").returncode, 1)
        self.assertFalse(target.exists())

    def test_repeated_generation_is_identical(self):
        def hashes():
            return {str(p.relative_to(self.root)): hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in self.root.rglob("*") if p.is_file()}
        before = hashes()
        self.assertEqual(self.run_generator().returncode, 0)
        self.assertEqual(before, hashes())

    def test_exported_pixels_match_existing_repository_artwork(self):
        # Compare decoded RGBA bytes, including RGB beneath transparent pixels.
        for name in ("assets/7zipContext.png", "package/Square44x44Logo.png",
                     "package/Square150x150Logo.png", "package/StoreLogo.png"):
            with Image.open(ROOT / name) as old, Image.open(self.root / name) as new:
                self.assertEqual(old.convert("RGBA").tobytes(), new.convert("RGBA").tobytes())

    def test_manifest_logos_exported(self):
        xml = ET.parse(ROOT / "package/AppxManifest.xml")
        names = set()
        for element in xml.iter():
            names.update(value for key, value in element.attrib.items() if "Logo" in key)
            if element.tag.endswith("}Logo") and element.text:
                names.add(element.text.strip())
        self.assertEqual(names, {"Square44x44Logo.png", "Square150x150Logo.png", "StoreLogo.png"})
        self.assertTrue(all((self.root / "package" / name).is_file() for name in names))


if __name__ == "__main__":
    unittest.main()