# Play-testing To Live (Milestone 1)

This is written for someone who knows Unreal but is new to Unity.

## 0. One-time setup

1. Open the project in **Unity Hub** → **Add** → choose `Z:\Github\to-live`. Use editor **6000.6.8f1**.
2. Wait for the first import to finish (the progress bar at the bottom right).
3. In the **Project** window (the Unity equivalent of the Content Browser), open `Assets/_Project/Scenes/Main.unity` by double-clicking it.
   - If `Main.unity` is missing, use the top menu **Tools > To Live > Setup Project (rebuild everything)**.
   - That menu rebuilds the materials, the player prefab, the District subscene and the main scene.

## 1. Fly alone (host)

1. With `Main.unity` open, press **Play** (the ▶ button at the top centre, like PIE in Unreal).
2. In the menu, click **Host**. The game then:
   - starts a server and a client inside this Editor,
   - loads the district,
   - spawns your player above the city.
3. Click inside the **Game** view so it receives keyboard and mouse input. A connected gamepad works straight away.
4. Fly using the controls below.
5. Press **Esc** (or **Start** on the gamepad) to open the menu, where you can choose **Resume** or **Leave game**. Press Play again to stop.

### Controls

| Action | Gamepad | Keyboard / mouse |
|---|---|---|
| Move (hover) | Left stick | W A S D |
| Look / orbit camera | Right stick | Mouse |
| Thrust → Cruise (analog) | Right trigger | Left Shift |
| Ascend | A | Space |
| Descend | B | Left Ctrl |
| Boost (hold, while cruising) | Left stick click | F |
| Menu | Start | Esc |

### Combat controls (Milestone 2)

| Action | Gamepad | Keyboard / mouse |
|---|---|---|
| Light strike (press 3 times for the chain; the 3rd hit launches) | X | Left mouse button |
| Heavy strike (hold to charge, release to hit) | Y | Right mouse button |
| Dash (toward the lock target, or the stick direction) | RB | Left Alt |
| Guard (hold) | LB | Q |
| Lock-on (toggle) | Right stick click | Tab |

### Transformation controls (Milestone 3A)

| Action | Gamepad | Keyboard / mouse |
|---|---|---|
| Beam (hold: a short charge, then it fires while held, draining energy) | LT | E |
| Grab a staggered or launched target, then press again to throw where the camera aims | D-pad up | G |
| Transformation ritual (when SURGE READY, hold about 0.4 s) | Hold BOTH stick clicks (L3 + R3) | Hold R |

Pressing R3 for the ritual also toggles lock-on, and L3 is boost. That's harmless during the ritual, because you're locked in place.

### Debug keys

| Key | What it does |
|---|---|
| F3 | Show or hide the performance overlay: FPS, frame time, ping, live and total debris, broken chunks |
| F5 | Reset the training dummy and rebuild the building (also in the Esc menu) |
| F6 | Toggle the training dummy holding guard (for testing guard) |
| F9 | Stress test: destroy the whole building at once (also in the Esc menu) |
| F4 | Fill your surge meter (also ends surge or burnout, for quick retests) |
| F7 | Toggle your tier (1 or 2) |
| [ and ] | Slide your alignment toward trust or toward dominance (0.25 per press) |
| F8 | The training dummy performs the ritual, using your alignment and tier, so you can watch from outside |
| Page Up / Page Down | Heat up or down one whole level (debug heat is blamed on you, so the army comes for you) |
| Home | Spawn one of each response unit (police drone, gunship, anti-god weapon); they stay even at heat 0 |
| End | Remove every response unit and rocket |
| Insert | Rival on/off (overrides the "one human = rival" rule until you press it again) |
| Delete | Cycle the rival's difficulty: easy, normal, hard (shown on its name plate) |

### Gears

