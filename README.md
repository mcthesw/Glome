# Glome

An $S^3$ geometry engine in the gloam for Godot 4.7 (.NET, Forward+ Vulkan).

Glome lets you author, inspect, collide with, and raytrace scenes embedded on the 3-sphere $S^3$ inside the Godot editor using standard `MeshInstance3D`, `CollisionShape3D`, `StandardMaterial3D`, `DirectionalLight3D`, and `OmniLight3D` nodes.

## Requirements

- Godot 4.7.1 (.NET / Mono) using the **Forward+ (Vulkan)** rendering driver
- .NET 8 SDK or newer (the standalone addon compiles cleanly on both `net8.0` and `net10.0`)
- Python 3.10+ (only required to run the standalone verification script)

## Repository Layout

- `addons/glome/`: Godot C# addon (`Glome` and `Glome.Core` namespaces)
  - `Core/`: Zero-Godot double-precision $S^3$ math, spherical-cap BVH, geodesic triangles, motor, collision, and acoustic path solver
  - `Runtime/`: `S3Space3D`, `S3Anchor3D`, `S3Camera3D`, `S3CharacterController`, generic baker, and standalone runtime host
  - `Rendering/`: `S3Environment`, `S3RenderProgram`, `GlomeComputeRenderer`, `GlomeMaterials`, and `GlomeBuffers`
  - `Audio/`: `S3AudioRig` spatial audio bridge
  - `Shaders/`: Flat-include compute shader library (`glome_*.glslinc`) and built-in `glome_standard.glsl`
  - `Editor/`: `S³` main screen viewport, picking/gizmo manipulation, bookmarks, radius scaling, and view provider registry
  - `Examples/Minimal.tscn`: Ready-to-run standalone scene
- `core/Glome.Core.csproj`: Standalone pure-C# class library targeting `net8.0` (links `addons/glome/Core/**/*.cs`)
- `tests/Glome.Core.Tests/`: xUnit test suite for `Glome.Core`
- `tests/verify_standalone_addon.py`: End-to-end verification in a fresh empty Godot project (including editor checks, custom shaders, and Windows release PCK export)

## Installation and Quick Start

1. Copy `addons/glome/` into your Godot .NET project at `res://addons/glome/` (or mount this repository as a Git submodule and sync `addons/glome/` into your project).
2. Build your C# project (`dotnet build` or the Godot MSBuild button).
3. Enable **S³ Editor** in **Project -> Project Settings -> Plugins**.
4. Open `res://addons/glome/Examples/Minimal.tscn` and press **F6** (Run Current Scene), or switch to the **S³** main screen tab in the editor.

### Minimal Scene Tree

```text
S3Space3D                     # Sets curvature radius R, environment, and auto-bootstraps runtime
├── DirectionalLight3D        # Or OmniLight3D (local -Z is the light propagation direction)
├── S3Anchor3D                # Places a tangent-space chart on S^3
│   ├── MeshInstance3D        # Any Godot primitive or imported mesh with StandardMaterial3D
│   └── StaticBody3D
│       └── CollisionShape3D  # BoxShape3D or CylinderShape3D
└── S3CharacterController     # First-person walking (or 6-DOF flight when floorless)
    └── S3Camera3D            # Current = true
```

Press **F6** on any scene rooted at `S3Space3D` (with `AutoStart = true`) to launch immediately in fullscreen Vulkan compute raytracing. Default controller bindings are `WASD` or Arrow keys to move, `Shift` to sprint, `Space`/`Ctrl` for vertical thrust in floorless flight, mouse to look, and `Escape` to toggle mouse capture.

## Core Nodes and Resources

### `S3Space3D`

Root `Node3D` for an $S^3$ space.

