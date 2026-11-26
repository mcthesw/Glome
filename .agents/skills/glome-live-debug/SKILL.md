---
name: glome-live-debug
description: Inspect and drive a running Godot 4.7 (.NET) Glome S³ Editor session via the passive local file bridge (status, snapshot, rebake, reload_scene, focus_node, set_camera, reload_shader) with zero open ports or UI clutter.
---

# Glome Live Editor Bridge Skill

When the user has a Godot 4.7 (.NET) project open with the **S³ Editor** (`GlomeEditorPlugin`) enabled, `GlomeEditorBridge` passively polls `editor-cmd.json` in the active bridge directory.

## Bridge Directory Resolution

- **Editor (`GlomeEditorBridge`)**: checks `GlomeEditorPlugin.BridgeDirectory`, then the `GLOME_BRIDGE_DIR` environment variable, and otherwise falls back to `Path.Combine(OS.GetUserDataDir(), "glome-bridge")`.
- **CLI (`glome_bridge.py`)**: always defaults to `<cwd>/.local/glome-bridge` and does **not** read `GLOME_BRIDGE_DIR`. Either pass `--dir <editor_bridge_directory>` to the CLI, or configure `GlomeEditorPlugin.BridgeDirectory` / `GLOME_BRIDGE_DIR` to `<cwd>/.local/glome-bridge` so both sides watch the same folder.
- **Shader Reload Hook**: register a handler via `GlomeEditorPlugin.BridgeShaderReload` to handle `reload_shader` commands.

## Usage

Run the bundled Python CLI against the matching bridge directory:

```bash
python .agents/skills/glome-live-debug/scripts/glome_bridge.py --dir "<bridge_dir>" status
python .agents/skills/glome-live-debug/scripts/glome_bridge.py --dir "<bridge_dir>" snapshot
python .agents/skills/glome-live-debug/scripts/glome_bridge.py --dir "<bridge_dir>" focus_node --json '{"name":"MyAnchor"}'
python .agents/skills/glome-live-debug/scripts/glome_bridge.py --dir "<bridge_dir>" set_camera --json '{"polarDeg":45,"azimuthDeg":0,"heightM":1.65,"pitchDeg":-5}'
python .agents/skills/glome-live-debug/scripts/glome_bridge.py --dir "<bridge_dir>" rebake
python .agents/skills/glome-live-debug/scripts/glome_bridge.py --dir "<bridge_dir>" reload_scene
python .agents/skills/glome-live-debug/scripts/glome_bridge.py --dir "<bridge_dir>" reload_shader
```

## Safety Rules

- `status` writes `editor-latest.json` (active scene, unsaved status, selected nodes & S³ anchor coordinates, current camera pose) with zero GPU overhead.
- `snapshot` saves the current **S³** main-screen preview PNG to `editor-latest.png` (pruning `history/` to the 10 newest PNGs) plus `editor-latest.json`.
- `reload_scene` scans the filesystem and **refuses** to overwrite unsaved edits unless `{"force":true}` is explicitly supplied.
- Never run `godot --headless --import` concurrently against a project open in the Godot Editor; let the open Editor own resource reimport.
