# Synthos FX Optimizer
A trimmed fork of [d4rkAvatarOptimizer](https://github.com/d4rkc0d3r/d4rkAvatarOptimizer) by d4rkpl4y3r that keeps only the parts that measurably made avatars run faster: **FX layer merging and cleanup**.

## Why a trimmed fork?
Standalone benchmarks (built through the real VRCFury upload pipeline and measured in a standalone player) showed:
* **FX layer merging** (toggle layers → one direct blend tree) cut animator time by ~12% and CPU main thread by ~11–16% on an avatar that already used VRCFury's Direct Tree Optimizer and Blendshape Optimizer. GPU time was unchanged.
* **Shader toggles / mesh merging** reduced the mesh count on paper but drew hidden outfits every frame: draw calls went 210 → 450, render thread time +65–110% and GPU time +13–20%. That is a perf rank gain, not a real one.
* **NaNimation, material merging and texture array merging** never applied to the tested avatars.

So everything except the FX layer work is permanently off. This also removes most of the overlap with VRCFury (mesh lock-in, SPS, material rewriting).

## What it does
* Deletes useless FX layers (no states, 0 weight with no weight control, or only animating bindings that don't exist).
* Merges simple toggle and multi-toggle layers into a single direct blend tree ([technique](https://notes.sleightly.dev/dbt-combining/)).
* Deletes unused animator parameters.
* Disables phys bones whose only mesh is toggled off.
* Removes blend shapes that are never animated and merges blend shapes that are always animated in the same ratio.
* Deletes components that are disabled and never enabled by animations.
* Merges material slots on the same mesh that use the exact same material, and trims unused vertex data.

## What it does not do
* No mesh or material merging, no shader toggles, no NaNimation, no generated shaders.
* No "Create Optimized Copy". The optimizer only runs on upload, non-destructively.
* No texture compression analysis.

## How to Use
1. Add the `Synthos FX Optimizer` component to your avatar. This can be on any transform.
2. Upload the avatar. The optimizations are applied automatically before upload. The avatar in your scene stays as it is.

Global settings are under `Tools/Synthos/FX Optimizer Settings`.

With VRCFury the optimizer also runs when you enter play mode, on every avatar in the scene. Each avatar adds its optimization time to entering play mode, so disable avatars you aren't testing if that gets slow, or turn off `Optimize in Play Mode` in the global settings.

## Installation
Add the Synthos VPM listing to VCC and add **Synthos FX Optimizer** to your project. It can be installed next to the original d4rkAvatarOptimizer, but don't put both components on the same avatar.

## Options
### Apply on Upload
Automatically applies the optimizer to the avatar before uploading it to VRChat. This is non destructive.
### Optimize FX Layer
Deletes useless layers and merges toggle layers into a direct blend tree.

Toggles are layers that:
  * Have no state behaviours.
  * Have exactly two states.
  * Both states transitions point to the other state.
  * One state can only have a single transition while the other state has to have one transition with one inverse condition per condition of the first state.
  * Transition conditions can be bool `if` & `if not`, int `greater x` & `less x + 1` or float `greater x` & `less x`.
  * If one state is empty and relies on write defaults, the value the avatar shows while the toggle is off has to be known (a lower constant layer or the scene value).

Layers are never merged when they are additive or have an avatar mask while animating transforms or muscles. Layers that animate transform rotations (rotations blend differently inside blend trees) are only merged when they have a single state whose animation never changes over time.

Layers that animate the same properties as other layers can be merged as long as the merged layer can be placed so that every shared property keeps the same winner. Two merged layers never animate the same property. Curves on objects, components or blend shapes that don't exist don't count as shared.

Animations that don't change over time get a length of 0 in the merged tree, so they don't change its timing. Only one merged layer may have animations that change over time, so it keeps playing at its own speed.

Multi toggles are layers that:
  * Have no state behaviours.
  * Have n > 2 states.
  * All states animate the exact same bindings.
  * No state has transitions.
  * For each \[0, n\) there is one any state transition that points to one unique state.
  * All any state transitions have only one int equals condition with the value of the state index. They all have to use the same int parameter.
  * The int parameter can't be used with a not equals condition in any transition condition anywhere in the FX Layer.
### Delete Unused Animator Parameters
Removes parameters that aren't used by any transition, blend tree, state, parameter driver or audio behaviour from the generated controller.
### Disable Phys Bones When Unused
Adds curves that turn off phys bones that are used by only one SkinnedMeshRenderer whenever that renderer is toggled off.
### Merge Same Ratio Blend Shapes
Merges blend shapes that always get animated in the same ratio. For example if one animation sets `A` to 100 and `B` to 50 and another sets `A` to 50 and `B` to 25, `A` and `B` get merged in a 2:1 ratio.
### MMD Compatibility
Keeps blend shapes used by MMD animations and stops FX layers 0, 1 & 2 from being deleted or merged, as MMD worlds use them.
### Delete Unused Components
* Deletes components that are turned off and never get enabled by animations.
* Deletes phys bones whose dependencies got deleted.
* Deletes phys bone colliders that no remaining phys bone uses.
### Exclusions
Any transform in this list (and everything below it) is left untouched.
### Show FX Layer Merge Result
Shows which FX layers can be deleted or merged, using the VRChat rank icons:
  * Excellent - Layer is useless and can be deleted.
  * Good - Layer is a simple toggle and can be merged into a direct blend tree.
  * Very Poor - Layer can't be optimized.

Show Detailed Errors lists why a layer was rejected. This preview can be wrong with VRCFury or Modular Avatar, since they change the avatar at build time.
### Last Upload Summary
After each upload the inspector shows what the optimizer actually did to the built avatar: FX layers, FX parameters and blend shapes before and after, how many phys bones got auto disabled and how many unused components were deleted. The same line is written to the log file. With VRCFury or Modular Avatar this is the number to trust, not the preview.

## Using with VRCFury
The optimizer runs after VRCFury and rewrites the FX controller VRCFury generated. The VRCFury developer does not support avatars that use d4rkAvatarOptimizer. If a toggle breaks, **turn this optimizer off and test again before reporting it to VRCFury**, and mention that you use it if the bug only happens with it on.

## Bug Reports
Open the settings on the component and click the open folder button. That opens the trash bin folder with a `_TrashBin.zip` that holds the generated controllers and the `_SynthosFxOptimizer.log`. Attach it to an issue on this repo's [issue tracker](https://github.com/GRIMREAPER35487/Synthos-FX-Optimizer/issues). Don't report issues with this fork to d4rkpl4y3r.

## Credits & License
Based on [d4rkAvatarOptimizer](https://github.com/d4rkc0d3r/d4rkAvatarOptimizer) by d4rkpl4y3r, licensed under MIT. This fork is also MIT; see [LICENSE](LICENSE).