- `Radius` (`double`, metres): Curvature radius $R > 0$. The Inspector displays the great-circle circumference ($2\pi R$), distance to the equator ($\pi R / 2$), and distance to the antipode ($\pi R$). Editing `Radius` directly preserves authored units (angles stay angles; metre arc lengths stay metres). Use the Inspector's undoable **Scale Entire Scene** button when you want all geometry and distances to scale proportionally with $R$.
- `EquatorialGroundEnabled` (`bool`, default `true`): Enables the analytical equatorial ground plane and ground-locked walking. Set to `false` (or disable `Environment.EquatorialGroundEnabled`) for floorless 3D space with 6-DOF flight.
- `Environment` (`S3Environment`): Background, ambient light, geodesic fog, shading mode, and ground material.
- `AutoStart` (`bool`, default `true`): Automatically bakes and renders the scene when run outside the editor. Set to `false` when a game host manages `GlomeBaker` and `GlomeComputeRenderer` manually.
- `RenderProgram` (`S3RenderProgram?`): Optional custom Vulkan compute shader (defaults to `res://addons/glome/Shaders/glome_standard.glsl`).
- `OutputTarget` (`TextureRect?`), `OutputMaterial` (`ShaderMaterial?`), `ResolutionScale` (`float`), `DisplayFilter`: Presentation controls for standalone runtime output.
- `BakeVisibilityPolicy` (`BakeVisibility`): Controls whether hidden nodes are skipped during baking (`HonoredUpToRoot` by default).
- `RequestRebake()`: Marks the running standalone session dirty so geometry, lights, and materials re-bake on the next frame.

### `S3Anchor3D`

Places a local Euclidean tangent chart onto $S^3$ via central (gnomonic) projection $q = (p + v/R) / \|p + v/R\|$.

- `ChartPlacement`: `ParentChart`, `Origin`, `Antipode`, `PositiveX`, `NegativeX`, `PositiveZ`, `NegativeZ`, `PositiveY`, `NegativeY`, `SphericalAngles`, or `ArcMetres`.
- `BearingDegrees`, `PolarDegrees`, `ElevationDegrees`, `ArcMetres`, `LocalOffset`: Coordinates for the chosen placement mode.
- `RelativeSpin`: Exact $SO(4)$ double-quaternion spin edited by the **S³** viewport gizmo.
- **Gnomonic distortion guidance**: Because central projection compresses radial lengths at larger offsets ($\|v\| = R/3$ gives 10% radial compression, e.g., $4.67\text{ m}$ at $R = 14\text{ m}$), the Inspector displays each anchor's furthest vertex distance, angle, and peak radial compression. Split long roads or walls across multiple anchors to keep distortion low.

### `S3CharacterController` and `S3Camera3D`

- `S3CharacterController`: Capsule-like $S^3$ body (`BodyRadius`, `BodyHeight`, `EyeHeight`, `WalkSpeed`, `SprintSpeed`, `FlightSpeed`, `MouseSensitivity`, `CaptureMouseOnStart`, `EnableDefaultInput`). Call `Move(direction, delta, sprint)` and `Look(motion)` directly when `EnableDefaultInput = false`.
- `S3Camera3D`: Sets vertical `Fov` (default `70` degrees) and `Current` camera selection. Works as a child of `S3CharacterController` or placed directly under `S3Space3D` / `S3Anchor3D` for static or scripted views.

## Materials, Textures, and Lights

- **Materials**: Assign standard Godot `StandardMaterial3D` resources (surface override or mesh surface material). Glome bakes `AlbedoColor`, `AlbedoTexture`, `Uv1Scale`, `Uv1Offset`, `Roughness`, `Metallic`, `Emission`, `EmissionEnergyMultiplier`, `TextureFilter`, `TextureRepeat`, `Transparency` (`AlphaScissor` with `AlphaScissorThreshold` in primary rays, shadow rays, and CPU picking), and `CullMode` (`Disabled` for double-sided geometry).
- **Textures**: Textures are packed into four GPU `texture2DArray` banks grouped by resolution, paired with four samplers (`nearest_repeat`, `linear_repeat`, `nearest_clamp`, `linear_clamp`).
- **Lights**: Add standard `DirectionalLight3D` and `OmniLight3D` nodes under `S3Space3D` or any `S3Anchor3D`. `DirectionalLight3D` shines along its local `-Z` axis (matching Godot convention); both light types honor `LightColor`, `LightEnergy`, and `ShadowEnabled`, and `OmniLight3D` honors `OmniRange`.

