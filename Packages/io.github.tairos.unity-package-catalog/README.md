# Unity Package Catalog — user guide

A Unity Editor extension for curated package collections in the native Package Manager. Combine Git repositories, local packages, UPM registry packages, and owned Asset Store items in `.upcjson` assets. Each catalogue adds a source under **Sources**. Separate `.upcbuiltinjson` presets configure engine modules through explicit Preview/Apply actions.

For screenshots and an overview, see [Tairos/UnityPackageCatalog](https://github.com/Tairos/UnityPackageCatalog).

**Compatibility:** tested with Unity **6000.6.0f1**. The Package Manager adapter targets Unity **6000.6** and uses undocumented internals. Other minor versions require a compatible adapter and testing. This is an experimental editor-only package.

## Installation

Choose **+ → Install package from Git URL** in Package Manager:

```text
https://github.com/Tairos/UnityPackageCatalog.git?path=/Packages/io.github.tairos.unity-package-catalog
```

Append `#<commit-or-release-tag>` with an existing revision to pin an installation. Git must be available to Unity. Unity CLI/Pipeline is used by the development project and is not required by the distributed tool.

For local development, install the package by selecting its `package.json`, or open the repository's Unity development project.

## Create and edit a catalogue

1. Choose **Assets → Create → Unity Package Catalog**.
2. Select the `.upcjson` asset and edit its **Catalogue name** in the Inspector.
3. Add entries, then click **Save catalogue**.
4. Click **Open in Package Manager** and select the catalogue under **Sources**.

Selecting a file edits it directly in the Inspector; double-clicking focuses the Inspector. **Revert changes** reloads the saved file. Saves validate the whole catalogue and check for external edits before writing. Adding or saving entries does not install packages or download Asset Store content.

The Inspector provides:

| Action | Behaviour |
|---|---|
| **Add Git package** | Creates an entry to edit with the repository URL, package ID, display name, and version. |
| **Add local package** | Select a folder containing `package.json`; the manifest supplies package metadata. |
| **Add UPM package** | Searches Unity's official registry for packages compatible with the current Editor. |
| **Add Asset Store item** | Searches purchases owned by the signed-in Unity account, with paging and checkbox selection. |

Entries default to native **Git**, **Local**, **UPM**, and **Asset Store** groups. **Group override** assigns a custom collapsible group.

## Project discovery and multiple catalogues

Imported catalogue assets under **Assets** and **Packages** are discovered automatically. Files inside Unity-ignored folders such as `Samples~` must be imported into the project before they can be used. Plain `.json` files and external catalogue selection are not supported.

**Window → Package Management → Package Catalog Settings** lists discovered catalogues and Built-in presets. Click a row to select/ping its asset; **Edit** focuses the Inspector. **Show catalogues in Package Manager** enables or disables sources for this project on this computer. It does not uninstall packages or disable preset actions.

Each Built-in preset's **Apply preset** button executes all saved module actions without saving or modifying the asset, so it also works for read-only Git packages. Progress and results appear below the preset, with action details in the Console. Save any Inspector edits first if you want to apply them. Invalid or empty presets and ongoing package operations disable the button.

Every catalogue has its own source, identified by its asset GUID. Commit `.meta` files: moving/renaming an asset preserves its identity, while deleting it removes its source without uninstalling packages.

Installed UPM packages are shared across the project. The same package ID can appear in several catalogues only when its source and version agree. Invalid edits or conflicting definitions preserve the last valid source set until corrected. The integration refreshes while Package Manager is open, waiting for current package operations to finish.

## Catalogue JSON format

Both custom extensions contain ordinary JSON. Package catalogue example:

```json
{
  "schemaVersion": 1,
  "displayName": "Project Tools",
  "packages": [
    {
      "name": "com.example.editor-tool",
      "displayName": "Editor Tool",
      "description": "Tools for this project.",
      "version": "1.0.0",
      "source": "ssh://git@github.com/YOUR-ACCOUNT/YOUR-TOOL.git#v1.0.0",
      "group": "Team Tools"
    },
    {
      "assetStoreProductId": 12345,
      "displayName": "An owned Asset Store item"
    }
  ]
}
```

The repository URL and Asset Store ID are placeholders. Replace them with your own package and an actual product you own.

| Field | Meaning |
|---|---|
| `schemaVersion` | Must be `1`. |
| `displayName` | Catalogue label under Sources. |
| `packages` | Entry array; may be empty. |
| `name` | Unique UPM package ID for Git/local/registry entries. |
| `displayName`, `version` | Required package display name and semantic version for UPM entries. |
| `source` | Git URL, `file:` path, or `registry`. |
| `description`, `group` | Optional description and native group override. |
| `assetStoreProductId` | Positive, unique Asset Store product ID. Omit UPM `name`, `source`, and `version` on these entries. |

Built-in module IDs belong in a preset rather than `packages`.

### Git and private GitHub packages

Sources accept `https://` and `ssh://` Git URLs, optionally prefixed with `git+`. Use `?path=/Packages/tool` for a package subfolder and `#<tag-or-commit>` for a pinned revision. The entry's ID/version should match the referenced `package.json`.

Private repositories use Unity's normal Git installation flow and your existing SSH agent or Git credential manager. Confirm that Unity's normal **Install package from Git URL** works first. The catalogue does not manage credentials; credential-bearing URLs are rejected. Keep private repository references in a private project or collection repository.

A known/installed native package record keeps Unity's real metadata and selected version. A catalogue does not automatically switch an installed package's source or version. Review those changes through Unity's native controls.

### Local packages

A `file:` source must point to an existing folder containing a matching `package.json`. Relative paths resolve from the catalogue file's physical folder. ID or version mismatches are rejected before saving.

The folder picker stores an absolute path. For a shareable catalogue, edit the source to a suitable relative path or use a Git/registry reference. Local paths must exist on every consumer's computer. Unity's virtual `Packages/...` asset path is not always a physical folder when a package comes from Git, cache, or an external local folder.

### Registry packages and OpenUPM

The **Add UPM package** picker uses Unity's `Client.SearchAll` API. It searches **Unity's main registry**, not OpenUPM or every project scoped registry. The search field filters those results; it is not an Internet-wide package search.

To reference an OpenUPM or other scoped-registry package:

1. Configure its registry URL and package scopes in **Project Settings → Package Manager**, following that registry's installation instructions.
2. Add a JSON entry with the exact package ID/version and `"source": "registry"`.
3. Save and use the native catalogue Install control once Unity can resolve the package.

```json
{
  "name": "com.example.vendor-tool",
  "displayName": "Vendor Tool",
  "version": "1.2.3",
  "source": "registry"
}
```

This is a placeholder entry, not a published package. For OpenUPM use the package's [manual installation instructions](https://openupm.com/docs/getting-started), including `https://package.openupm.com` and its correct scopes. If Unity cannot resolve a package through **Install package by name**, fix registry configuration before troubleshooting the catalogue.

The tool does not configure registries, publish packages, search OpenUPM directly, or operate a registry server. Unity's [SearchAll documentation](https://docs.unity.com/en-us/engine/6000.6/script-reference/unityeditor/packagemanager/client/searchall) describes the main-registry discovery boundary.

## Asset Store collections

Sign in to Unity and use **Add Asset Store item** to search owned purchases. Select across pages, then **Add selected** to place references in the draft. Existing entries are marked to prevent duplicates. Save before browsing them in Package Manager.

Unity owns authentication, ownership checks, product metadata, Download, and Import. A shared catalogue stores product IDs and optional labels; it does not bundle purchased content, store credentials, or grant another user access to products they do not own. Unowned/unavailable products may not appear. Asset Store import/removal follows Unity's normal asset workflow rather than UPM Remove.

If the picker fails, confirm sign-in and connectivity, then choose **Refresh**. If a product is missing from a source, check **My Assets** and its ownership/availability. You can turn catalogue integration off and on to retry a stopped integration.

## Built-in module presets

Choose **Assets → Create → Unity Built-in Preset**, then select the `.upcbuiltinjson` asset in the Inspector. Use **Add Built-in module**, select modules available in the current Editor, and choose **Enable** or **Disable**.

**Preview changes** and **Apply preset** save the draft first. Preview discovers the current Editor's available modules and installed dependency graph. Apply adds or removes the requested direct project dependencies through Unity's Package Manager, skips unavailable or incompatible actions, then verifies the result; script reloads do not repeat the mutation. The Console lists successful actions, modules retained as dependencies with their dependency chains, skipped actions with reasons, and any verification failures. The Inspector shows only progress and a short summary.

Unlisted modules are not explicitly added or removed; Unity may enable dependencies needed by a requested module. Presets are independent one-time actions: they do not merge across files, apply automatically, enforce builds, or add catalogue sources. Opposing presets can coexist. Unity's existing **Built-in** view shows current module state.

```json
{
  "schemaVersion": 1,
  "displayName": "Engine Modules",
  "unityVersion": "6000.6.0f1",
  "builtInModules": [
    { "name": "com.unity.modules.audio", "requirement": "required" },
    { "name": "com.unity.modules.vehicles", "requirement": "excluded" }
  ]
}
```

`required` means explicitly enable when applying; `excluded` means remove the explicit project dependency, matching Unity's Built-in Disable button. If another package still needs the module, Unity keeps it installed as a dependency; this is a successful removal of the direct entry, not a fully disabled module. A module may appear only once per preset. The recorded Unity version is informative: a different current version shows a notice, and availability is checked against the running Editor. Unavailable enables and incompatible installed sources block only the affected actions; other enables and removals still proceed. Retained package dependencies do not block removal of direct manifest entries. Unity retains those modules indirectly and the Console explains why. Excluding a module already absent from the current Editor is already satisfied. `com.unity.modules.jsonserialize` stays installed as a dependency while this tool needs it, even after its direct project entry is removed.

For older mixed catalogues, move `builtInModules` into a `.upcbuiltinjson` preset and remove the field from the `.upcjson` file. Mixed documents are rejected before rules can be silently lost. The old `.upcbuiltin` extension is no longer supported; rename the file together with its `.meta`, preferably through Unity's Project window.

## Distribute a collection package

A collection package can contain its own `package.json` and `.upcjson`. Keep optional tools out of that package's dependencies so they install only when requested. You can list this catalogue tool as a normal dependency if it is available through a configured registry; a Git URL is not a supported package-manifest dependency reference.

In a repository with `Packages/collection`, `Packages/tool-a`, and `Packages/tool-b`, install the collection using `?path=/Packages/collection`. Its entries can reference tool-a/tool-b with separate Git paths, or configured registry IDs, alongside Asset Store references.

Catalogue assets shipped inside installed packages are discovered automatically; Unity-ignored folders are excluded. Treat installed collection files as distribution content: copy a catalogue into Assets if you want a project-specific editable variant, and keep definitions for shared package IDs aligned.

Updating the collection package brings a revised catalogue. Pushing new folders or Git tags does not update already installed collections automatically. Repository scanning, automatic catalogue generation, cloning, and pulling remain future work.

## Examples, testing, and troubleshooting

The development project's `Assets/PackageCatalogs/Example Packages.upcjson` references two documentation-only local fixtures. Consumers can import **Local catalogue smoke test** from this package's Samples in Package Manager; the `.upcjson`, alpha, and beta folders are imported together so their relative paths resolve.

- **No catalogue source:** confirm the extension, successful asset import, compatibility, and integration toggle. Correct any Preferences validation message.
- **Missing script warning after upgrading:** reimport the asset and wait for compilation; importers have matching script filenames and custom Inspector editors.
- **Invalid catalogue:** select it for repair in the Inspector. Correct the JSON or restore a valid version; the last valid source set remains active while open.
- **Conflicting package definitions:** align the source/version for that ID across catalogues.
- **Cannot disable a module:** read the dependency chain in the Console after Preview and adjust the selected preset or retained package dependencies.
- **Integration stopped:** inspect the diagnostic in Preferences/Console, correct the cause, and turn the integration off and on to retry.

See [TESTING.md](TESTING.md) for verified checks and release follow-ups, [architecture](Documentation~/ARCHITECTURE.md) for the adapter boundary, and [public release preparation](Documentation~/PUBLISHING.md) for repository metadata and remaining decisions. Include exact Unity/OS versions and redacted reproduction steps in issues.

## License

Licensed under [0BSD](LICENSE.md), allowing use, modification, and redistribution without an attribution requirement. Referenced packages and Asset Store content retain their own licenses.
