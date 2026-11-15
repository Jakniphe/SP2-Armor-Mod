# Per-face armor

0.9.0

A fuselage used as armor is a hollow shell whose six faces are plates of their own thickness. Shells and fragments fly through open faces and the inside of the block, and meet an armored face from outside or from inside.

Setting up
- Turn on "Armor" on a fuselage: every face is open (0 mm).
- With "Per face" off, one "Thickness" sets every face at once. With "Per face" on, face buttons (Front, Back, Left, Right, Top, Bottom) appear and the slider sets the picked face on its own. Turning "Per face" off sets every face to the thickest one. A block whose faces differ opens with "Per face" on.
- Thickness is 0 to 500 mm, by slider or typed into the box beside it. The face being set is tinted orange on the block and the other armored faces blue; with "Per face" off, every armored face is orange.
- In the name: `[ARM h=100 b=50 fr=80 lt=20 rt=20]` (`fr bk lt rt tp bm`, millimeters; a face not listed is open).
- Armor is always per face. A block with whole-block armor (`t=`) becomes every face at that thickness when it is selected in the designer; until then it keeps working as one solid plate.
- Every slider in the mod's panels has a box beside it where the value can be typed.

Faces
- Front and Back are the block's ends (the game's front and rear sections); Left, Right, Top and Bottom are its sides, looking toward the front with the block's top up.
- Faces follow the block's real shape: tapered, offset, curved and cut blocks keep the face the player sees. A cut end stays the end up to 45 degrees of slope.
- On a rounded section the four sides are the quarters around the top, bottom, left and right. Rounded or bulged corners split along the diagonals between the sides.
- A triangle section (trapezium at 100%): the slanted walls are Left and Right, the base is Top or Bottom, and the edge that shrank to a point has no area.
- A cone (the fuselage Cone style): its surface is the four sides all the way to the tip, the base is Back, and Front exists only where the tip is cut off.
- Fixed parts with no fuselage shape (the parts-list nose cones and blocks) take the face whose direction is closest to the surface.
- A hollow fuselage's plate is its outer surface; its inner walls are part of no face.

How it plays
- An armored face is a plate like any other: ricochet, block or penetration, with the face's thickness and the armor's hardness and brittleness.
- An open face is empty space: no ricochet, no stop, no HE burst, no fuse start.
- A shell through the front plate flies on inside the block, through any parts in there, and meets the back plate from inside. Spall does the same.
- Cook-off blasts count the armored faces between the rack and each part.

Weight
- Armor weighs what its steel would: each armored face's area on the block times its thickness, at 7850 kg/m3. A 1 x 1 m face at 100 mm is 785 kg.
- The weight is added to the block's own, in the designer (part mass, craft mass, center of mass) and in flight. Nothing extra is saved in the craft file.
- The armor panel shows "Armor weight" and the weight of each armored face. In flight the log lists each armor block's weight by face.
- Whole-block armor (`t=`) weighs as every face at that thickness.
- The weight sits where the plates are: each face's weight at the middle of that face, half its thickness in. The block's center of mass moves toward its armor, so a thick front plate makes the vehicle nose-heavy and the designer's center-of-mass marker moves with it. The craft file keeps the block's own center of mass.

# Ammo racks

Ammo racks are fuselages that hold a cannon's ammunition. When hit through their rounds they can catch fire and explode inside the vehicle, killing it without blowing the hull apart.

0.8.1
- Cannons pick their own ammo racks with "Add rack" and "Remove rack". Cannons of different sizes each draw from their own racks, instead of every cannon sharing every rack.
- With no rack picked, a cannon uses its Ammo Count slider.
- Vehicles built on 0.8.0: pick the racks once on each cannon.
- The cook-off blast and its bang come from the rack, for every vehicle around; the vent only carries the fire. Armor in the way, the burning vehicle's own hull included, shields what's behind it.
- Racks exploding at the same moment go up as one blast, with one fire jet per vent.
- The bang's size grows with the cube root of the explosive, like the blast's reach, so low-velocity rounds (little propellant) still make a visible cloud. A full 120 lb rack looks the same as before.

Setting up
- Turn on "Ammo rack" on a fuselage (`[AMM]`) and "Cannon" on the gun (`[CAN]`). On the cannon, "Add rack" opens the game's part picker: click the racks it draws from (as a wheel picks its engine). A cannon can draw from several racks, and cannons that fire the same round can share one. With no rack picked, the cannon keeps its Ammo Count slider.
- A rack holds whole rounds sized from the cannons that draw from it, packed into the fuselage's real shape. It weighs its rounds and empties from the top layer down. A cannon empties its racks in the "Empty order" set on each rack (`o=`).
- Mark a fuselage at the turret ring or a hatch as a "Cook-off vent" (`[VNT]`).

Cook-off
- Only hits that pass through a round still in the rack count: the shell itself, plate spall, or burst fragments.
- A shell through the rounds is likely to set them off (about 64% for 20 mm, 95% for 37 mm, certain from 75 mm); fragments add up by the energy they leave.
- The rack burns for 10 s, then explodes. Fire jets out of the nearest vent, and every vent keeps burning.
- The explosion kills the vehicle (a normal kill: credit, kill cam, respawn), while the hull stays intact.

Explosion
- Sized by the propellant left in the rack, as a real blast from the rack: pressure falls with distance. A full 100 mm rack wrecks modules within 2.7 m and damages them out to 4.8 m.
- It damages your vehicle's modules and racks, and other vehicles nearby. Armor in the way, your own hull included, shields what's behind it. Armor itself is never damaged.
- Racks that explode at the same moment make one blast: their explosive adds up, up to the `MaxBlastLb` cap.

Armored vehicles keep their shape
- On vehicles with armor, the game no longer blows up parts or the vehicle from damage, and only damage modules and ammo racks take damage from shells and fragments. They are killed by an ammo rack exploding.
- Vehicles without armor, like planes, take damage and die as before.

Also
- Every cannon shows its shell energy and estimated penetration at 0, 30 and 60 degrees.
- Racks are drawn see-through in the designer with their rounds inside, and show their rounds in the X-ray.
- Funky Trees: `Ammo_rounds`, `Ammo_fire`, `Ammo_boom` (wire `_boom` to a detacher to throw the turret). Racks with a group name (`g=`) get their own `<group>_` variables.
- Settings are in the `[AmmoRacks]` section of the config file; `GameDamageExplosions` and `ArmoredStructureDamage` (both off) are under `[Fragments]`.
