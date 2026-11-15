# Armor impact lab

A small web app to try the mod's impact simulation outside the game, with different armor, ammunition, angle and speed.
It compiles the mod's own simulation (`Armor Mod/Sim/`), energy model (`EnergyModel.cs`), ammunition and tag code and
analytic stamps (`DamageMaps/PlateStamps.cs`), so what it shows is what the game would do, not a copy of it. Unity and
BepInEx are replaced by tiny stand-ins (`Stubs/`). The mod's default settings (armor resistance, ricochet angles...) are
read from `Armor Mod/ArmorFragmentsPlugin.cs` when the lab starts, so they cannot drift from the mod.

## Run (Windows or Linux; needs the .NET 8 SDK, which Visual Studio installs)
    dotnet run --project tools/sim-lab
It prints the address (http://localhost:5057) and opens the page.

## The page
- **Armor**: thickness (mm), hardness and brittleness (%), as in the `[ARM]` tag.
- **Ammunition**: caliber (a preset or any value), mass (0 = what the game gives a shell of that caliber), speed, shell hardness.
- **Shot**: angle from the plate's normal and the direction along the plate.
- **Simulation**: quality (40x40, 64x64 or 96x96 columns), threads (0 = every core), seed, and an energy scale (a knob).
- **Run**: the game's energy-model verdict (ricochet, blocked, penetrated, with the numbers), what the simulation did (deepest dent,
  hole, energy left, time), pictures of the front and back of the plate and a section through the impact, and, below,
  what the mod draws today (the analytic stamps) for the same shot.
- **Sweep speed**: the same shot at six speeds around yours, to see where the shell stops getting through and whether the
  simulation agrees with the model.
- **Thread scaling**: the same simulation on 1, 2, 4... threads. The work is handed out in small chunks to every thread that is
  free, so a busy thread simply takes fewer. Small grids (low quality) are not worth threading.

## From a terminal
    dotnet run --project tools/sim-lab -- --cli out --thickness 20 --speed 800 --angle 30
writes the pictures into `out/` and prints the model and simulation numbers (any field of the scenario can be given).

## What it is not
The simulation is simplified and made to look right, not to be engineering-accurate (see `Armor Mod/Sim/ImpactSim.cs`).
