# Destruction performance (Milestone 2)

All numbers below were **measured on this machine on 2026-11-10** with the built-in benchmark. None are estimates.

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 9 9900X (12 cores / 24 threads) |
| GPU | NVIDIA GeForce RTX 5070 |
| RAM | 31 GB |
| Unity | 6000.6.8f1, URP, Entities / Netcode for Entities / Unity Physics 6.6.0 |

## How the benchmark works

`PerfBenchmark` (on the "Game Systems" object) runs these steps:

1. Host a game, then rebuild the building.
2. Look at the building and measure 3 seconds of idle frames.
3. **Destroy all 119 chunks at once** (the stress command, the same as pressing F9).
4. Record every frame for 15 seconds while the debris falls and settles.

Each run appends one line to a CSV file. The columns are listed below.

| Column | Meaning |
|---|---|
| `liveCap` | Debris live cap: the maximum number of debris pieces simulated at the same time. Older pieces past the cap are frozen. |
| `pieceMultiplier` | Spawns N times more (smaller) debris pieces per chunk. This simulates several buildings' worth of destruction, since a single building only produces 728 pieces. |
| `destroyAvgMs`, `destroyAvgFps` | Average frame time and frame rate over the 15 s after the destroy. |
| `first3sMedianMs` | Median frame time during the first 3 s, the heaviest part of the collapse. |
| `p99Ms` | 99th-percentile frame time: 1 frame in 100 is slower than this. |
| `maxMs` | The single worst frame (the hitch when debris spawns). |
| `peakLiveDebris`, `peakTotalDebris` | The most pieces simulated at once, and the most pieces in total (simulated plus settled). |

To reproduce in a standalone build:

```bash
Builds/Windows/ToLive.exe -tolive-bench -tolive-cap 1000 -tolive-mult 4 -tolive-out bench.csv -screen-fullscreen 0 -screen-width 1600 -screen-height 900
```

To build the player, use **Tools > To Live > Build Windows Development Player**. In the Editor, `DevProbe.StartBenchmark(cap, mult)` runs the same benchmark.

## Results: Windows development build, 1600×900

| liveCap | multiplier | peak live | total pieces | avg ms | avg fps | first-3 s median ms | p99 ms | worst frame ms |
|---|---|---|---|---|---|---|---|---|
| unlimited | 1 | 728 | 728 | 2.56 | 390 | 2.48 | 5.55 | 25.8 |
| 1000 | 1 | 728 | 728 | 2.65 | 378 | 2.52 | 5.79 | 17.9 |
| 250 | 4 | 250 | 2912 | 4.07 | 246 | 3.21 | 11.35 | 23.9 |
| 500 | 4 | 500 | 2912 | 2.58 | 388 | 2.23 | 7.08 | 23.2 |
| 1000 | 4 | 1000 | 2912 | 3.89 | 257 | 3.64 | 11.28 | 22.7 |
| unlimited | 4 | 2912 | 2912 | 3.81 | 262 | 3.51 | 9.71 | 27.9 |
| 500 | 8 | 500 | 5824 | 3.64 | 275 | 3.18 | 10.70 | 28.1 |
| 1000 | 8 | 1000 | 5824 | 2.81 | 356 | 2.26 | 7.36 | 30.5 |
| 2000 | 8 | 2000 | 5824 | 3.44 | 291 | 2.54 | 9.63 | 25.9 |
| unlimited | 8 | 5824 | 5824 | 4.43 | 226 | 3.50 | 10.47 | 39.1 |

The raw CSV from these runs is in `Logs/bench-build.csv` on this machine (not committed).

## Results: Editor Play mode (host + client in one process), 1469×718 Game view

These Editor runs were made before spawning was spread over several frames (`MaxChunksPerFrame = 12`), so their worst-frame column also shows the spawn hitch that change removed.

