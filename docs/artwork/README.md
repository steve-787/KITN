# KITN artwork

Three 3200 × 1600 PNG banners, each showing fresh UI renders from the current source on 2026-11-15:

- [White](kitn-banner-01-white.png): white background and separated windows.
- [Dark](kitn-banner-02-dark.png): dark background, gold logo and two rows.
- [Charcoal](kitn-banner-03-charcoal.png): larger Porkchop plot and floating widgets.

The main README uses the dark banner. Borea's listing icon uses [kitn-logo-preview.png](kitn-logo-preview.png), the black logo on white.

To use the selected banner:

```markdown
![Kitten Intelligent Trajectory Navigator](docs/artwork/kitn-banner-02-dark.png)
```

The [logo SVG](kitn-logo.svg) uses the vendored Lucide cat and an outlined KITN wordmark. Transparent 1024 × 1024 PNGs are available in [black](kitn-logo-black.png), [white](kitn-logo-white.png) and [gold](kitn-logo-gold.png). Lucide's ISC license is in [the icon directory](../../KITN/Assets/Icons/LICENSE).

Original fresh captures are in `screens/`; screen layouts and text are preserved. The captures use sample flight states rendered by KITN's native ImGui UI. They are composed artwork, not in-game flight screenshots. Rebuild the artwork with `python tools/build_readme_artwork.py` (Pillow, fontTools, resvg-py and Windows Bahnschrift required).