- **Hover** (default): moves relative to the camera at up to 15 m/s and stops precisely when you let go.
- **Cruise**: hold thrust. You fly where the camera points, accelerate to 80 m/s and bank into turns.
- **Boost**: while cruising, also hold boost to accelerate to 400 m/s. Turning is much slower.
  - Passing **343 m/s** (the speed of sound) logs "Crossed supersonic" in the Console and shows a white placeholder ring.
- You can't pass through ordinary buildings or the ground; you slide along them.
- The **breakable building** near spawn 1 is the exception. A launched body, or a player boosting faster than 150 m/s, smashes through its chunks. They lose some speed and take a little damage.

The bottom-left overlay shows the current **gear**, **speed** (m/s) and **ping**.

## 1b. Five-minute combat and destruction test (Milestone 2)

Use a gamepad if you have one; the keyboard equivalents are in the tables above.

1. **Get there.** Press Play, click **Host**, then press **F3** to see the overlay.
   - Below you to the south-east there's a slightly darker, tiled building: the **breakable building**.
   - A **red capsule**, the training dummy, hovers in front of it.
   - Dive down (point the camera down and hold the right trigger) and stop about 10 m from the dummy, on the side facing away from the building.
2. **Lock on.** Click the right stick (Tab). A red diamond marks the dummy, and its power bar appears at the top. The camera swings to keep both of you in view.
3. **Light chain.** Press X three times in rhythm. You home in, and hits 1 and 2 stagger the dummy. Hit 3 **launches** it up and away toward the building, leaving a white trail.
4. **Finisher.** Straight after the launch, press **RB** to dash after it, then **X**. The dummy is driven hard in the direction your camera points.
   - Aim at the building or the ground.
   - Chunks break where it hits, debris falls and settles, and the overlay's "Broken chunks" count rises.
5. **Heavy.** Press F5 to reset. Hold **Y** for about a second (the HUD shows CHARGING), then release. You get a big knockback, which scales with the charge.
6. **Guard.** Press F6 so the dummy guards, then hit it from the front. It takes only a sliver of damage and can't be launched. Press F6 again to turn guard off.
7. **Knock-out.** Keep hitting the dummy until its power bar is empty. It turns grey, then respawns at home after about 4 seconds.
8. **Smash with your body.** Fly high, point at the building, then cruise and boost straight into it. Above 150 m/s you punch through, taking a little damage.
9. **Collapse.** Break the lowest ring of the building with launches or a boost dive. Anything left without support falls.
   - For the full stress case, press **F9**: the whole building comes down at once. Watch FPS on the overlay.
10. **Rebuild.** Press **F5**: the building and the dummy are restored.

To tune the combat numbers (damage, knockback, timing windows, hit-stop, break thresholds):

1. Open the District subscene: select **District (SubScene)** and tick **Open**.
2. Select **Combat Config**.

Debris settings are on **Debris Config**. The cap measurements behind its defaults are in [PERF.md](PERF.md).

## 1c. Transformation test (Milestone 3A, about 5 minutes)

1. **Find a spot.** Press Play, click **Host**, then fly down to the street just north of the breakable building, about 10–15 m from it, facing it.
2. **Fill the meter.** Fight the dummy, or break chunks with the beam (hold **LT**), until the SURGE bar is full and "SURGE READY" pulses.
   - Shortcut: press **F4**.
3. **The ritual.** Hold **L3 + R3** (or **R**). Over 3 seconds:
   - you lock in place;
   - the camera drops low and close off your shoulder, then slowly pulls out;
   - the halo shards gather around you;
   - rubble tears up from the ground and hovers, then rises higher;
   - pressure rings pulse faster and faster;
   - windows on nearby buildings blow out, and the building in front of you starts to crack;
   - your energy colour washes over the street.
4. **The eruption.** A hard flash and a shockwave. Everything lifted is flung outward, the building takes a big bite, and nearby characters are thrown back.
5. **The surge** lasts about 20 s. Fly (much faster) and hit the dummy: damage is doubled. Watch the SURGE bar drain.
6. **Burnout** comes next: the bar greys out and you're weaker for 6 s.
7. **Alignment.** Press **[** four times (full trust) or **]** four times (full dominance), then repeat steps 2–3.
   - Trust is calm cyan rings.
   - Dominance is jagged, flickering crimson shards that snap outward.
