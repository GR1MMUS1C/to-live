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

### Gears

- **Hover** (default): moves relative to the camera at up to 15 m/s and stops precisely when you let go.
- **Cruise**: hold thrust. You fly where the camera points, accelerate to 80 m/s and bank into turns.
- **Boost**: while cruising, also hold boost to accelerate to 400 m/s. Turning is much slower.
  - Passing **343 m/s** (the speed of sound) logs "Crossed supersonic" in the Console and shows a white placeholder ring.
- You can't pass through buildings or the ground; you slide along them. There is no damage.

The bottom-left overlay shows the current **gear**, **speed** (m/s) and **ping**.

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
