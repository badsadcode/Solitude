# Solitude: models wishlist

Tom's list of models to make, from Sage. **No rush.** Everything already has a stand-in, and each model you add replaces its stand-in automatically.

The counts come from the 20 pod stations (how often each prop appears). The sizes are what the generator reserves.

---

## ✅ Done

| Model | File | Used as |
|---|---|---|
| Ravager | `assets/ravager.glb` | the big hunter in open water |
| Fangwyrm | `assets/Abyssal_Fangwyrm.glb` | the deep-water serpent |
| Crystal Ray | `assets/Abyssal_Crystal_Ray.glb` | the gentle, curious rays |
| Dive locker | `assets/sub_utilities/locker.glb` | the suit locker in engineering |
| Abyssal Crawler | `assets/Abyssal_Crawler.glb` | the station monster (purple) |
| Abyssal Crawler, spiked | `assets/Abyssal_Crawler_Spiked.glb` | variant: tougher, glows at half strength |
| Whale of the Abyss | `assets/Whale_of_the_Abyss.glb` | the gentle giant (shown at 70 m) |

---

## Station props

Drop a model in at **`assets/props/<class>_<n>.glb`** (`console_0.glb`, `console_1.glb`, …). It appears in every station that uses that class. Several numbered files make variants, and the game picks among them. **`<class>_<n>_broken.glb`** is used where the station marks a prop as broken.

Size is width × depth × height in metres.

### Tier 1: you'll see these constantly

- [ ] **locker**: 0.5 × 0.5 × 1.9. Floor, against a wall. Appears 45×. Two or three variants; `_broken` with the door hanging open.
- [ ] **console**: 1.2 × 0.6 × 1.1. Floor, against a wall. Appears 25×. Paint the screen with vertex alpha below 1 so it glows; `_broken` gets a dark, cracked screen.
- [ ] **dive_suit**: 0.7 × 0.5 × 2.0. Floor (hangs on a rack). Appears 19×. Empty suit, visor dark. The creepiest prop in the game.
- [ ] **light_fixture_dead**: 0.6 × 0.3 × 0.1. Ceiling. Appears 102×. A broken tube hanging on one wire.
- [ ] **sign**: 0.6 × 0.05 × 0.3. Wall. Appears 295×. Just the plate; the game writes the text on it.
- [ ] **stool**: 0.4 × 0.4 × 0.5. Floor. Appears 38×.
- [ ] **crate**: 0.8 × 0.8 × 0.8. Floor. Appears 30×.

### Tier 2: room character

- [ ] **bunk**: 2.0 × 1.0 × 1.5. Floor. Appears 46×. Two tiers.
- [ ] **shelf**: 1.8 × 0.5 × 2.0. Floor. Appears 22×.
- [ ] **valve_wheel**: 0.4 × 0.2 × 0.4. Wall. Appears 25×.
- [ ] **specimen_tank**: 0.9 × 0.9 × 2.0. Floor. Appears 9×. The occupant is up to you.
- [ ] **cabinet**: 0.8 × 0.5 × 1.8. Floor. Appears 10×.
- [ ] **desk**: 1.4 × 0.7 × 0.75. Floor. Appears 6×.
- [ ] **chair**: 0.5 × 0.5 × 0.9. Floor. Appears 3×.
- [ ] **water_tank**: 1.0 × 1.0 × 1.8. Floor. Appears 3×.

### Tier 3: clutter (small and quick, but everywhere)

- [ ] **debris**: about 0.7 × 0.7 × 0.2. Floor. Appears 147×. Scrap, broken panels.
- [ ] **bones**: 0.5–0.9 × 0.3–0.5 × 0.2. Floor. Appears 48×.
- [ ] **stain**: 0.5–0.9 square, flat. Floor. Appears 77×. A decal.
- [ ] **vent_grate**: 0.8 × 0.6, flat. Floor or wall. Appears 18×.
- [ ] **pipe_run**: a 1 m segment that tiles, 0.3 thick. Wall. Appears 17×. The game repeats it along 3–6 m runs.
- [ ] **cable_run**: a 1 m segment that tiles, 0.2 thick. Ceiling. Appears 54×.

