# Armor impact lab

A program to try the mod's impact simulation outside the game, with different armor, ammunition, angle and speed.
It compiles the mod's own simulation (`Armor Mod/Sim/`), energy model (`EnergyModel.cs`), ammunition and tag code and
analytic stamps (`DamageMaps/PlateStamps.cs`), so what it shows is what the game would do, not a copy of it. Unity and
BepInEx are replaced by tiny stand-ins (`Core/Stubs/`). The mod's default settings (armor resistance, ricochet angles...) are
read from the `Config.Bind` lines of `Armor Mod/ArmorFragmentsPlugin.cs`, embedded when the program is built, so they cannot
drift from the mod (build again after changing a default).

Built on **.NET 10 (LTS)**. Three projects:
- `Core/` the engine and the page (a library: a small local web server and the page, both inside it).
- `Desktop/` a Windows window that runs the engine inside itself and shows the page with WebView2 (part of Microsoft Edge,
  already in Windows 10/11). This is the program to double-click.
- `Web/` the same engine from a console, opening the page in your browser (any system).

## The desktop program (Windows)
Double-click `publish.cmd` (needs the .NET 10 SDK, https://dotnet.microsoft.com/download/dotnet/10.0, which Visual Studio 2026 or a recent
2022 update installs). It builds **`publish\ArmorImpactLab.exe`**: one self-contained file (about 64 MB, nothing else to install). Copy it anywhere.
The engine runs inside the window on a free port of your computer only; nothing leaves it. In Visual Studio you can also open `SimLab.slnx`
and run `SimLab.Desktop`.

## In a browser
    dotnet run --project tools/sim-lab/Web
It prints the address (http://localhost:5057) and opens the page.

## The page
- **Armor**: thickness (mm), hardness and brittleness (%), as in the `[ARM]` tag.
- **Ammunition**: caliber (a preset or any value), mass (0 = what the game gives a shell of that caliber), speed, shell hardness.
- **Shot**: angle from the plate's normal and the direction along the plate.
- **Simulation**: quality (40x40 up to 128x128 columns), **use the graphics card (OpenCL)** (the same kernels as the game; falls back to the CPU if the computer has none that works; the first start builds the kernels, which can take a few seconds, and the page says when the card is ready), threads (0 = every core, for the CPU), seed, and an energy scale (a knob). A **progress bar** shows how much of the simulation is calculated while it runs.
- **Run**: the game's energy-model verdict (ricochet, blocked, penetrated, with the numbers), what the simulation did (deepest dent,
  hole, energy left, time), pictures of the front and back of the plate and a section through the impact, and, below,
  what the mod draws today (the analytic stamps) for the same shot.
- **Sweep speed**: the same shot at six speeds around yours, to see where the shell stops getting through and whether the
  simulation agrees with the model.
- **Thread scaling**: the same simulation on 1, 2, 4... threads. The work is handed out in small chunks to every thread that is
  free, so a busy thread simply takes fewer. Small grids (low quality) are not worth threading.

## From a terminal
    dotnet run --project tools/sim-lab/Web -- --cli out --thickness 20 --speed 800 --angle 30
writes the pictures into `out/` and prints the model and simulation numbers (any field of the scenario can be given).

## What it is not
The simulation is simplified and made to look right, not to be engineering-accurate (see `Armor Mod/Sim/ImpactSim.cs`).
