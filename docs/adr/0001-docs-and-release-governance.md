---
status: accepted
---

# Documentation Site, Design Contract Gate, and Release Governance

We host Glome's bilingual documentation (`en` default at `/`, `zh-cn` at `/zh-cn/`) as an Astro Starlight project in `site/`, deployed to `glome.sworld.club` via Cloudflare Pages (owned by `sworld-infra` OpenTofu), while keeping architectural decision records under `docs/adr/` and canonical terminology in root `CONTEXT.md`. A machine-enforced `site/design-contract.json` CI gate locks all approved colors, font families, forbidden CSS primitives (no gradients, backdrop blur, or diffuse glow shadows), and forbidden UI punctuation (no middle-dot `·` separators), and `release-please` (`simple` mode) manages `CHANGELOG.md`, `addons/glome/plugin.cfg` version bumps, and lean addon-only release archives under the retained MIT license.

## Considered Options

- **VitePress vs. Astro Starlight**: Starlight was chosen for clean bespoke landing-page ownership, Expressive Code (`glsl`/`csharp` frames and dual-theme sync), static build-time KaTeX (`remark-math` + `rehype-katex`), Pagefind search, and zero-framework island interactivity.
- **`release-plz` vs. `release-please`**: `release-plz` targets Rust/Cargo workspaces exclusively; `release-please` supports generic repositories and `extra-files` updates for `addons/glome/plugin.cfg` while preserving linear history.
- **MIT vs. BSD-3-Clause / Apache-2.0**: MIT already requires retaining the copyright notice (`Copyright (c) 2026 Sworld`) and permission notice with minimal friction for Godot users, so switching licenses adds no value.
- **Standalone Authoring Skill vs. `/llms.txt`**: Build-time `/llms.txt` generation provides zero-drift agent indexing while v0.2 APIs evolve, deferring a separate authoring skill until the surface stabilizes.

## Consequences

- Adding or changing any color, font family, or banned CSS visual primitive requires an explicit update to `site/design-contract.json` approved by `@mcthesw` via `CODEOWNERS`.
- Routine PR CI stays fast by running only docs/design/API-sync checks and (when `Core/` changes) pure-C# `Glome.Core.Tests`; Godot/Vulkan acceptance (`tests/verify_standalone_addon.py`) remains local or manual.
- `sworld-infra` remains the sole owner of `glome.sworld.club` DNS and Cloudflare Pages resources; this repository carries no cloud deployment credentials.
