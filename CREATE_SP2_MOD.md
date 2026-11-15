# How Claude creates a SimplePlanes 2 mod (from scratch, or "a random mod")

A guide for Claude. It was written from what this project (the armor mod, folder `Armor Mod/`) has already done and verified in the game. Everything here was really used in this repository, except where marked **not verified**. When a fact is not in this guide, read the game's decompiled code instead of guessing.

## 0. Rules of conduct (always apply)

- **Who asks:** usually a player who knows SP2 well and is a beginner in programming and Unity. Explain in small steps, say what was tested and what is a draft, answer in the language of the last message.
- **Claude can neither run the game nor build for it.** All code is "untested" until the user compiles in Visual Studio and sends `BepInEx/LogOutput.log` or screenshots. Say so when delivering. Do not claim something works.
- **Before delivering:** run the compile check (section 9) and reread the code for what it does not catch. A build error costs the user a whole round trip.
- **Git:** develop on the agreed branch, only update `master` when asked, do not open a PR unless asked, do not bump the version before the whole set is finished. End commits with the trailers the session requires.
- **Privacy:** do not put personal data (name, e-mail) in anything that goes outside.
- **Respect for others:** another modder's code goes in only with their permission; credit them in `claude.md` and in the changelog.

## 1. "Make a random mod": how to choose and run it

If the user only says "create a mod" (or "random"), **you choose the idea**, within these rules:

1. **One small, verifiable idea**, provable with a log line or something visible in flight. Nothing that depends on guessing the game's API without reading its code.
2. **Use at least one built-in game feature** (section 4) besides the patch itself: own panel, designer section, Funky Trees variable, key, setting, multiplayer sync, camera, etc. A "complete" mod combines 3 or 4 of them.
3. **Announce the idea in 3 lines** (what it does, where it acts, how the player sees it) and carry on without waiting for an answer, unless there is a decision that belongs to the user.
4. **Deliver in phases**, each with a check (an expected log line, a behavior). Phase 1 is always: the plugin loads and writes a line to the log.

Idea seeds (pick one, mix them, or invent another):

| Idea | Features it uses |
|---|---|
| Shot counter and accuracy per cannon, shown in a panel | cannon patch, Juicy panel, setting |
| "Radiator" part that overheats with the engine and writes the temperature to a variable | name tag, designer, Funky Trees, `Update` |
| Target marker (line and distance to the aimed vehicle) | raycast, `LineRenderer`, key, camera |
| Fuel that leaks with tank damage and syncs in multiplayer | part damage, variable, network message |
| "Black box": records the flight path and shows it as a line afterwards | `Update`, line drawing, panel, key |
| Eject a marked part when it takes damage above X | tag, `OnDamaged`, designer, undo |
| Brake parachute triggered by a key, with sliders in the designer | tag, designer, key, physics |

## 2. Project structure

