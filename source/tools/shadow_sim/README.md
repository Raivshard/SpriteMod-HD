# Ground-shadow simulator

Runs the game's GPU shadow model offline on a sprite and its depth map, so the look can be judged and tuned without restarting the game.

## Run

```
python source\tools\shadow_sim\shadow_sim.py
```

| Option | Default | Meaning |
|---|---|---|
| sprites | every party sprite with a `.depth.png` | sprite PNGs to simulate |
| `--fit` | `robust` | how the depth scale and ground are found (below) |
| `--az` | `0,90,180,270` | azimuths, comma-separated |
| `--scale` | `0.5` | sprite size in the simulation (detail vs speed) |
| `--elevation`, `--tilt`, `--opacity`, `--thickness`, `--depthRange` | from MenuLayout.json `battle.groundShadow` | same meaning as the config keys |
| `--label` | none | added to the output folder name |
| `--lift` | none | flyer: gap from the figure's lowest pixel down to the ground (the default shadow's base), sprite px at full size |
| `--flyer` | `centred` | `centred` = lowest 3D point exactly the gap above the ground (proposal); `game` = as the plugin does now |

Reads the game folder only. Writes a new folder per run, never overwriting: `build\reviews and reports\shadow_sim\<date>_<time>_<fit>[_label]\`.

## Output (per sprite)

| File | Shows |
|---|---|
| `<name>_az000.png` … | the sprite on a ground grid with its shadow; red ring = front sole, blue ring = rear sole |
| `<name>_side.png` | the 3D reconstruction from the side (near on the left); white line = ground |
| `<name>_sheet.png` | all of the above in one row |
| `summary.txt` | soles, depth scale, ground, point counts per sprite |

Grid: horizontal lines = constant ground depth, vertical lines = constant x.

## Fits

| `--fit` | What it does |
|---|---|
| `legacy` | exactly what plugin a97c087bcd28 does: one pixel per sole, 20 % of the texture width apart; reproduces the in-game failures |
| `robust` | proposal: soles from the figure's outline, depth = mean over each foot's lowest band, ground through the front sole (as the game, from plugin 2026-11-14 port) |
| `fixed` | depth scale = `depthRange` x texture width, no sole solving |

## Model

Same as the shaders in `source\shaders\UnityProject\Assets\Shaders`. Each body pixel is a slab in depth:

- the depth map gives its front
- `thickness` gives its rounded depth
- the sole fit gives the depth scale and the ground

The game marches from each ground pixel toward the light. The simulator splats every body point forward onto the ground instead: the same shadow, much faster in plain Python. Edge speckles are a sampling effect of the simulator, not part of the model.

Flyers: `--lift` (see above). The game uses 2 x the gap to the default shadow's base (measured in-game), so the nearest shadow lands 2 x gap / tan(elevation) from the ground point. Zu: gap 13.6 units = 127 px, so `--lift 254`.
