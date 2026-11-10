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

## Where things live

| What | Where |
|---|---|
| Flight tuning (all numbers) | `Flight Config` object in the District subscene (`FlightConfigAuthoring`) |
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