## Customizing Rendering Styles

Glome supports three progressive customization layers:

1. **Built-in Shading Modes (`S3Environment.ShadingMode`)**: Switch between `StandardLit` (GGX specular + Lambert diffuse + geodesic shadows), `Unlit`, and `ClayClean` (studio clay shading for layout inspection) with zero shader code.
2. **Screen-Space Post-Processing (Layer 2)**: Assign a Godot `ShaderMaterial` (`.gdshader`) to `S3Space3D.OutputMaterial`, optionally lowering `ResolutionScale` and setting `DisplayFilter = Nearest` for retro pixelation, dithering, CRT, or color grading.
3. **Custom Vulkan Compute Shader (Layer 3)**: Assign an `S3RenderProgram` resource to `S3Space3D.RenderProgram` pointing at your own `#[compute]` GLSL shader. Include Glome's Set 0 library headers in order:

```glsl
#[compute]
#version 450
#include "res://addons/glome/Shaders/glome_abi.glslinc"
#include "res://addons/glome/Shaders/glome_camera.glslinc"
#include "res://addons/glome/Shaders/glome_intersections.glslinc"
#include "res://addons/glome/Shaders/glome_lighting.glslinc"
```

### Set 0 Compute ABI (`glome_abi.glslinc`)

| Binding | Type | Contents |
| --- | --- | --- |
| `set = 0, binding = 0` | `writeonly image2D` (`rgba8`) | Output color image |
| `set = 0, binding = 1` | `readonly buffer` (`Triangle[]`) | Baked geodesic triangles (160 B each) |
| `set = 0, binding = 2` | `readonly buffer` (`CapNode[]`) | Spherical-cap BVH nodes (32 B each) |
| `set = 0, binding = 3` | `readonly buffer` (`MaterialRecord[]`) | Baked materials (48 B each) |
| `set = 0, binding = 4` | `buffer` (`Diagnostic[]`) | CPU/GPU parity probe slots (64 B each) |
| `set = 0, binding = 5` | `readonly buffer` (`uvec4` + `LightRecord[]`) | Unified directional and omni lights (32 B each) |
| `set = 0, binding = 6` | `uniform Environment` (`std140`) | Background, ambient, fog, mode, ground UV (80 B) |
| `set = 0, binding = 7..10` | `texture2DArray` (`bank0`..`bank3`) | Four resolution-grouped albedo texture banks |
| `set = 0, binding = 11..14` | `sampler` | `nearest_repeat`, `linear_repeat`, `nearest_clamp`, `linear_clamp` |
| `push_constant` | `Camera` (`std430`, 96 B) | `right`, `up`, `back`, `pos`, `lens`, `dims` |

To pass game-specific GPU buffers, declare `set = 1` bindings starting at `binding = 0` in your compute shader and populate `S3RenderProgram.CaptureSet1` with matching `ProgramBuffer` entries (and optional `SpecializationValues` for shader specialization constants).

## Editor Integration and Custom Views

Enabling the **S³ Editor** plugin adds the **S³** main screen tab with live compute raytracing, click-to-select picking, $SO(4)$ translation/rotation gizmos, camera bookmarks, and Inspector tools for `S3Space3D` and `S3Anchor3D`.

Game plugins can register additional preview modes in the **S³** viewport toolbar by implementing `Glome.Editor.IGlomeEditorViewProvider` and calling `Glome.Editor.GlomeEditorViews.Register(provider)` inside `_EnterTree()`, disposing the returned handle in `_ExitTree()`.

## Running Tests

Run pure-C# math, BVH, collision, and acoustic unit tests:

```bash
dotnet test tests/Glome.Core.Tests/Glome.Core.Tests.csproj
```

Run full end-to-end standalone addon verification (builds a fresh empty Godot 4.7.1 .NET project, verifies `Minimal.tscn`, a Layer-2 `.gdshader`, a custom Layer-3 compute shader, editor plugin workflows, and a cold Windows release PCK export):

```bash
python tests/verify_standalone_addon.py --godot /path/to/Godot_v4.7.1-stable_mono_console
```

## License

Glome is released under the [MIT License](LICENSE).

