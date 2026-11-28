"""Exercise the installed add-on with stock Godot Mono, Vulkan and a cold release export."""
from __future__ import annotations

import argparse
import os
import re
import shutil
import socket
import struct
import subprocess
import tempfile
from contextlib import contextmanager
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "tests" / "standalone"
DIAGNOSTIC = re.compile(r"^\s*(?:SCRIPT ERROR|SHADER ERROR|ERROR|WARNING):|Unhandled exception|RID allocations? .*leaked", re.M | re.I)

PROJECT = """config_version=5

[application]
config/name="FreshGlome"
run/main_scene="res://addons/glome/Examples/Minimal.tscn"
config/features=PackedStringArray("4.7", "C#", "Forward Plus")

[dotnet]
project/assembly_name="FreshGlome"

[editor_plugins]
enabled=PackedStringArray("res://addons/glome/plugin.cfg")
"""

CS_PROJECT = """<Project Sdk="Godot.NET.Sdk/4.7.1">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
</Project>
"""


def fresh_project(project: Path, addon_dir: Path) -> None:
    project.mkdir()
    addon = project / "addons" / "glome"
    shutil.copytree(addon_dir, addon)
    for source in addon.rglob("*"):
        if source.suffix not in {".cs", ".cfg", ".tscn", ".glsl", ".glslinc"}:
            continue
        code = source.read_text(encoding="utf-8-sig")
        if source.suffix == ".cs" and not code.startswith("#nullable enable"):
            raise ValueError(f"Missing nullable context: {source}")
        if re.search(r"res://(?!addons/glome/)", code):
            raise ValueError(f"Nonportable add-on source: {source}")
    (project / "FreshGlome.csproj").write_text(CS_PROJECT, encoding="utf-8")
    (project / "project.godot").write_text(PROJECT, encoding="utf-8")


def build(project: Path, logs: Path, configuration: str, label: str) -> None:
    output = run(["dotnet", "build", "FreshGlome.csproj", "-c", configuration, "--nologo"], project,
                 logs / f"{label}-{configuration}.log")
    if not all(re.search(rf"^\s*0 {kind}\(s\)", output, re.M) for kind in ("Warning", "Error")):
        raise RuntimeError(f"{configuration} build did not report zero warnings and errors")


