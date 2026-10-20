# Public repository and release preparation

## Repository identity

- Repository: https://github.com/Tairos/UnityPackageCatalog
- Package ID: `io.github.tairos.unity-package-catalog`
- Package display name: Unity Package Catalog
- License: `0BSD` (no attribution requirement); root `LICENSE` and package `LICENSE.md`
- Current package version: `0.1.0-experimental.4`
- Supported native adapter: Unity 6000.6; tested Editor: 6000.6.0f1

Suggested GitHub description:

> Curated Unity package collections in the native Package Manager: Git, local, UPM and Asset Store packages, plus Built-in module presets.

Relevant GitHub topics:

`unity`, `unity-editor`, `unity-package-manager`, `upm`, `package-manager`, `package-catalog`, `asset-store`, `git-packages`, `editor-tools`, `built-in-modules`

Search descriptions should describe current capabilities: OpenUPM search, automatic registry setup, and repository scanning are not implemented.

The root README is the illustrated repository overview. The package README is the detailed consumer guide. Root TESTING links to the single maintained package testing guide, avoiding duplicate milestone instructions.

## README screenshots

Images in the repository's `.github/images` are real Unity Editor captures:

| File | View |
|---|---|
| `package-manager.png` | Mixed catalogue under Sources and native Git Install/details |
| `catalogue-inspector.png` | Inspector authoring with add/save/revert actions |
| `asset-store.png` | Owned public demo product with native Download and public Releases metadata |
| `built-in-preset.png` | Independent Built-in preset, module actions, and Preview plan |
| `asset-store-picker.png` | Purchase picker filtered to the public demo product |

Captures use temporary examples in a disposable consumer. Temporary catalogue files, personal catalogue contents, and account credentials are not added to the public repository. No private paths, credentials, account names, or purchase dates are included in the published images. Capture scripts do not ship in the editor package. The temporary examples are removed and the user's window layout is restored after capture.

## Before tagging a release

1. Preserve the selected 0BSD license files and the matching `package.json` license identifier in the published package.
2. Commit the finished source, documentation, example assets, and `.meta` files. Keep local/personal catalogues, generated test results, caches, and temporary screenshots out of the commit.
3. Complete the concrete lifecycle follow-ups in [TESTING.md](../TESTING.md), including a full restart and clean-consumer installation through the public Git URL.
4. Update `package.json` to the version you intend to publish and create a matching Git tag. Only advertise install URLs using tags that exist.
5. Keep the experimental/Unity 6000.6 compatibility statement visible until wider compatibility is tested.

The repository description and discovery topics have been updated on GitHub. Documentation, screenshots, license, and source changes remain local until committed and pushed. Tagging a release and publishing packages are separate actions.
