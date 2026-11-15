# Compile check (no game needed)

Claude cannot run the game or build with the game's DLLs, but it can check the mod's C# against the **decompiled game
code** and find the errors a re-read misses: wrong types, misspelled or nonexistent members, wrong argument lists,
unassigned variables, scope clashes. In a test, it caught all five deliberate mistakes (type, missing game member, unassigned
variable, wrong `OnDamaged` arguments, wrong property type).

## How it works
The mod needs the game's types, and the only copy here is the decompiled source, which has errors of its own (packages
missing, decompiler artifacts), so a normal build gives up. `CompileCheck` (a small Roslyn program, `Program.cs`) puts the mod
AND the decompiled game in one compilation and asks for the diagnostics of the **mod's files only**. The game's
declarations are what the mod is checked against; the game's own errors are ignored.

References the mod's code needs and the decompile does not contain:
- Unity's API: the `UnityEngine.Modules` NuGet (2021.3.33, reference only). The game is Unity 6, so a few names are missing
  there; `unity6-compat.txt` ("old=>new") replaces them in the mod's text before the check (the mod is never changed).
- HarmonyX (NuGet) and the real `BepInEx.dll` (`fetch-libs.sh` downloads it into `lib/`, not committed).
- Stand-ins in `Stubs/ExternalStubs.cs` for what the game uses but the decompile lacks: Unity's Input System and UI, and FishNet.
  Only what the mod uses; add to it when the mod starts using another member.

## Setup (Ubuntu container, once)
    apt-get update && apt-get install -y dotnet-sdk-8.0
    export SSL_CERT_FILE=/root/.ccr/ca-bundle.crt       # if dotnet complains about certificates
    cd tools/compile-check
    dotnet restore && dotnet build --no-restore -v q     # builds the checker
    ./fetch-libs.sh                                      # real BepInEx.dll into lib/

## Run
The decompiled folders are the user's study material (never committed). Give the game's, `Jundroo.Packages` and
`Jundroo.Common` (the ones with the namespaces the mod uses):

    python3 tools/compile-check/check.py --src <game> --src <Jundroo.Packages> --src <Jundroo.Common>
    # or:  export SP2_SRC=<game>:<packages>:<common>   and run without --src

It prints `file(line,col): error CSxxxx: message` for each error and ends with `COMPILE CHECK: no errors in the mod (N files)`.
Takes about 7 seconds. Exit code 0 = clean.

## What it does NOT prove
- That the game's real build matches the decompile (a game update can change it): the decompile must be of the build the
  user plays, and `CompatibilityCheck` verifies the real members when the mod loads.
- Strings: Harmony targets (`[HarmonyPatch(typeof(X), "Method")]`), `___field` names and reflection names are not checked.
- Behavior. It is a compiler, not a test. The mod is still untested until the user runs it.
- Unity 6 only members that are not in `unity6-compat.txt`: they show up as errors of a missing member; if the member
  exists in Unity 6, add a substitution.
