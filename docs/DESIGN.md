# To Live — design summary

> The design source of truth is the design doc artifact:
> https://claude.ai/artifact/R9KTCMBJkDpJguvwksWfaN
> This file is a short summary for working in the repo. If the two disagree, the artifact wins.

## The game

**To Live** is a third-person action game for **2 players** on PC, designed for a **gamepad first**.

- You play a godlike being whose power comes from people. You get it in one of two ways:
  - **Trust**: people believe in you.
  - **Dominance**: people fear you.
- **Crises** break out in city districts, and how they are resolved decides who owns each district.
- Planned for later milestones:
  - a fully destructible city, pre-fractured
  - impact-heavy combat
  - transformations

## Architecture: hybrid ECS + GameObjects

| Layer | Tech | Why |
|---|---|---|
| Heroes and gameplay-critical physics | **ECS** (Entities) + **Netcode for Entities** (server-authoritative, client prediction) + **Unity Physics** | Deterministic, predicted, and scalable to a destructible city |
| City, UI, art | Authored as **GameObjects**; city baked into ECS through **subscenes** | Normal Unity authoring workflow |
| Camera, menus, HUD | GameObjects (Cinemachine, UI Toolkit), client-only | Never networked |

Art stays grey-box until **milestone 5**.

## Milestone 1: flight and camera, networked from day one

**Gate:** flying feels great on a gamepad, and both players see each other move smoothly.

The milestone covers:

- **Netcode bootstrap:** Host (server and client in one process) or Join by IP and port (default port 7979).
  The menu is built with UI Toolkit, and the HUD shows gear, speed and ping.
- **Player ghost:** owner-predicted for its own player and interpolated for everyone else.
  Each player is a capsule in their own colour, with a yellow front marker.
- **Three flight gears**, simulated in the prediction loop:
  - **Hover:** the default gear, 15 m/s, moving relative to the camera.
  - **Cruise:** hold thrust, up to 80 m/s, with a limited turn rate and banking into turns.
  - **Boost:** hold boost while cruising, up to 400 m/s, with a "crossed supersonic" flag at 343 m/s.
- **Collision:** a kinematic sphere is swept against the static city ("move and slide") using Unity Physics. There is no damage.
- **Camera:** Cinemachine third-person orbit. The distance (6 → 14 m) and field of view (60 → 85°) grow with speed, and a collision check keeps it from clipping into buildings.
- **Grey-box district:** a 600 × 600 m ground with a 5 × 5 grid of blocks and 20 m streets. Buildings are 20–200 m tall.
  It is generated from the menu **Tools > To Live > Generate Grey-box District**.

Out of scope for M1: combat, destruction, crowds, cloud services, relay.

## Milestone 2: impact combat and one fully breakable building

**Gate:** punching someone through the building is fun, stays in sync for two players, and holds frame rate. Debris budgets are measured (see [PERF.md](PERF.md)).

**Combat.** Short, impact-driven chains where the buildings do the damage:

- Soft lock-on.
- A 3-hit light chain whose 3rd hit launches the target.
- A charged heavy strike.
- Dash, used to chase a launched target. A strike on a launched target is a **finisher** that drives it where the camera aims.
- Guard: from the front it soaks most damage and prevents launching.
- Power (health) with a HUD bar. At zero you are knocked out and respawn after a few seconds.
- A launched body, or a player boosting above 150 m/s, smashes through building chunks: it loses some speed and takes damage.
- A server-simulated training dummy for solo testing.
- Hit feedback: hit-stop, camera shake, a white flash, knockback trails and impact rings, plus SFX hooks (`CombatFx.OnImpact`).
- All combat numbers are on **Combat Config**.

**Netcode for hits.**

- The combat state machine (`CombatSystem`) runs in the prediction loop. The attacker's own client predicts its moves, homing, hit-stop and hit effects instantly.
- **Only the server applies hits.** It checks the strike sphere against each target's position rewound to what the attacker saw. It does this with a small per-tick position history and the attacker's `CommandDataInterpolationDelay`, which is the documented server-side rewind approach, done with distance checks instead of physics colliders.
- Damage and knockback are written into the victim's predicted ghost state (`CombatState`, `FlightState`). The victim's client gets them in the next snapshot and re-predicts from there.
- Knockback runs in the same predicted flight code, so there is no rubber-banding beyond the unavoidable one-RTT delay before the victim learns it was hit.

**Breakable building (our own pre-fracture pipeline).**

- `Tools > To Live > Make Selected Building Breakable` (also run by the district generator) cuts a box building into about 120 **layer-1** chunks:
  - storey bands of random height
  - wall panels cut at jittered positions, sometimes split again vertically
  - floor slab rings and a central core
- Each chunk has 4 to 8 **layer-2** pieces, used as debris.
- Intact chunks are static colliders in the main (predicted) physics world.
- The **server alone** decides breaks and structural collapse: a flood-fill from grounded chunks, where unsupported chunks fall.
- Only a **128-bit broken mask** (plus a generation counter and the last impact velocity) is replicated, on a pre-spawned ghost. That's compact, delta-compressed, and correct for late joiners.
- Each machine applies the mask by removing the chunk's collider and hiding it. The client applies it when the snapshot arrives, so about one RTT after the server.
- **Debris is cosmetic:** it is simulated per client in a client-only physics world (index 1, sharing static colliders), as the Netcode docs recommend.
  - Settled pieces drop their physics body.
  - Live and total caps apply, and spawning is spread over frames.

## Where things live

| What | Where |
|---|---|
| Flight tuning (all numbers) | `Flight Config` object in the District subscene (`FlightConfigAuthoring`) |
| Combat tuning | `Combat Config` in the District subscene (`CombatConfigAuthoring`) |
| Debris caps / feel | `Debris Config` in the District subscene (`DebrisConfigAuthoring`) |
| Breakable building generator | `Assets/_Project/Editor/BreakableBuildingGenerator.cs` |
| Camera tuning | `Game Systems` object in `Main.unity` (`FlightCameraRig`) |
| Input bindings | `Assets/_Project/Input/ToLive.inputactions` |
| Code | `Assets/_Project/Scripts/` (Net, Player, Flight, Input, CameraRig, UI) |
| Editor generators | `Assets/_Project/Editor/ToLiveSetup.cs` (menu **Tools > To Live**) |

## Key technical decisions

- **Kinematic sphere sweeps instead of a predicted rigidbody for heroes.**
  - Prediction stays stable: nothing depends on solver contacts or impulses that could diverge between client and server.
  - Re-predicting rolled-back ticks only re-runs our own movement code, not a full physics step per tick.
  - Sweeps can't tunnel through walls at 400 m/s.
  - Netcode's physics group is set to run whenever colliders exist (`NetCodePhysicsConfig`: *LagCompensationEnabledOrAnyPhysicsEntities*), so the static collision world is built on both the server and the client.
- **The camera's look direction is part of the networked input.** Movement "relative to the camera" is then computed identically on the server and the client.
- **Custom bootstrap.** The game starts in a plain local world (the menu). Host or Join creates the client and server worlds, then loads the District subscene into them.
