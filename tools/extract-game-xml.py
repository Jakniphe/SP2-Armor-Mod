"""Finds the game's Juicy layouts (the XML of its UI) inside resources.assets, where they are stored as plain text.

Usage:  python tools/extract-game-xml.py <text to look for> <output folder>
Example: python tools/extract-game-xml.py throttle-progress out     (the flight UI: Xml/Flight/FlightUI)
         python tools/extract-game-xml.py 'class="btn-flight"' out  (the styles: Xml/Flight/FlightStyles)

For personal study only (the game's files are not ours to redistribute).
"""
import os
import sys

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SimplePlanes 2\SimplePlanes 2_Data\resources.assets"

if len(sys.argv) < 3:
    print(__doc__)
    sys.exit(1)

needle = sys.argv[1].encode()
out_dir = sys.argv[2]
os.makedirs(out_dir, exist_ok=True)
data = open(GAME, 'rb').read()
pos = 0
found = 0
while True:
    i = data.find(needle, pos)
    if i < 0:
        break
    start = data.rfind(b'<Xml', 0, i)
    end = data.find(b'</Xml>', i)
    if start >= 0 and 0 < end - start < 3_000_000:
        path = os.path.join(out_dir, 'xml_%d.xml' % found)
        open(path, 'w', encoding='utf-8').write(data[start:end + 6].decode('utf-8', 'replace'))
        print('wrote', path, end - start, 'bytes')
        found += 1
        pos = end + 6
    else:
        # a style file is not always a whole document: show what is around the text instead
        a = max(0, i - 100)
        print('--- at', i)
        print(data[a:i + 700].decode('utf-8', 'replace'))
        pos = i + len(needle)