8. **Tier 2.** Press **F7**: a small, faint halo stays with you permanently.
9. **From outside.** Stand about 30 m from the dummy and press **F8**. The dummy performs the ritual, and you see its halo, the light and the eruption from a distance.
10. **Grab and throw.**
    1. Lock on (R3), then press X three times to launch the dummy.
    2. Dash (RB) after it, then press **D-pad up** to grab it.
    3. Turn the camera toward the building and press **D-pad up** again to throw it through the building.
11. **Beam.** Aim at the building and hold **LT**. Chunks along the beam break.

To tune:

- All transformation gameplay numbers are on **Power Config** (District subscene).
- The look is on **Power Look** in `Main.unity`: energy colours, halo size and speed, glow, wash and the ritual effects.
- The ritual camera settings are on **Flight Camera Rig**.

## 1d. Milestone 3 play script (about 10 minutes)

Do this solo (one human): the **rival** is only present when one human is connected.

**Part 1: transformation (2 minutes).** Follow 1c steps 1–6 once: fill the meter (F4 is fine), ritual, eruption, surge, burnout.

**Part 2: heat and the response force (4 minutes).** Heat is the pip row top right: CALM, POLICE, MILITARY, ANTI-GOD.

1. **Raise heat by destruction.** Smash through the breakable building (boost into it), beam it, throw the dummy through it. Every broken chunk adds heat, and an eruption adds a lot. Watch the pips fill.
2. **Level 1: police.** Small grey drones with a blue light bar fly in from the district edge, keep their distance and fire yellow tracers. Each hit is tiny. Lock on (R3) and punch one, or grab and throw it into the building or into another drone.
   - A thrown unit is a projectile: it smashes chunks and damages whatever it hits.
3. **Level 2: military.** Gunships with rotor discs keep higher and further away and fire **slow, visible rockets** with smoke trails. Dodge them sideways. A hit launches you, and rockets that miss blow chunks out of the building.
   - Shortcut: **Page Up** twice.
