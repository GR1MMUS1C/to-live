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

## Milestone 3B: opposition (heat, the response force, the rival)

**Intent.** Tyrants mostly fight the army, protectors mostly fight disasters (milestone 4), and both fight the rival. Grey-box shapes; silhouettes and telegraphs over looks.

**Heat** (per district, 0 to 3, server-decided, replicated on one small "Opposition State" ghost, shown as pips on the HUD).

- `HeatSource` / `HeatEvent` is the single, clearly named place where heat rises, like the surge fuel source. Today's producers: chunks broken (blamed on whoever is responsible: a thrown unit is the thrower's fault), chunks that fall in the structural collapse that break caused (`HeatPerCollapsedChunk`, 0.5 vs 1.5 for a direct break, so dropping the whole tower in one hit reaches level 2, not straight to 3), damage dealt to response units, ritual eruptions. The F9 stress destroy and the army's own rockets raise no heat. Milestone 4's dominance replaces the producers.
- Heat decays after a quiet period. Levels are thresholds on the raw value.
- Each god (player or rival) has a server-only **blame** that rises with the heat it causes and fades after about 20 s. The army targets blamed gods first, and only leaves while nobody is blamed.

**The response force.** Server-simulated, interpolated ghosts with simple, readable AI: spawn at the district edge, approach, hold a preferred range and height, strafe, fire on a cadence, retreat when heat drops (and nobody is blamed), fall as a wreck when destroyed.

| Level | Unit | Weapon |
|---|---|---|
| 1 | Police drone (flat quad-rotor, blue light bar) | Hitscan tracers decided on the server; weak damage, tiny flinch |
| 2 | Gunship (fuselage, wings, rotor, aiming pods) | Slow, visible rockets (40 m/s, slight lead): knockback launch, blast damage, breaks chunks |
| 3 | Anti-god weapon (big core, ring, long barrel) | Telegraphed beam: 2.5 s charge with a visible aim line that tracks with lag, locks 0.6 s before firing, then a 0.5 s beam that drains a lot of power, launches and suppresses surge gain for 6 s. Buildings block it |

- **Units are characters.** They carry the same flight + combat components as the dummy, so every existing move works on them: strike, launch, finisher, beam, grab, throw, chunk smashing. All damage goes through `Damage.Apply`. A launched body hitting another body (`BodyImpactSystem`) damages both and launches the one it hit.
- **Movement.** The unit's brain writes a desired velocity (`UnitDrive`); the shared flight code steers to it (like an AIController feeding CharacterMovement).
- **Spawning** is wave based and budgeted: per-type caps and a total cap (`Opposition Config`). The defaults come from the measured benchmark (see [PERF.md](PERF.md)).
- **Wrecks** are cosmetic debris pieces under the existing debris caps.

**Projectile netcode (why).**

