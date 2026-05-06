# Contributing

Thanks for considering a contribution. This project is small (~1.7k lines C# + ~220 lines HLSL) and intentionally so — every line earns its keep against the demo scene, against the live game, or against a documented Unity quirk. Read the existing files before adding new ones; half of the comments are documentation.

## Ground rules

1. **One feature, one PR.** A "fragment baker editor tool" should not also "rewrite the simulation loop". Small PRs land faster.
2. **Comments answer "why", not "what".** A reader can see `s.velocity *= 0.8f`. The comment should explain *why 0.8 and not 0.5 or 0.95* — usually a tuning note from a real session.
3. **Two demos, both must keep working.** The simple `Assets/MeshFracture/Demo/MeshFractureDemo.cs` (drop-on-a-cube, Space-to-shatter) is the minimum viable example for the runtime. The WebGL demo at `Assets/Demo/` (8 fracturable objects, click-to-fracture, sliders, mobile-aware UI) is the integration test. PRs that change the API must update both; PRs that break either get bounced.
4. **No `using LeapOfLegends.*`** — this project is decoupled from the game by design. New runtime code lives in the `MeshFracture` namespace only. Demo-only code lives under `MeshFractureDemo.*`.

## Where to start

| Goal | File |
| --- | --- |
| Tweak the Voronoi math, add a new clip mode | `Assets/MeshFracture/Runtime/MeshFragmenter.cs` |
| Change cache lifecycle, add a new bake source, tune the convex-hull cook | `Assets/MeshFracture/Runtime/FragmentCache.cs` |
| Adjust simulation, gravity, trails, dissolve, physics mode | `Assets/MeshFracture/Runtime/FractureBurst.cs` |
| Tune the URP/Lit transparent runtime setup | `Assets/Demo/Runtime/MaterialFactory.cs::ConfigureFractureTransparent` |
| Update the simple "drop-on-cube" demo | `Assets/MeshFracture/Demo/MeshFractureDemo.cs` |
| Update the WebGL preview demo | `Assets/Demo/Runtime/` (`DemoBootstrap`, `DemoTargetCatalog`, `DemoTarget`, `DemoUI`) |
| Tune the WebGL build + deploy pipeline | `Tools/Build/Build-Demo.ps1`, `Tools/Build/Deploy-GhPages.ps1` |

## Adding a new feature

1. **Sketch the API.** Write the call site you wish you had. If it's awkward, the design is wrong — iterate before implementing.
2. **Implement.** Match the existing style: small fields, comment the *why*, no defensive null-checks in inner loops.
3. **Update both demos.** New flag → expose in the simple demo's inspector AND in the WebGL demo's UI panel where appropriate. New API → call it from at least one. Both demos should always render the full pipeline you intend users to wire.
4. **Verify the WebGL build.** `Tools\Build\Build-Demo.ps1 -Serve` builds and serves on `:3000` — catches WebGL2-specific regressions (texture-default binding, shadow-keyword fallout, CBUFFER alignment) that the editor lets pass.
5. **Update the README.** Architecture section, what-makes-this-robust section, when-not-to-use section — pick whichever fits your change.
6. **Update CHANGELOG.md** under the unreleased section.

## Pull request checklist

- [ ] Simple demo still works end-to-end (drop `MeshFractureDemo` on a Cube, press Space, fragments fall, fade, gone at lifetime end).
- [ ] WebGL demo still builds + works (`Tools\Build\Build-Demo.ps1 -Serve`) — click-to-fracture, sliders, fracture-all / restore-all, mobile flip below 768 px.
- [ ] No new `using LeapOfLegends.*` or other product-specific imports.
- [ ] Comments answer *why*, not *what*.
- [ ] If you touched the URP/Lit transparent setup (`MaterialFactory.ConfigureFractureTransparent` or the inline copy in `MeshFractureDemo.cs`): ZWrite stays ON. ZWrite Off + Cull Off lets back-face-lit triangles win the depth race — presents as "see-through to interior" on solid chunks.
- [ ] No new `.shader` assets unless absolutely necessary. The runtime ships zero `.shader` files; reverting to a custom URP shader risks reintroducing the WebGL2 black-fragment cross-compilation bug we already escaped.
- [ ] CHANGELOG.md updated.
- [ ] README updated if the public API or behaviour changed.

## Reporting bugs

If fragments look wrong:

1. **Reproduce in one of the demos** if possible — preferably the WebGL preview at [`https://sinanata.github.io/unity-mesh-fracture/`](https://sinanata.github.io/unity-mesh-fracture/), since most platform-specific bugs show up there. If the bug only appears in your project, attach a minimal repro: prefab + script.
2. **Include Unity version + URP version.** Both move quickly; `6000.0.x` patch releases sometimes break shader compilation.
3. **Note your platform.** Mobile (especially Adreno / Mali) sometimes silently drops shader features the desktop validates. WebGL2 has its own long-tail set of cross-compiler quirks (see the README's "Why both paths exist" section).
4. **Screenshots for visual bugs.** Side-by-side "expected vs actual" beats words.

## Licence

By contributing you agree your contributions are released under the project's MIT licence. See [LICENSE](LICENSE).
