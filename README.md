# Project Inventory

An editor window for browsing every asset in your Unity project — inspired by the
search tab of *Asset Inventory*, but scoped to the current project only.

## Install

**From Git** (Package Manager → `+` → *Install package from git URL…*):

```
https://github.com/Ignaciomode/com.ignaciomode.projectinventory.git
```

or add it to `Packages/manifest.json`:

```json
"com.ignaciomode.projectinventory": "https://github.com/Ignaciomode/com.ignaciomode.projectinventory.git"
```

Pin a version with a tag: `...projectinventory.git#1.0.0`.

**From disk** (for local development):

```json
"com.ignaciomode.projectinventory": "file:../../com.ignaciomode.projectinventory"
```

## Usage

Open **Tools → Project Inventory**, or right-click any asset → **Show in Project Inventory**.

| Area | Features |
|------|----------|
| Toolbar | Search, sort field + direction, grid / list, tile size, settings menu |
| Sidebar | Filter by type, top-level folder, asset label, minimum size, unreferenced only |
| Results | Virtualized thumbnail grid or sortable list; multi-select, drag & drop, context menu |
| Details | Interactive preview, metadata, type-specific info, labels, dependencies, Used By |

### Search syntax

Space-separated terms, all must match:

| Term | Meaning |
|------|---------|
| `word` | name or path contains `word` |
| `-word` | exclude |
| `t:texture` | category or type name |
| `l:label` | asset label |
| `ext:png` | file extension |
| `dir:ui` | folder contains |

### Shortcuts (results focused)

Arrows navigate · Enter opens · `F` pings · Space plays audio · Delete moves to trash · Ctrl/Cmd+A selects all.

### References

*Used By* and the *Unreferenced only* filter need a one-time project-wide reference scan
(settings menu → *Scan References*, cancellable). Scenes, scripts and anything under
`Resources`, `Editor`, `Settings` or `StreamingAssets` are treated as implicitly used.

## Requirements

Unity 6000.0 or newer. Editor-only — nothing is included in builds.
