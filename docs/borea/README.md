# Borea listing

[KITN.toml](KITN.toml) is the listing submitted to `KSAModding/content-index/listings/KITN.toml` in [PR #354](https://github.com/KSAModding/content-index/pull/354). It uses the selected logo on white, pinned to the artwork commit with its SHA-256, dimensions and byte size.

The listing links to the [KITN forum thread](https://forums.ahwoo.com/forums/kitten-space-agency/mod-releases/kitn-autopilot-for-kitten-space-agency.1683/).

The release source is `steve-787/KITN`. Borea's watcher starts with the latest release and picks up future releases automatically. KITN requires StarMap 0.5.4 or newer and was written against game build 2026.11.10.5801 on Windows.

Validation on 2026-11-15 passed the content index's layout, schema, index, SPDX license, status, tag, image and image-reference checks. The public logo matched its image record, and the official release checker stamped v0.1.3 successfully.

A separate Borea instance launched StarMap 0.5.4 with the published v0.1.3 ZIP. KITN registered its control hook and wrote its logs inside the instance's mod folder. This was a startup smoke test.

See the [listing rules](https://github.com/KSAModding/content-index/blob/main/CONTRIBUTING.md) and [Borea instance guidance](https://github.com/KSAModding/Borea/blob/main/docs/mod-authors.md) before submission.