- **Tracers: not ghosts.** The server decides hit or miss instantly and writes the last shot (tick, end point, hit) into the unit's ghost state; clients draw the tracer when that changes. Many short-lived bullets as ghosts would cost a spawn + despawn in snapshots each; this costs a few bytes on a ghost that is replicated anyway.
- **Rockets: interpolated ghosts, moved and detonated by the server.** They are few (a handful alive), slow and must be visible and dodgeable, and late joiners must see the ones in flight, which is exactly what ghosts give. Predicted spawning (the Netcode docs' recommendation for projectiles) is for **player-fired** projectiles to hide the client's round trip; these are fired by server AI, so there is nothing to predict. Clients draw the explosion when a rocket ghost despawns (the docs note despawns are sent immediately).
- **Anti-god beam: state on the weapon's ghost** (phase, phase end tick, aim point), so every client draws the same telegraph from replicated data.

**The rival ("VANTA").** An AI-controlled god using the **same player prefab and the same systems**. Its server-side brain decides after each simulation tick and writes a `PlayerInput` into the character's Netcode input buffer for the next tick, exactly where a client's input would land, so it can't do anything a player can't and it syncs like a player (owner-predicted prefab with no owner, so interpolated on every client).

- Small state machine with tunable weights: approach, circle at range, dash in for chains, reactive guard (reaction delay + success chance, never frame-perfect), punish with a heavy after a successful guard, chase a launched target for a finisher, beam at range, grab a staggered target and throw it, disengage when low, ritual when the meter is full (more likely when losing). Finishers and throws aim through the breakable building when it's roughly behind the target.
- Three difficulty presets (easy, normal, hard) on `Rival Config`.
- Its own alignment: dominance by default, the opposite of the first human's when that leans dominance. Deep violet body.
- Present when exactly one human is connected, absent with two; the Insert key overrides. Knocked out, it returns after 12 s and the HUD announces it.
- Three-way fights emerge: the rival raises heat like anyone, the army shoots blamed gods, and the rival fights units that shoot at it.

**HUD.** Heat pips + level name + progress, rival name plate (when locked on or within 60 m), incoming-fire warning with edge arrow for the anti-god beam, SURGE SUPPRESSED, small health bars over recently hit units. UI Toolkit only.

## Milestone 4A: crowds, the district meter, real fuel, recolouring

**Intent.** Power is borrowed from people. Two fuels, trust and dominance (fear), one visible meter per district, no global good/evil score. Both players draw from the same population, so belief and fear are scarce. Gate: a 15-minute session tells a different story depending on how you played.

**Citizens (a few hundred, server decided, compact on the wire).**

- The server simulates every citizen as plain data (a buffer on a server singleton, not entities, not ghosts): position along their street, mood, alive.
- **What players must agree on is replicated, compactly**, on one district ghost:
  - an alive bit per citizen;
  - a 2-bit mood per citizen (wander, gather, flee, cower);
  - the last 4 violence points;
  - each god's trust and fear;
  - the meter, the leader and population;
  - the cause feed.
- **Bodies** are static-optimised ghosts (sent once, then free; late joiners get them).
- **Homes** are baked into the subscene, so they cost no bandwidth.
- **Clients run look-alike local agents** from that state: they walk, gather and cheer (little jumps), flee along their street away from the latest violence, or cower. Their exact positions are cosmetic and only roughly match the server's; a death puts the body where the server says.
- **Why:** one ghost per citizen was measured at about 880 bytes per snapshot for 320 citizens (worst 1,891 B, over one packet). The compact state costs about 8 bytes per snapshot. See [PERF.md](PERF.md).

**Behaviour.**

- **Wander** along the street near home.
- **Gather and cheer** within 45 m of a god they trust.
- **Flee** (and **cower** when very close) from violence: hits, gunfire, rockets, beams, eruptions, fast bodies, supersonic passes. They also flee from a god they fear.
- **Dead:** the body stays, with a blood pool. No dismemberment.

**Deaths, all server decided, blamed with the same Responsible rules as heat** (a thrown or launched body is its launcher's fault):

- falling chunks (direct breaks and structural collapse);
- launched or fast bodies at street level;
- player and rival beams;
- eruptions (inner half of the radius);
- rockets;
- very low supersonic passes;
- the anti-god beam.

They all go through one queue, `CrowdHazards` / `CrowdHazard`.

**District source** (`DistrictSource` / `DistrictEvent`, the one place trust and fear change, rates on **District Config**):

| Source | Effect |
|---|---|
| Staying near a crowd without harming it (not boosting) | +trust per second |
| Destroying an army unit that threatened people | +trust |
| Knocking out the rival while people watch | +trust |
| Civilian deaths you caused | +fear, −trust, plus heat and a short fear-fuel spike |
| Destruction (chunks) | +fear, a little −trust |
| An eruption near a crowd | +fear, −trust |
| A reckless supersonic pass over people | −trust, a little fear |

- Standing fades slowly (half-life about 2 minutes).
- The **meter** is total fear against total trust. The **leader** is the god with the most standing; **hold** is how strongly they hold the district.
- Each god's **alignment** (the look of their power) now follows their own trust and fear. The [ ] keys add debug trust or fear.

**Fuel.** `SurgeFuel` is unchanged as an interface. Its producers are now the crowd:

- Every half second, each god gets fuel from the living citizens watching within 70 m.
- An awe share is paid even with no standing; trust plus fear raises it to full.
- Killing people pays a short fear spike, but dead citizens never pay again. So slaughter is a short-term gain and a long-term loss.
- The milestone 3 placeholder producers (damage, chunks) are off while **Crowd Fuel** is on. F4 stays as debug.

**Heat follows dominance.**

- Every god-blamed heat event is scaled by how much the district fears rather than trusts that god (a fully trusted god: `TrustedHeatScale`, default 0.15).
- Civilian deaths add heat.
- A trusted god who only fights the rival draws little or no army.

**Recolouring.** The base world stays muted. A glowing crown around the top of every building, the lamps at every intersection and a light sky tint take the leader's colour: the calm cyan family for trust, crimson for dominance. Brightness follows how strongly the district is held. It's done with per-instance colour overrides on one unlit material asset and a runtime volume, never `Shader.Find`.

**HUD.**

- The district meter at the bottom: trust on the left, dominance on the right, with a marker.
- Who holds the district and how, and the number of people alive.
- Your trust and fear.
- A short cause feed above the meter ("+trust: protected the crowd", "+fear: 12 civilians killed").

**Hooks for 4B.**

- Crises: new `DistrictSource` producers and new `CrowdHazard` kinds.
- Rebuilding and scarring: read `DistrictState` hold and alignment.
- Contesting: standings are already per god.
- "Drained and expelled": replaces the respawn in `CombatServerSystem` and `RivalSystem`.

## Milestone 4B: crises, ownership, rebuild and scar, defeat

**Core loop.** A crisis breaks out. You fly in and end it either by protecting people or by force. The district's trust or fear shifts, and its crowd reacts. Holding the district grows your power (tier progress). The district changes, and so does who shows up next. Another player can arrive at any point.

**The crisis director** (server, `CrisisSystem`, numbers on **Crisis Config**):
- One crisis at a time, the first about 25 s in, then about 45 s after each one ends.
- The crisis is replicated compactly on the district ghost (`CrisisState`: kind, marker, timer, progress, failing chunk, outcome), so a late joiner sees it live.
- Clients draw a beacon column visible from the air, a red ring around a failing chunk, a HUD banner with the objective and **both ways to resolve it**, a screen marker with distance, and an outcome banner.
- SFX hooks: `CombatFx.OnCrisisStart` and `OnCrisisEnd`.

| Crisis | Protect | Force | Ignored |
|---|---|---|---|
| **Disaster: the failing tower** (only a god can stop it) | Chunks of the breakable tower start to fail one at a time (red ring, 2.5 s warning). Hover next to the failing chunk and **guard** to hold it up while people evacuate. Hold 6. | Bring the tower down yourself: break 12 chunks (cutting the base collapses the rest) | The tower comes down on the street |
| **Attack** | Units spawn and shoot into a crowd (or, in a trust-held district, VANTA attacks it). Destroy them with little collateral (≤5 deaths + chunks/5 that you cause). | Destroy them while killing people or wrecking the street | The attackers leave; the holder loses standing |
| **Uprising** | People near the most feared god rise up. They defy it (its fear drains every second) and don't flee. Stay near them and harm no one for 12 s. | Kill 8 rioters: a massacre, lasting fear, bodies | The target loses fear |

**Disaster design, two candidates.**
- **(a) Failing tower (chosen).** It reuses the breakable building, structural collapse, guard, flight and the crowd's evacuation. Both paths read clearly: hold it up, or bring it down.
- **(b) Energy breach (rejected).** A growing rift you contain with the beam. It would need a new damage-field system, and it uses the building and the crowd less.

**Outcomes.**
- Outcomes go through `DistrictSource`: +30 trust when protected; +35 fear and a little −trust when ended by force; the holder loses standing when a crisis fails.
- The cause feed names the cause.
- Who comes next follows ownership:
  - **dominance-held:** uprisings and attacks; heat never drops below police; bigger waves;
  - **trust-held:** disasters, and VANTA as the attacker.

**VANTA in crises.** It takes the opposite approach to the leading player:
- **When the leader is trusted or nobody leads,** it goes for force: it flies to the crisis and beams it, aiming at the tower's base during a disaster.
- **When the leader is feared,** it protects: it holds failing chunks up (guarding after a reaction delay) and fights the attackers.
- A human nearby takes priority, so there's always a contest.

**Ownership.** After a crisis or a defeat, a god whose trust or fear is at least 30, and 5 more than any other god's, **holds** the district through trust or dominance.
- The HUD shows the holder, and the city takes the holder's colour at full strength.
- A holder whose standing fades below half the threshold loses it.
- Holding feeds tier progress (lifetime fuel per second): tiers come from district control.

**Rebuild and scar** (server-decided, through the existing broken mask and body ghosts):
- **Trust-held:**
  - the breakable tower rebuilds one supported chunk every 2 s;
  - bodies are cleared after 30 s, then people return home one every 2 s;
  - towers come down.
- **Dominance-held:**
  - damage and bodies stay;
  - one **fortification tower** is raised every 15 s (up to 8; grey-box shaft and barricade with a band in the holder's colour);
  - *What towers do:* they shoot any other god who comes within 70 m of them (light damage, a visible shot), so the holder's ground pushes back on challengers.

**Defeat: drained and expelled** (replaces knock-out and respawn for players and VANTA; the dummy and units keep the old rule):
- The loser loses 40% of their trust and fear in the district; the winner takes half of that.
- The loser loses their surge meter (a surge ends in burnout).
- After the knock-out the loser comes back **at the district edge** and is locked out for 15 s: pushed back if they fly in, unable to gain standing. The army also stops chasing them.
- The HUD says so.

**Contesting.** Standing is per god, the population and its fuel are shared and scarce, and the recolouring follows the holder. So a second player can flip a held district through the crisis loop, or by beating the holder there (verified with two players in one process).

**Two scripted 15-minute sessions** (2026-11-11, same length, same district, VANTA present, the crisis director left to run):

| End state | Protector | Tyrant |
|---|---|---|
| Crises | 12; most protected; VANTA ended 2 disasters by force | 14; all ended by force |
| Holder | the player, through trust | the player, through dominance |
| Meter | −0.87 | +0.87 |
| Trust / fear | 151 / 3 | 0 / 70 |
| People alive | 640 / 640 | 312 / 640 (328 bodies) |
| The tower | rebuilt 119 chunks over the session | scarred |
| Fortification towers | 0 | 8 |
| Heat / army | 0 / none | level 3 / 11 units |
| Tier | 2 | 2 |
| Defeats | none | repeatedly drained and expelled by the army |

Screenshots: `docs/screenshots/m4b-*.png`.

**4A fixes.**
- **Bodies:** lighter figures and an unlit, brighter, bigger blood pool, readable in building shadow.
- **City colour:** accents use the colour's hue at a capped intensity, so trust reads cyan instead of blooming to white.
- **Crowds:** people within 80% of the eruption radius freeze while a ritual builds up (they cower instead of outrunning it). Beams and collapses kill as before.


| What | Where |
|---|---|
| Flight tuning (all numbers) | `Flight Config` object in the District subscene (`FlightConfigAuthoring`) |
| Combat tuning | `Combat Config` in the District subscene (`CombatConfigAuthoring`) |
| Debris caps / feel | `Debris Config` in the District subscene (`DebrisConfigAuthoring`) |
| Breakable building generator | `Assets/_Project/Editor/BreakableBuildingGenerator.cs` |
| Transformation / beam / grab tuning | `Power Config` in the District subscene (`PowerConfigAuthoring`) |
| Power look (colours, halo, wash) | `Power Look` in `Main.unity` (`PowerLook`) |
| Post-processing | `Assets/_Project/Rendering/BaseLook.asset`, `PowerSurge.asset` |
| Heat and response force tuning | `Opposition Config` in the District subscene (`OppositionConfigAuthoring`) |
| Crowd, trust/fear sources, crowd fuel | `District Config` in the District subscene (`DistrictConfigAuthoring`) |
| Rival tuning (difficulty presets) | `Rival Config` in the District subscene (`RivalConfigAuthoring`) |
| Camera tuning | `Game Systems` object in `Main.unity` (`FlightCameraRig`) |
| Input bindings | `Assets/_Project/Input/ToLive.inputactions` |
| Code | `Assets/_Project/Scripts/` (Net, Player, Flight, Input, CameraRig, Combat, Destruction, Power, Opposition, Rival, District, UI) |
| Editor generators | `Assets/_Project/Editor/ToLiveSetup.cs` (menu **Tools > To Live**) |

## Key technical decisions

- **Kinematic sphere sweeps instead of a predicted rigidbody for heroes.**
  - Prediction stays stable: nothing depends on solver contacts or impulses that could diverge between client and server.
  - Re-predicting rolled-back ticks only re-runs our own movement code, not a full physics step per tick.
  - Sweeps can't tunnel through walls at 400 m/s.
  - Netcode's physics group is set to run whenever colliders exist (`NetCodePhysicsConfig`: *LagCompensationEnabledOrAnyPhysicsEntities*), so the static collision world is built on both the server and the client.
- **The camera's look direction is part of the networked input.** Movement "relative to the camera" is then computed identically on the server and the client.
- **Custom bootstrap.** The game starts in a plain local world (the menu). Host or Join creates the client and server worlds, then loads the District subscene into them.