4. **Level 3: the anti-god weapon.** A big dark sphere with a ring and a long barrel. When it targets you:
   - a thin red line appears from its barrel, and a red **ANTI-GOD WEAPON CHARGING** warning shows (plus an arrow at the screen edge if it is off screen);
   - the line flickers and thickens; about half a second before it fires it turns solid and bright (**LOCKED - BREAK LINE OF SIGHT**) and stops following you;
   - **move sideways or put a building between you and it.** If it hits, you lose a big chunk of power, get blasted backwards, and the HUD shows **SURGE SUPPRESSED** (your meter can't fill for a few seconds).
   - Or rush it: it is slow and has a lot of power, but it can be launched, beamed and thrown like anything else.
   - Shortcut: **Page Up** three times.
5. **Decay.** Stop breaking things. After about 15 s of quiet the pips drain; units whose level is gone fly back to the edge and leave, once no god is still wanted (the units remember who raised heat for about 20 s).
6. Destroyed units fall as dark wrecks and break into debris when they land.

**Part 3: the rival (4 minutes).** A deep violet character with a crimson (dominance) halo, **VANTA**. Its name plate, power bar and surge state appear when you lock on to it or get within 60 m.

1. Let it come to you. It circles at about 12 m, then dashes in for light chains. Guard (LB) when it attacks; it punishes your guard with a charged heavy sometimes.
2. Attack it: it guards some of your chains (on normal, a bit under half, and never instantly).
3. At range it fires its beam; break line of sight or close the gap.
4. If you are staggered next to it, it may grab and throw you, aiming at the building. Launch it yourself and finish it (dash + X with the camera on the building) to drive it through the building.
5. When its meter fills, or when it's losing, it performs the ritual: you see its halo, eruption and surge from outside. Its surge makes it much stronger for 20 s; get away or guard.
6. When it is knocked out, **RIVAL DOWN** is announced; it returns after about 12 s (**THE RIVAL RETURNS**).
7. Three-way fights: the rival raises heat too, and the army shoots any god that raised heat, so units will attack it as well, and it fights back.
8. Try **Delete** for easy or hard. Normal should be beatable if you guard its chains and punish after its heavy.

To tune: heat and units are on **Opposition Config**, the rival's three difficulty presets and ranges on **Rival Config** (both in the District subscene).

## 2. Two players on one PC (Multiplayer Play Mode)

The **Multiplayer Play Mode** package is installed. It runs a second, lightweight Editor as player 2.

1. Open **Window > Play Mode > Scenarios**. You can also open it from the dropdown next to the Play button: choose **Configure play mode scenarios**.
2. Click **+** to create a scenario named "Two players".
   - Make sure the **Editor** instance is ticked.
   - Under **Additional Editor Instances**, click **+** once.
3. Select that scenario in the Play-mode dropdown, then press **Play**.
   - A second Editor window opens. The first time, it takes a minute.
4. In the **main** Editor, click **Host**.
5. In the **second** Editor window, leave the IP as `127.0.0.1` and the port as `7979`, then click **Join**.
   - You can also join later, after some of the building is broken. The late player sees exactly the same broken chunks.
   - On both screens, the broken chunks should match exactly, because the server decides them.
   - Debris is cosmetic and simulated separately on each PC, so the piles can look a little different on each screen.
6. Each window controls its own player, and you should see the other player moving in each window.
   - Player 1 is **orange** and player 2 is **blue**.
   - The yellow marker shows which way each player faces.

Only the focused window receives keyboard and mouse input. A gamepad can control one window while the keyboard controls the other.

### Without Multiplayer Play Mode: a development build as player 2

1. Open **File > Build Profiles**. Select **Windows**, tick **Development Build**, then click **Build**.
   - Build into a folder outside `Assets`, for example `Builds/`, which git ignores.
2. Press Play in the Editor and click **Host**.
3. Run the built `.exe`, then click **Join** with `127.0.0.1` / `7979`.

### Two PCs on the same network

1. On the host PC, click **Host**.
2. On the other PC, type the host's LAN IP (for example `192.168.1.20`) and click **Join**.
3. If the connection fails, allow the game or the Unity Editor through Windows Firewall on **UDP port 7979**.

## 3. Tuning

You can change all tuning values in the Inspector without touching code.

- **Flight:**
  1. In `Main.unity`, select the **District (SubScene)** object and tick **Open** in its Inspector. Opening the subscene makes its objects editable.
  2. Select **Flight Config**.
  3. Change the values. They are grouped as Hover, Cruise, Boost, Banking and Collision.
  - With the subscene open you can tune during Play and see the effect immediately (Unity re-bakes live).
- **Camera:** select **Game Systems** in `Main.unity` and use the **Flight Camera Rig** component.
  - It holds distance and field of view at hover vs. boost, lag, orbit sensitivity and invert Y.
- **Bindings:** double-click `Assets/_Project/Input/ToLive.inputactions`.
- **District layout:** constants at the top of `Assets/_Project/Editor/ToLiveSetup.cs` set the block count, street width, heights and seed.
  After changing them, run **Tools > To Live > Generate Grey-box District**.

## 4. Automated checks (for Claude, optional for you)

`Assets/_Project/Editor/DevProbe.cs` lets an agent drive a play-mode session from the Unity CLI (`unity command script eval ...`):

- fake gamepad input,
- add an in-process second client,
- print each player's position, gear and speed for the server and each client world.

## Known limits in Milestone 1

- The district is grey-box: boxes only, no destruction, no crowds.
- If the Editor window loses focus, Unity throttles it, and the Console shows "Server Tick Batching" warnings. Keep the Game view focused while testing feel.
- There is no in-game rebinding UI yet. Edit the `.inputactions` asset instead.