- **BepInEx 5.4.23.5 x64 (Mono)** plugin, C# **netstandard2.1** (`Game.dll` is built for it). Visual Studio Community, no Unity Editor. The game is Unity 6.
- `ArmorFragments.csproj` (SDK-style: every new `.cs` is picked up automatically) references, from the game folder (`GameDir`; the data folder may be called `SimplePlanes 2_Data` or `SimplePlanes 2 (1.1)_Data`, the `.csproj` finds it): `BepInEx.dll`, `0Harmony.dll`, `Game.dll`, `UnityEngine*.dll` (Core, Physics, UI...), `Jundroo.Packages.dll` (Juicy, the UI), `Jundroo.Common.dll`, `Unity.InputSystem`, `Unity.TextMeshPro`, URP. All with `<Private>False</Private>` (not copied to the output).
- Main class: `[BepInPlugin(guid, name, version)]` inheriting `BaseUnityPlugin`. `Awake()` does: `Log = Logger`, `Config.Bind(...)` for everything, patch registration, a log line with the version. One `const string Version`, shown in the log and in the panel (this is how an old DLL is told from a new one).
- Output: the `.dll` goes to `<game>\BepInEx\plugins\`. Log at `<game>\BepInEx\LogOutput.log`. Settings at `BepInEx\config\<guid>.cfg`.
- Study material (personal only, never goes into the repository): the decompiled `Game.dll` (classes in `Assets.Scripts.*`), `Jundroo.Packages.dll`, and reference mods (GravyMenu and the other BepInEx mods for SP2).

## 3. How to act on the game: Harmony

- **Simple patch:** `[HarmonyPatch(typeof(Class), "Method")]` on a static class with `Prefix`/`Postfix`. Special parameters: `__instance`, `__result` (use `ref`), `__state`, `___privateField` (three underscores + the field's name), arguments by their real name, or by position `__0..__n` when the name does not exist (generated RPC methods: the name `P_0` is not found; use position).
- **Private methods, overloads and overrides:** pass the name as a string; for a set of overrides use `TargetMethods()`.
- **One failing patch must not bring down the others:** `SafePatcher.PatchAll` applies class by class and logs "Patch X skipped".
- **A Prefix returning `false`** skips the original method (that is how the mod handed over its own UI XML).
- **After a game update:** the mod can break without warning (it has happened: `RendererMaterialMap.Renderer` changed from `MeshRenderer` to `Renderer`). Keep a `CompatibilityCheck` that, on load, checks every method/field used by reflection and writes `[COMPAT]` to the log for what changed. For types that change shape, use reflection instead of direct access.
- **Name ambiguity:** `Assembly` clashes with another game type; write `System.Reflection.Assembly`.
- **State between frames:** the plugin has `Update()`; call from there what must run every frame (`ModuleOutputs.Tick()`, `ImpactCamera.Tick()`...). Never rebuild UI inside a click (it destroys the control being clicked): set `rebuildPending` and redo it in `Tick`.

## 4. Catalog of game features you can use

Each item says what exists, how to hook into it, and the known pitfall.

### 4.1 Parts, modifiers and data
- `PartScript` (`Assets.Scripts.Craft.Parts`) is the part in flight; `PartScript.Part` is the `PartData` (`Name`, `Id`, `Modifiers`, `LoadedMass`, `PartType`). Modifiers are `PartModifierData` (`CannonData`, `JFuselageData`, `JEngineData`, `FuelTankData`, `PropellerAssemblyData`, `TurboshaftData`, `JetEngineData`...). Test the type with `modifier is T`.
- Masses: the game stores every mass at **1/100** of the real value (the designer divides by 0.01 to show kg). Divide by 0.01 before using it in physics.
- Aircraft: `AircraftScript` (`Parts`, `MainCockpit`, `Velocity`, `RemoteAircraft`, `VariableSystem`, `NetworkAircraft`, `GetPartById`). Local player: `FlightSceneScript.Instance.LocalPlayer.Aircraft`.

### 4.2 Storing data on a part without changing the game: tags in the name
- The part's editable name (`Part.Name`) is saved with the aircraft, goes into undo and travels in multiplayer. The mod keeps properties in it, in brackets after the player's label: `Plate [ARM t=20 h=100] [MOD v=Engine]`. A `TagCodec` reads (`Parse`, cached, read-only; `ParseCopy` to edit) and writes (`Compose`) everything; unknown and old keys are ignored, so the format can evolve.
- This is the cheapest way to give **per-part data** without touching save files.

### 4.3 Designer (builder)
- Own section inside the part properties panel: patches on `PartPropertiesPanelScript` (`InitializeDesignerPanel` creates, `SetSelectedPart` selects, `UpdateCommonPartProperties` says the name changed). Controls come from the game's own templates (`PanelBuilder`: `control-header`, `control-toggle`, `control-slider`, `control-text`).
- Every change rewrites the part's name (`part.Part.Name = ...`, `modifier.OnPartNameChanged(name)`), refreshes the game's name field and creates an undo step when the slider is released (`Designer.Instance.CreateUndoStepForSelectedPart`). While dragging, update the name without creating an undo step.
- Show only the rows that make sense for the selected part's modifier. The game does not tell you when one of its own controls changes (e.g. the cannon's projectile type): read the value a few times per second and rebuild the rows if it changed.

### 4.4 Own panel in flight (Juicy UI)
- The game's UI is XML + scripts found by name. Three patches hand over yours: a button in the flight menu (`MenuPanelScript.OnWidgetInitialized`), the XML (`Jundroo.Juicy.ResourceLoader.LoadXml`, prefix returning your layout) and the script type (`ResourceLoader.GetScriptType`, postfix that looks in your assembly for names with your namespace).
- The window inherits `PanelDialogScript` (`IsModal => false` to keep flying). "Live" rows (`Live(label, () => text)`) refresh every 0.5 s; sliders and toggles bound to a `ConfigEntry` rewrite the setting.

### 4.5 Funky Trees and variables
- `VariableSystemScript.AddVariable(name)` creates an aircraft variable and `AircraftVariable.SetValue(value, priority)` writes to it (you may call it from `Update`; the game queues it for the next `StartFrame`). The game deletes variables nothing uses at every `RefreshVariables`, so recreate them in a postfix. Call `RefreshVariables` in the designer after changing tags so the name shows up in the Funky Trees lists.
- The mod's output variables: a damage level 0..1 per module; `<group>_rounds`, `<group>_fire`, `<group>_boom` on ammo racks.

### 4.6 Damage, explosions and destruction
- `PartScript.OnDamaged(int? attacker, float damage, Vector3 point, Vector3 direction)` adds to `PartDamage`; health is `MaxHealth`; destroyed = the whole body switched off (`BodyScript.DisconnectParts` → `SetActive(false)`). On a remote aircraft `OnDamaged` returns early: damage to another player goes through `Aircraft.NetworkAircraft.DamagePart(...)` (an RPC to the owner).
- `FlightSceneScript.CreateExplosion(...)` creates the game's explosions. Cannon: `CannonData` has `ImpactDamageScalar`, `ExplosionScalar` (private setters, via reflection), `AmmoType`, `ProjectileVolume`, `ProjectileVelocity`, `CaliberInMilimeters`.
- `CannonScript.Fire` instantiates the projectile (`Rigidbody`); `CannonProjectileScript.OnCollisionEnter` handles the impact (private fields `_previousFrameVelocity`, `_cannonScript`, `_isExplosive`). Use the previous frame's velocity, not the current one.

### 4.7 Physics, raycasts and colliders
- `Physics.RaycastNonAlloc` with a fixed buffer; ignore projectiles and use the mask without the world-space UI layer. To know how far a ray travels inside a part: `Collider.Raycast` forward (entry) and backward from a point beyond (exit).
- Layers: the main camera excludes 3, 14, 15, 22, 24, 27, 31; layers 28 to 30 are "Mod Layer 1-3" (use 29 for your extra drawing and hide it from the main camera).

### 4.8 Drawing and effects
- `LineRenderer` and primitive spheres work, with a material from whichever shader exists in the build (the game is URP and strips shaders: try `Sprites/Default`, `Hidden/Internal-Colored`, `Unlit/Color`, `Universal Render Pipeline/Unlit`, `UI/Default` and log which one worked). `MonoBehaviour` objects you create (animations) live in the world; destroy them at the end.
- "X-ray": translucent copies of the meshes (`MeshRenderer`/`MeshFilter`; parts with skinned meshes do not appear), hiding the originals with `Renderer.forceRenderingOff`.

### 4.9 Cameras
- `CameraManagerScript.Instance.MainCamera`, `.Controller` (e.g. `OrbitCameraController`). An extra camera rendered into a small window (picture-in-picture, like the impact camera) needs a layer mask, light and URP configured; write a log diagnostic (`[CAMDIAG]`) before debugging blind.

### 4.10 Input
- Keys through the Input System, configurable (`ConfigEntry`), with an equivalent button in the panel. Avoid `[`, `]`, `P`, `G`, `L` and Alt (the game already uses them or they cause trouble). F8 is taken by the vehicle X-ray.

### 4.11 Settings
- `Config.Bind(section, key, default, description/AcceptableValueRange)`. The accepted range is read by the panel to build the slider. A value that decides damage to other players must be **global** (section 4.12); the rest stays local (visuals, keys, logs).

### 4.12 Multiplayer (FishNet)
- A cannon projectile only exists in the shooter's game; everyone else sees only the muzzle flash. The shooter detects the collision and sends the damage to the server, which passes it to the part's owner.
- The game's own part-message channel: `INetworkAircraft.SendPartNetworkMessage`; intercept `ProcessPartNetworkMessage`; the game ignores unknown types (this mod uses 200 = host settings and 201 = "I have the mod"). The host repeats the global settings every 5 s; the client applies them and restores on leaving, and never writes the host's values into its own `.cfg`.
- If only the host has the mod, it can recompute what a shooter without the mod did (the host resolver). Peaceful mode: the server discards damage between players (warn in the log).
- **Not verified in every version:** test multiplayer with other players whenever you touch damage or networking.

## 5. Patterns that hold for any mod

1. **Everything configurable, with a safe default.** One slider in the panel for every tuning number, with a description.
2. **Useful, short logs**, with prefixes (`[COMPAT]`, `[HOST]`, `[SIM]`): the log is how the user reports bugs. Log decisions, not only errors.
3. **A test function inside the game**, like the armor mod's "simulated shot": a button that fires the logic without needing an opponent, showing the result.
4. **Fail gracefully:** `try/catch` around everything the game may change; the mod must never freeze flight.
5. **Cache per frame/shot** for anything expensive (colliders, densities, tags).
6. **Document in `claude.md`** what was done, what was not tested and the decisions. The next Claude starts there.
7. **A context document for each big mod/feature** (like `AMMO_RACKS_CONTEXT.md`).

## 6. Workflow per phase

1. **Choice/minimal question.** Ask only what is the user's to decide (a balance number, an ambiguous behavior). For the rest, pick a default and say which.
2. **Read the game's code** before assuming (the class, the fields, who calls it). Search the decompiled code with `grep`.
3. **Write the smallest phase that proves the path** (a log). Then the logic. Then the interface. Then the network.
4. **Run the compile check**, reread, deliver with: what changed, what to expect in the log, how to test, what is still untested.
5. **Receive the user's log**, fix, repeat. Only bump the version when the whole set is ready.

## 7. Mistakes that already happened (do not repeat)

- Slider "still visible" and dead key: it was the **old DLL** in the plugins folder. The version line in the log/panel settles it at once.
- X-ray ghosts invisible: they were on layer 3 (excluded from the camera); use a configurable layer.
- X-ray view arriving late: do not wait for idle; build fast, with a time limit and without throwing exceptions.
- Destroyed part vanishes from the X-ray: the game switches off the **whole body**; freeze the ghosts **before** `DisconnectParts` (prefix patch) on a holder that follows the cockpit.
- "Parameter name" bug in a generated RPC: use position `__0..__n`.
- A game update changed a type: reflection and `CompatibilityCheck`.
- An edit script that fails halfway: apply edits from a script file with assertions (each snippet's occurrence count must be 1) and check the result.

## 8. Delivery and versioning

- One commit per coherent set, a message in English saying what and why.
- `README`/changelog in the short Discord style, in English, one line per change (grouped: new things, fixes), with the release link and the testers' role mention.
- Version: patch for fixes, minor for features. Update the `Version` constant, `claude.md` and only then `master`.

## 9. Compile check without the game's DLLs

Claude does not have the game's `.dll` files, but it has the game's decompiled code. The mod can be compiled against the decompile: a small Roslyn program (`tools/compile-check` in this repository) puts the mod and the decompiled game in one compilation and reports only the errors in the mod's files (the decompile has errors of its own, which are ignored). It catches wrong types, nonexistent members, wrong arguments, unassigned variables and scope clashes. Details and setup: `tools/compile-check/README.md`. In a fresh container: `apt-get update && apt-get install -y dotnet-sdk-8.0`, then `dotnet restore && dotnet build` in the tool's folder and `./fetch-libs.sh`.

What it does not prove: behavior, strings (Harmony targets, reflection names) and whether the installed game is the same build as the decompile. Packages the decompile lacks (Input System, Unity UI, FishNet) go in as stubs. Without the decompile or without `dotnet`, fall back to the syntax check (`tree-sitter-c-sharp`) and careful rereading (duplicate declarations in nested scopes, `out`/`ref`, unassigned variables).

## 10. Checklist before saying "done"

- [ ] The idea is described in 3 lines and uses game features (section 4).
- [ ] The plugin loads and logs the version; applied patches show in the log.
- [ ] Every balance constant is in a setting, with a default.
- [ ] Panel and/or designer show the player what they need to test.
- [ ] Compile-checked against the decompile; reread done.
- [ ] Multiplayer effects thought through (global × local; damage to other players).
- [ ] `claude.md` updated with what was not tested.
- [ ] The final message says what to test and which log to send back.
