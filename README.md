# Solitude

*A submarine captain, alone, on the floor of an alien ocean.*

A first-person submarine survival-horror game built in **Godot 4.7.2 (C#, Forward+)** with a
PS1-era look: low-res rendering, vertex jitter, dithering, CRT camera feeds, and a fully
synthesized soundscape designed to make you uncomfortable about what's below you.

A [Bad Ideas Works](https://github.com/badsadcode) production.

## Current state — Milestone 1: the vertical slice

- **Walkable interior** — bridge, sensor bay, galley, quarters/workshop, engineering. The room
  leans as the hull pitches and rolls; you drift downhill and feel engine vibration and impacts.
- **Helm** with semi-realistic hydrodynamics: engine spool-up, anisotropic drag, rudder and dive
  planes that only bite with water flow, slow ballast tanks, self-righting roll.
  Two drive modes: **Telegraph** (ring engine orders) and **Hold-to-run**.
  Views: bridge screen / bow camera / external drone.
- **Camera monitor station** — six real hull cameras (bow, port, starboard, keel, stern, panning
  mast) rendered live into CRT screens, with signal dropouts. Any feed to the big screen or fullscreen.
- **Procedural, streamed seabed** — canyons, ridged mountains, terraced cliffs, shelf drop-offs into
  the abyss, spires, bioluminescent meadows, hydrothermal vents, and the occasional carcass of
  something enormous.
- **Atmosphere** — marine snow, volumetric fog lit by your floodlights, depth-driven darkness,
  PS1 post-processing.
- **Audio** — synthesized in real time on its own thread: abyssal drone, ventilation, engine,
  hull creaks and groans that worsen with depth, and distant calls you won't get an explanation for.

## Running

Open the folder in Godot 4.7.2 (.NET build) and press Play, or:

```
Godot_v4.7.2-stable_mono_win64.exe --path . -- --seed=1234
```

`--seed=N` replays a specific world. Without it every dive is a new ocean.

## Controls

| Context | Keys |
|---|---|
| Walking | **WASD** move · **Shift** run · **E** use · **Esc** release mouse · **F11** fullscreen |
| Helm | **W/S** throttle (telegraph steps or hold) · **A/D** + mouse rudder · **R/F** + mouse dive planes · **Space** blow / **C** flood ballast · **X** centre controls · **T** drive mode · **L** floodlights · **V** view · **E** leave |
| Monitors | **1–6** or click a screen · **F** fullscreen feed · **E/Esc** leave |

## Roadmap

1. ~~Vertical slice: interior, helm, monitors, world, atmosphere, audio~~
2. Sonar and holo table (scanned terrain hologram)
3. Manipulator arm and collectors, inventory, crafting and cooking stations, hull and power systems
4. Deep-sea stations: docking, procedural interiors (via [NoorLevel](https://github.com/badsadcode/BadLevel)), weapons, creatures
5. Diving suit and swimming outside the boat
6. Replayability: biomes, progression, logs, map fragments

## Development notes

- Everything is built in code (`Scripts/Main.cs` is the director); scenes are minimal on purpose.
- **Capture harness:** `-- --capture=<dir>` makes the game drive itself through every mode, save
  screenshots, log telemetry (fps, physics, audio underruns) and record the audio to WAV, then quit.
- 3D assets from other studio tools follow **Noor Interchange** (GLB + `.noor.json`, +Y up, metres).
