#!/usr/bin/env python3
"""Compile check for the mod without the game's DLLs. See README.md.

    python3 check.py --src <decompiled game folder> [--src <another>...]

The decompiled folders can also come from the environment variable SP2_SRC (folders separated by ':').
Exit code 0 = no compile errors in the mod's files.
"""
import argparse
import glob
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
NUGET = os.environ.get("NUGET_PACKAGES", os.path.expanduser("~/.nuget/packages"))


def first(patterns):
    for pattern in patterns:
        found = sorted(glob.glob(pattern))
        if found:
            return found[-1]
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", action="append", default=[], help="decompiled game folder (repeatable)")
    parser.add_argument("--mod", default=os.path.join(ROOT, "Armor Mod"))
    args = parser.parse_args()

    sources = args.src + [p for p in os.environ.get("SP2_SRC", "").split(":") if p]
    if not sources:
        print("No decompiled game folder: pass --src <folder> or set SP2_SRC.", file=sys.stderr)
        return 2

    netstandard = first(["/usr/lib/dotnet/packs/NETStandard.Library.Ref/*/ref/netstandard2.1",
                         "/usr/share/dotnet/packs/NETStandard.Library.Ref/*/ref/netstandard2.1",
                         os.path.expanduser("~/.dotnet/packs/NETStandard.Library.Ref/*/ref/netstandard2.1")])
    unity = first([os.path.join(NUGET, "unityengine.modules", "*", "lib", "net35")])
    harmony = first([os.path.join(NUGET, "harmonyx", "*", "lib", "net452", "0Harmony.dll")])
    bepinex = os.path.join(HERE, "lib", "BepInEx.dll")
    for name, path in (("netstandard reference pack", netstandard), ("UnityEngine.Modules (run: dotnet restore)", unity),
                       ("HarmonyX (run: dotnet restore)", harmony), ("lib/BepInEx.dll (run: ./fetch-libs.sh)", bepinex)):
        if not path or not os.path.exists(path):
            print("Missing: " + name, file=sys.stderr)
            return 2

    tool = first([os.path.join(HERE, "bin", "*", "net8.0", "CompileCheck.dll")])
    if not tool:
        print("The checker is not built: run  dotnet build  in " + HERE, file=sys.stderr)
        return 2

    command = ["dotnet", tool, "--mod", args.mod, "--subst", os.path.join(HERE, "unity6-compat.txt"),
               "--extra", os.path.join(HERE, "Stubs"),
               "--ref", netstandard, "--ref", unity, "--ref", harmony, "--ref", bepinex]
    for source in sources:
        command += ["--src", source]
    return subprocess.call(command)


if __name__ == "__main__":
    sys.exit(main())
