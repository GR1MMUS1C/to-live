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

## Milestone 3A: transformation, beam, grab and throw

**Gate:** "the transformation moment gives you chills."

**Power state.** Each character has a replicated `PowerState`:

- **tier**: 1, or 2 (a permanent baseline multiplier);
- **alignment**: from -1 (trust) to +1 (dominance); it drives the look;
- **surge meter**;
- **surge phase**: Ritual → Surge → Burnout;
- **beam energy**.

It is predicted for its owner (so starting the ritual or the beam feels instant) and authoritative on the server. The timers are tick numbers, so re-prediction always lands on the same result.

**Fuel interface.** `SurgeFuel` / `SurgeFuelEvent` is the single, clearly named place where the meter gets filled. Today the producers are damage dealt, damage taken and chunks broken. A chunk smashed by a launched body is credited to whoever launched it. **Milestone 4 replaces the producers with the crowd** (trust / fear) without touching the surge code. Tier 2 unlocks when lifetime fuel crosses a threshold.

**The ritual.**

1. A deliberate input: hold L3 + R3, or R.
2. A 3 s build-up. You are locked in place and take 15% damage, and knockback can't interrupt you. Meanwhile the server breaks chunks in a growing radius and pushes characters back.
3. The **eruption**, server-side on the first surge tick: damage and knockback in a radius, plus chunks broken in a larger radius.
4. About 20 s of **surge**: speed ×1.6, damage ×2, knockback ×1.8, beam ×2, damage taken ÷1.5. The meter drains as it runs.
5. 6 s of **burnout**: speed ×0.7 and damage ×0.6, and you can't re-surge.

Every number is on **Power Config**.

**The look: "fractured halo".**

- Procedural, hard-edged shard meshes in an unlit HDR colour, so they bloom. No outline or toon shading.
- **Trust:** two level, symmetrical rings that orbit slowly with a steady glow, in cyan.
- **Dominance:** an uneven, tilted swarm of outward-pointing shards with a fast, unstable orbit, flicker and occasional snaps outward, in crimson.
- The two blend by alignment.
- Tier 2 shows a faint, small halo permanently. The ritual grows it to the full halo, and the surge keeps it.

**Art rule.**

- A global **Base Look** volume keeps the world muted: ACES tonemapping, saturation -35.
- During a ritual or surge, a **Power Volume** fades in for the local view, whether it's your own ritual or one happening within about 80 m. It adds a colour push and a stronger bloom.
- At the same time, a point light in the energy colour washes over the surroundings.
- Power is the only saturated colour.

**Ritual world reactions (cosmetic, per client).** Rubble tears up from the ground. Settled debris near the caster lifts and hovers, then is flung outward at the eruption. Alongside that: pressure rings, a crater disc, glass bursts on the faces of nearby buildings (found by raycast), an eruption flash and camera kick, and the ritual camera move. There are also SFX hooks: `CombatFx.OnRitual(progress)` and `OnEruption`.

**Beam.** A sustained sphere sweep along the aim direction, run on the server. Chunks along it break until it reaches the first unbreakable surface. Characters near the line take damage over time and are pushed. The beam draws on a regenerating energy bar.

**Grab and throw.**

- Only a staggered or launched target can be grabbed.
- Holder side: predicted.
- Victim side: the server holds the victim in front of the holder.
- The throw launches the victim along the holder's aim, and it smashes chunks like any launch.
- The victim can mash light or dash to break free after 0.8 s.

**Build note.** Player builds strip shaders that no asset references. All runtime-created effect materials therefore come from material assets on the **FX Materials** scene object (`FxMaterials` / `FxMaterialSource`), not from `Shader.Find`. Milestone 2's impact rings and trails were affected by this in builds too, and are now fixed.

## Where things live

| What | Where |
|---|---|
| Flight tuning (all numbers) | `Flight Config` object in the District subscene (`FlightConfigAuthoring`) |
| Combat tuning | `Combat Config` in the District subscene (`CombatConfigAuthoring`) |
| Debris caps / feel | `Debris Config` in the District subscene (`DebrisConfigAuthoring`) |
| Breakable building generator | `Assets/_Project/Editor/BreakableBuildingGenerator.cs` |
| Transformation / beam / grab tuning | `Power Config` in the District subscene (`PowerConfigAuthoring`) |
| Power look (colours, halo, wash) | `Power Look` in `Main.unity` (`PowerLook`) |
| Post-processing | `Assets/_Project/Rendering/BaseLook.asset`, `PowerSurge.asset` |
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
