---
name: glome-live-debug
description: Inspect and drive a running Godot 4.7 (.NET) Glome S³ Editor session via the passive local file bridge (status, snapshot, rebake, reload_scene, focus_node, set_camera, reload_shader) with zero open ports or UI clutter.
---

# Glome Live Editor Bridge Skill

When the user has a Godot 4.7 (.NET) project open with the **S³ Editor** (`GlomeEditorPlugin`) enabled, `GlomeEditorBridge` passively polls `<project_parent>/.local/glome-bridge/editor-cmd.json` (or the path configured in `GlomeEditorPlugin.BridgeDirectory`).

## Usage

Run the bundled Python CLI against the target project root (defaults to current working directory):

```bash
python .agents/skills/glome-live-debug/scripts/glome_bridge.py status
python .agents/skills/glome-live-debug/scripts/glome_bridge.py snapshot
python .agents/skills/glome-live-debug/scripts/glome_bridge.py focus_node --json '{"name":"MyAnchor"}'
python .agents/skills/glome-live-debug/scripts/glome_bridge.py set_camera --json '{"polarDeg":45,"azimuthDeg":0,"heightM":1.65,"pitchDeg":-5}'
python .agents/skills/glome-live-debug/scripts/glome_bridge.py rebake
python .agents/skills/glome-live-debug/scripts/glome_bridge.py reload_scene
python .agents/skills/glome-live-debug/scripts/glome_bridge.py reload_shader
```

## Safety Rules

- `status` writes `editor-latest.json` (active scene, unsaved status, selected nodes & S³ anchor coordinates, current camera pose) with zero GPU overhead.
- `snapshot` saves the current **S³** main-screen preview PNG to `editor-latest.png` (pruning `history/` to the 10 newest PNGs) plus `editor-latest.json`.
- `reload_scene` scans the filesystem and **refuses** to overwrite unsaved edits unless `{"force":true}` is explicitly supplied.
- Never run `godot --headless --import` concurrently against a project open in the Godot Editor; let the open Editor own resource reimport.
