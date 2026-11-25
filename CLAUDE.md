# To Live: notes for Claude

- **Design source of truth:** the design doc at https://claude.ai/artifact/R9KTCMBJkDpJguvwksWfaN.
  [docs/DESIGN.md](docs/DESIGN.md) is the in-repo summary. Read it before changing gameplay.
- **How to play-test:** see [docs/PLAYTEST.md](docs/PLAYTEST.md).
- **Who you're working with:** Jon knows Unreal Engine well and is new to Unity. Comment code with short Unreal analogies where they help
  (for example "like a SpringArm", "like CharacterMovement prediction", "like a Data Asset"). Keep the comments short.
- **Engine and packages:**
  - Unity 6000.6.8f1, URP.
  - Entities, Netcode for Entities and Unity Physics 6.6.0.
  - Input System, Cinemachine, and Multiplayer Play Mode.
- **API rule:** don't write Netcode for Entities, Entities or Unity Physics code from memory.
  Check the installed package docs and samples in `Library/PackageCache/` first (for example `com.unity.netcode@*/Documentation~` and `Tests/Editor/DocCodeSamples`).
- **Generated content:** build these from **Tools > To Live** (`Assets/_Project/Editor/ToLiveSetup.cs`), not by hand:
  - materials
  - the player prefab
  - the District subscene
  - `Main.unity`
  - the hero materials, Animator Controller and prefabs (**Build Hero Look**, `ToLiveSetup.Hero.cs`)
- **Hero art:** the Meshy-rigged FBX files and 2K textures live in `Assets/_Project/Art/Characters/Hero/` and go through Git LFS (`.gitattributes`).
  `HeroImportSettings.cs` imports them as Humanoid with an explicit bone map (Meshy has no Spine bone: Chest maps to Spine, UpperChest to Chest).
- **Tuning values:** flight numbers live in `FlightConfigAuthoring`, combat numbers in `CombatConfigAuthoring`, transformation/beam/grab numbers in `PowerConfigAuthoring`, the power look in `PowerLook`, debris settings in `DebrisConfigAuthoring`, heat and response-force numbers in `OppositionConfigAuthoring`, rival behaviour in `RivalConfigAuthoring`, crowd/district trust, fear and fuel numbers in `DistrictConfigAuthoring`, crisis, ownership, rebuild/fortify and defeat numbers in `CrisisConfigAuthoring` camera numbers in `FlightCameraRig`, and the hero look (foot offset, grounded distance, run playback, tints) in `HeroPresenter` on "Power Look", with the dominance-form threshold in `PowerLook`.
  Expose new tuning values in the Inspector the same way.
- **Testing from the CLI:** `Assets/_Project/Editor/DevProbe.cs` can fake input (`FakeInput`, `FakeCombat`, `PlayCode` button sequences), teleport, report combat and break state per world, report the hero (`HeroReport`), save a camera screenshot (`Shot`), break chunks and run the benchmark. PowerShell strips double quotes from `script eval`, so call it with code that has no string literals.
- **Networking rule:** gameplay that must agree between players (hits, breaks) is decided on the server and replicated as compact state. Cosmetics (debris, flashes, trails, shake) are simulated locally per client.
- **Runtime materials:** never `Shader.Find` at runtime (player builds strip unreferenced shaders). Use `FxMaterials` (material assets on the "FX Materials" scene object).
- **One MonoBehaviour per file**, named after the class. Otherwise player builds break.
- **Git:** commit every asset together with its `.meta` file. Never commit `Library/`, `Temp/`, `Obj/`, `Logs/` or `UserSettings/`.