| liveCap | multiplier | peak live | total pieces | avg ms | avg fps | p99 ms | worst frame ms |
|---|---|---|---|---|---|---|---|
| unlimited | 1 | 728 | 728 | 5.75 | 174 | 10.07 | 41.1 |
| 250 | 4 | 250 | 2912 | 5.43 | 184 | 9.40 | 75.9 |
| 500 | 4 | 500 | 2912 | 5.37 | 186 | 9.21 | 71.8 |
| 1000 | 4 | 1000 | 2912 | 5.75 | 174 | 10.24 | 72.1 |
| unlimited | 4 | 2912 | 2912 | 7.18 | 139 | 12.92 | 80.4 |
| 500 | 8 | 500 | 5824 | 5.53 | 181 | 10.77 | 138.7 |
| 1000 | 8 | 1000 | 5824 | 6.20 | 161 | 11.66 | 159.2 |
| 2000 | 8 | 2000 | 5824 | 6.27 | 159 | 11.46 | 136.8 |
| unlimited | 8 | 5824 | 5824 | 10.17 | 98 | 18.09 | 119.5 |

## What the numbers say

- **On this PC, debris simulation isn't the bottleneck at any cap we tested.**
  - The build stays far above 60 fps (16.7 ms) even with 5,824 pieces simulated at once. That case averages 4.4 ms, with a p99 of 10.5 ms.
  - The difference between caps is close to run-to-run noise in the build.
  - The Editor, which carries much more overhead, shows the trend more clearly. With 8 times the debris, the average frame went from 6.2 ms (cap 1000) to 10.2 ms (no cap), and p99 went from 11.7 ms to 18.1 ms.
- **The real cost is the spawn hitch, not the simulation.**
  - Turning a chunk into debris is a structural change: it creates entities and colliders.
  - Before spawning was spread out, destroying the whole building in the Editor caused single frames of 70 to 160 ms.
  - With 12 chunks per frame, the worst frame in the build is 18 to 39 ms. That's one or two dropped frames at the moment of a full collapse.
- **Settled debris costs almost nothing.** Pieces that stop moving drop their physics body and become plain rendered boxes.

## Recommendation

- **Live debris cap: 1000.** This is now the default on the "Debris Config" object.
  - Measured at 1000 live: 2.8 to 3.9 ms average and 7.4 to 11.3 ms p99 in the build, with 2,912 to 5,824 total pieces. On this PC that leaves more than 5 ms of headroom under 16.7 ms even at p99.
  - Removing the cap only cost about 1 ms more on average here. It's still worth keeping a cap, because physics cost grows with the number of moving pieces and a slower CPU will hit the limit first.
- **Total debris cap: 3000** (the default). Total pieces mostly cost draw time, which stayed small here.
- **Chunks turned into debris per frame: 12** (the default). It keeps the full-collapse hitch to one or two frames.
- **Re-run the benchmark on the minimum-spec PC** once one is chosen. These numbers come from a high-end Ryzen 9 and RTX 5070, so the cap may need to come down there.

## Limits of this measurement

- Each run uses one building (119 chunks). The multiplier stands in for more destruction, but it doesn't cover the extra static-collider rebuilds that several buildings would add.
- The camera sat at the spawn point, roughly 60 to 200 m from the building, so GPU cost for close-up debris isn't covered.
- Network snapshot size wasn't measured. Break state is 4 × 32-bit masks plus a generation counter and an impact velocity, delta-compressed. It only changes on ticks when something breaks.

## Milestone 3A: transformation + collapse (measured 2026-11-10, same machine)

The benchmark ran with `-tolive-ritual` in the Windows development build at 1600×900. The camera stays on the player.

1. The player stands 3 m from the breakable building with a full surge meter and holds the ritual input.
2. The 3 s build-up runs: cracks, push, lifted rubble, rings and the colour wash.
3. At the eruption, the stress command collapses the whole building, so the eruption, the fling of lifted rubble and a full collapse all land on the same frames.
4. Frames are recorded for 15 s from the start of the ritual.

| liveCap | multiplier | peak live | total pieces | avg ms | avg fps | first-3 s median ms | p99 ms | worst frame ms |
|---|---|---|---|---|---|---|---|---|
| 1000 | 1 | 723 | 728 | 1.66 | 601 | 1.39 | 4.10 | 14.9 |
| unlimited | 1 | 721 | 728 | 1.82 | 550 | 1.40 | 4.41 | 15.8 |
| 1000 | 4 | 1000 | 2912 | 2.19 | 457 | 1.62 | 5.62 | 15.8 |
| unlimited | 4 | 2905 | 2912 | 2.74 | 365 | 2.81 | 6.51 | 25.5 |
| 1000 | 8 | 1000 | 5824 | 2.08 | 480 | 1.56 | 4.79 | 16.7 |
| unlimited | 8 | 5814 | 5824 | 3.11 | 322 | 1.76 | 9.70 | 17.2 |

