# Icon source and exports

`icon-source.ico` is the single editable source: the project icon in ICO format. Its 16/32/48/256 pixel frames are retained, without redrawing or downloading artwork. This is a preservation source, not a newly created vector master.

Use Python 3.10+ and `Pillow==12.1.1` (`scripts/requirements-icons.txt`). Run from any working directory:

```
python scripts/gen_icon.py
python scripts/gen_icon.py --check
python tests/test_icons.py
```

The generator resolves paths relative to itself. It exports `assets/7zipContext.ico` unchanged from the source, `assets/7zipContext.png` from the 256 pixel frame, and the 44/150/50 pixel PNGs referenced by the AppX manifest. PNG scaling uses LANCZOS with RGBA transparency. The dependency version is fixed for byte reproducibility. `--check` compares all five exports without modifying them; missing or stale files return 1. There are no network requests or changes to `dist`.

Only edit the source ICO, then regenerate all exports. Generated files must not be independently edited. Attribution and licensing remain described in `THIRD-PARTY-NOTICES.md`; this export pipeline does not grant new rights to existing artwork.