def run(command, cwd: Path, log: Path, *, env=None, timeout=180, marker=None) -> str:
    print(f"[{log.stem}] {subprocess.list2cmdline(list(map(str, command)))}", flush=True)
    result = subprocess.run(list(map(str, command)), cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            encoding="utf-8", errors="replace", timeout=timeout,
                            env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en-US", **(env or {})})
    output = re.sub(r"\x1b\[[0-9;]*m", "", result.stdout)
    log.write_text(output, encoding="utf-8")
    if result.returncode or DIAGNOSTIC.search(output) or (marker and marker not in output):
        raise RuntimeError(f"{log.stem} failed (exit {result.returncode}), see {log}\n{output[-3600:]}")
    return output


@contextmanager
def editor_stage(project):
    layout = project / ".godot/editor/editor_layout.cfg"
    previous = layout.read_bytes() if layout.exists() else None
    with tempfile.TemporaryDirectory(prefix="glome_editor_check_", dir=project / "addons") as folder:
        stage = Path(folder)
        try:
            yield stage
        finally:
            if layout.exists() and stage.name in layout.read_text(encoding="utf-8"):
                if previous is None:
                    layout.unlink()
                else:
                    layout.write_bytes(previous)


def editor_check(godot: Path, project: Path, review: Path, logs: Path) -> None:
    name = "05-fresh-editor-s3-mainscreen"
    env = {"GLOME_EDITOR_SHOT": str(review / f"{name}.png"), "GLOME_EDITOR_SCENE": "res://Authored.tscn",
           "GLOME_EDITOR_REFERENCE": str(logs / "editor-reference.cfg")}
    # The temporary scene drives the real editor without changing the enabled plugins.
    with editor_stage(project) as stage:
        shutil.copyfile(FIXTURES / "editor_check.gd", stage / "editor_check.gd")
        resource = f"res://{stage.relative_to(project).as_posix()}"
        (stage / "EditorCheck.tscn").write_text(
            f'[gd_scene format=3]\n[ext_resource type="Script" path="{resource}/editor_check.gd" id="1"]\n'
            '[node name="EditorCheck" type="Node"]\nscript = ExtResource("1")\n', encoding="utf-8")
        def free_port():
            with socket.socket() as sock:
                sock.bind(("127.0.0.1", 0))
                return sock.getsockname()[1]
        output = run([godot, "--editor", "--path", project, "--rendering-driver", "vulkan", "--single-window",
                      "--debug-server", f"tcp://127.0.0.1:{free_port()}", "--lsp-port", free_port(),
                      "--dap-port", free_port(), "--resolution", "1920x1080",
                      f"{resource}/EditorCheck.tscn", "--", "--check-editor"],
                     project, logs / f"{name}.log", marker="[GLOME-EDITOR-PASS]", env=env)
        print("\n".join(row for row in output.splitlines() if row.startswith(("Editor ", "[GLOME-"))))


def install_checks(project: Path) -> None:
    for name in ("Acceptance.cs", "EditorChecks.cs", "CsgBakeChecks.cs", "GeodesicSpanChecks.cs",
                 "SpanBay.tscn", "SpanPier.tscn", "Authored.tscn", "Tile.tscn", "Cel.tscn", "cel.glsl", "grade.gdshader"):
        shutil.copyfile(FIXTURES / name, project / name)
    with (project / "project.godot").open("a", encoding="utf-8") as config:
        config.write('\n[autoload]\nAcceptanceDriver="*res://Acceptance.cs"\n')
    (project / "export_presets.cfg").write_text(
        '[preset.0]\nname="Windows Desktop"\nplatform="Windows Desktop"\nrunnable=true\n'
        'export_filter="all_resources"\ninclude_filter=""\nexclude_filter="*.glslinc"\n'
        '[preset.0.options]\nbinary_format/embed_pck=false\nbinary_format/architecture="x86_64"\n'
        'application/modify_resources=false\n', encoding="utf-8")


def acceptance(godot: Path, addon_dir: Path, parent: Path, review: Path | None) -> None:
    if not (addon_dir / "plugin.cfg").is_file():
        raise FileNotFoundError(f"Add-on missing plugin.cfg: {addon_dir}")
    for directory in (parent, review):
        if directory is None:
            continue
        if directory.is_relative_to(ROOT):
            raise ValueError(f"Use an artifact directory outside the checkout: {directory}")
        directory.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="run-", dir=parent))
    project, logs, images = work / "project", work / "logs", work / "screenshots"
    logs.mkdir()
    images.mkdir()
    print(f"Fresh acceptance workspace: {work}", flush=True)
    version = run([godot, "--version"], work, logs / "version.log")
    if "4.7.1.stable.mono" not in version:
        raise RuntimeError(f"Expected Godot 4.7.1 Mono, found {version}")
    fresh_project(project, addon_dir)
    run(["dotnet", "new", "sln", "--format", "sln", "-n", "FreshGlome"], project, logs / "new-solution.log")
    run(["dotnet", "sln", "FreshGlome.sln", "add", "FreshGlome.csproj"], project, logs / "add-project.log")
    for configuration in ("Debug", "ExportRelease"):
        build(project, logs, configuration, "addon-only")
    run([godot, "--headless", "--import"], project, logs / "addon-only-import.log")
    run([godot, "--headless", "--quit-after", "4"], project, logs / "headless-minimal.log")
    run([godot, "--rendering-driver", "vulkan", "--quit-after", "12", "res://addons/glome/Examples/Minimal.tscn"],
        project, logs / "addon-only-f6.log", marker="Forward+")

    install_checks(project)
    build(project, logs, "Debug", "checks")
    run([godot, "--headless", "--import"], project, logs / "checks-import.log")
    env = {"GLOME_SHOTS": str(images), "GLOME_EDITOR_REFERENCE": str(logs / "editor-reference.cfg")}

    def runtime(label, binary, cwd, scene=None, flags=()):
        command = [binary, "--rendering-driver", "vulkan", "--resolution", "1920x1080"]
        if scene:
            command.append(scene)
        output = run([*command, "--", "--accept", *flags], cwd, logs / f"{label}.log", env=env,
                     marker="[GLOME-RUNTIME-PASS]")
        if "Vulkan" not in output or "Forward+" not in output:
            raise RuntimeError(f"{label} did not use Forward+ Vulkan")
        print(output.strip())

    runtime("minimal", godot, project, "res://addons/glome/Examples/Minimal.tscn")
    runtime("cel", godot, project, "res://Cel.tscn", ("--cel",))
    editor_check(godot, project, images, logs)
    runtime("authored", godot, project, "res://Authored.tscn", ("--authored",))
    build(project, logs, "ExportRelease", "checks")
    packed = work / "export"
    packed.mkdir()
    binary = packed / "FreshGlome.exe"
    run([godot, "--headless", "--export-release", "Windows Desktop", binary], project, logs / "export.log")
    if not binary.is_file() or not binary.with_suffix(".pck").is_file():
        raise RuntimeError("Windows release executable or external PCK missing")
    # The release processes have only the PCK and published assemblies in their working directory.
    runtime("cold-standard", binary, packed, flags=("--cold",))
    runtime("cold-cel", binary, packed, flags=("--cold", "--cel"))
    captures = sorted(images.glob("*.png"))
    if len(captures) != 5:
        raise RuntimeError(f"Expected five review captures, got {len(captures)}")
    for index, capture in enumerate(captures, start=1):
        with capture.open("rb") as stream:
            header = stream.read(24)
        if (not capture.name.startswith(f"{index:02d}-") or header[:8] != b"\x89PNG\r\n\x1a\n"
                or struct.unpack(">II", header[16:24]) != (1920, 1080)):
            raise RuntimeError(f"Invalid 1080p capture: {capture}")
    if review is not None:
        for capture in captures:
            shutil.copy2(capture, review / capture.name)
    print(f"[GLOME-FRESH-PASS] {work}\n1080p review images: {review or images}")


if __name__ == "__main__":
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--godot", default=os.environ.get("GODOT_BIN", "godot"),
                     help="Godot Mono executable (default: GODOT_BIN or godot on PATH)")
    cli.add_argument("--addon-dir", type=Path, default=ROOT / "addons" / "glome")
    cli.add_argument("--temp-dir", type=Path, default=Path(tempfile.gettempdir()) / "glome-standalone")
    cli.add_argument("--review-dir", type=Path, help="Copy the five 1080p captures here")
    args = cli.parse_args()
    executable = shutil.which(args.godot)
    if not executable:
        cli.error("Godot Mono executable not found. Pass --godot or set GODOT_BIN.")
    acceptance(Path(executable).resolve(), args.addon_dir.resolve(), args.temp_dir.resolve(),
               args.review_dir.resolve() if args.review_dir else None)