### Needs a small change from Sage first

These are "blockout" furniture: the level generator already builds them as rough boxes. If you model one, tell Sage, who will add the swap that hides the box.

- [ ] table (1.8 × 0.9 × 0.75), bed (2.0 × 1.0 × 0.75), counter (2–3 × 1.0 × 1.5)
- [ ] grow_rack (1.8 × 0.6 × 2.0), bench_lab (2.0 × 0.8 × 0.95), reactor_core (4 × 4 × 4)

### Station hardware (also replaceable, ask Sage to hook them up)

- [ ] **pressure door leaf**: about 1.0 × 0.12 × 2.4. One half of a sliding door; two slide apart.
- [ ] **junction box**: 0.6 × 0.25 × 0.8, wall. Needs a lever and a status lamp (red / green).
- [ ] **porthole frame**: about 0.6 m across, wall. The game puts the water behind it.
- [ ] **airlock hatch**: 1.0 × 2.2, wall. A heavy door with a wheel.

---

## Creatures and gear

- [ ] **Crawler variants**: any `assets/Abyssal_Crawler_<name>.glb` with the **same rig** shows up in the stations automatically. Tell Sage if a variant should be tougher, faster or glow differently.
- [ ] **Harpoon gun**: held in the diving suit. Origin at the grip, barrel pointing forward (−Y in Blender). You stare at it the whole dive.
- [ ] **Multi-tool**: the welder held in hand.
- [ ] **Loot items**: datapad, keycard, medkit box, power cell.
- [ ] **The Gloam** (working name, rename it if you like): the deep hunter that comes for light. About **24 m** long, an armoured, segmented swimmer (think giant isopod crossed with a centipede): 12–14 overlapping arched plates, a pair of long oar-like paddles on each segment, a flat shovel head with a crown of **six small pale eyes** (emission), two long feelers and hooked mandibles folded underneath. Dark, wet plates, pale belly. Rig: a bone chain head → tail (one bone per segment is perfect), plus one bone per paddle if you can; unanimated is fine, Sage rows the paddles. Save as `assets/Gloam.glb`. Right now it's a procedural placeholder in Scripts/Creatures/Gloam.cs.
- [ ] **More sea creatures**: whatever you dream up. Rigged and unanimated is fine; Sage animates on the bones.

---

## How to export (so models work first try)

**Axes and origin**
- Front faces **−Y in Blender**, so the Front view (numpad 1) looks straight at it. The exporter turns that into +Z; the game handles the rest.
- **Floor props:** origin at the bottom centre of the footprint.
- **Wall props:** origin on the wall, at the bottom centre of the back face.
- **Ceiling props:** origin at the top centre.

**Scale**
- **Apply scale before export** (Ctrl+A → Scale). Creatures can be any size; the game scales them.
- Props should match the size in the list; the game doesn't rescale props.

**PS1 look**
- 64–128 px textures for props (256–512 is fine for big creatures). Low poly: about 50–500 triangles for props.

**Glow (emission)**
- Wire the glow image into **Principled BSDF → Emission Color**; Blender only exports it that way. Use its own image, not the colour texture.
- Paint glow **dim**: a coloured 30–50% value, never white. The game boosts it, so white becomes "the sun".
- Keep glowing areas small, with a bright core and a soft falloff. Black means "doesn't glow".
- Too bright or too dim in the game? Tell Sage: each model's glow strength is one number.

**Creatures**
- Rigged, no animation needed.
- Head facing −Y in Blender, like the props.
- Keep bone names when making a variant of an existing creature.

**Checking a model before it goes in**

```powershell
& "E:\Codex\SilentMeridian\.engine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" --path F:\Sage\Solitude -- --model-shot=res://assets/YOUR_MODEL.glb --model-out=F:/Sage/model_check
```

This renders the model three ways (its own material, the game's shader, and normals as colours) from four angles, into `F:/Sage/model_check`.

`.blend` files can live in the project. Godot ignores them now.