What the numbers show:

- The ritual's effects cost very little next to the destruction. The halo, light, post-processing push, rubble, rings and glass bursts together measured 1.6–1.7 ms idle and about 1.4 ms median during the build-up.
- The worst single frame is still the collapse spawn: 15–17 ms with the cap at 1000, up to 25 ms without a cap. Every average and p99 is far inside 16.7 ms.
- **The recommendation stays a live cap of 1000.** It keeps the worst frame at about 16 ms even with 8 times the debris.
- These runs are faster overall than the Milestone 2 numbers because the camera faces the building from close range, so far less of the city is drawn.

# Response force and rival (Milestone 3B)

Measured on the same machine on 2026-11-11, Windows **development build**, 1600×900, debris live cap 1000.

The benchmark adds `-tolive-units N`. It hosts and spawns N response units close by: 1 anti-god weapon, 30% gunships, the rest police drones. The units all fight the player, and the AI rival is present (solo). After 10 s of fighting it measures 5 s ("fight"), then destroys the whole building and records 15 s with everything still fighting.

```bash
Builds/Windows/ToLive.exe -tolive-bench -tolive-cap 1000 -tolive-units 10 -tolive-out bench-units.csv -screen-fullscreen 0 -screen-width 1600 -screen-height 900
```

In the Editor: `DevProbe.StartBenchmark(1000, 1, false, 10)`.

| units requested | peak alive | fight avg ms | collapse avg ms | collapse fps | first-3 s median ms | p99 ms | worst frame ms |
|---|---|---|---|---|---|---|---|
| 0 (rival only) | 0 | 3.84 | 3.93 | 254 | 3.55 | 10.43 | 21.0 |
| 6 | 8 | 3.33 | 3.84 | 261 | 3.78 | 10.38 | 47.1 |
| 10 | 10 | 3.35 | 3.56 | 281 | 3.13 | 9.48 | 20.8 |
| 16 | 16 | 3.78 | 3.99 | 250 | 3.61 | 10.62 | 23.6 |
| 24 | 24 | 3.76 | 3.16 | 317 | 4.17 | 7.28 | 34.5 |
| 32 | 32 | 3.40 | 3.49 | 287 | 2.77 | 7.78 | 20.6 |

(The 6-unit run peaked at 8 because the rival's own destruction raised heat and a police wave arrived.)

**Bandwidth** (Netcode's `GhostMetricsMonitor` snapshot metrics, received by a client in the Editor, `DevProbe.StartNetSample`):

| Scene | avg snapshot | worst snapshot |
|---|---|---|
| 2 players + rival, no units | 64 B | 190 B |
| 10 units + rival + 2 players, rockets flying | 185 B (≈11 KB/s at 60 snapshots/s) | 509 B |
| 32 units + rival + 1 player | 469 B (≈28 KB/s) | 1,520 B |

At 10 units, police drones were the largest share (~75 B) because they strafe constantly, then gunships (~35 B) and rockets (~26 B). The anti-god weapon (~14 B) and the opposition state (~5 B) are small.

**What the numbers say, and the chosen caps.**

- **Frame time isn't the limit.** From 0 to 32 units, the frame cost stays within run-to-run noise (3–4 ms average). The worst frames come from the collapse, not the units. Unit AI, hit checks and rockets are cheap at this scale.
- **Bandwidth and readability are the limits.** At 32 units the worst snapshot is larger than one packet (about 1,400 B), and the sky is too busy to read the telegraphs.
- **Defaults:** police 6, gunships 3, anti-god 1, **total 10**. That keeps snapshots around 185 B (worst ~510 B), so there is room for the crowds of milestone 4. The measured headroom allows raising the total to about 16–20 if fights feel thin.
- **Projectiles:** tracers cost nothing extra (they are a few fields on the unit's ghost). Rockets are cheap ghosts because only a handful are alive at once.
