# Ammo racks

Ammo racks are fuselages that hold a cannon's ammunition. When hit through their rounds they can catch fire and explode inside the vehicle, killing it without blowing the hull apart.

0.8.1
- Cannons pick their own ammo racks with "Add rack" and "Remove rack". Cannons of different sizes each draw from their own racks, instead of every cannon sharing every rack.
- With no rack picked, a cannon uses its Ammo Count slider.
- Vehicles built on 0.8.0: pick the racks once on each cannon.

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
- Sized by the propellant left in the rack, as a real blast: pressure falls with distance. A full 100 mm rack wrecks modules within 2.7 m and damages them out to 4.8 m.
- It damages your vehicle's modules and racks, and other vehicles nearby. Armor in the way shields what's behind it. Armor itself is never damaged.

Armored vehicles keep their shape
- On vehicles with armor, the game no longer blows up parts or the vehicle from damage, and only damage modules and ammo racks take damage from shells and fragments. They are killed by an ammo rack exploding.
- Vehicles without armor, like planes, take damage and die as before.

Also
- Every cannon shows its shell energy and estimated penetration at 0, 30 and 60 degrees.
- Racks are drawn see-through in the designer with their rounds inside, and show their rounds in the X-ray.
- Funky Trees: `Ammo_rounds`, `Ammo_fire`, `Ammo_boom` (wire `_boom` to a detacher to throw the turret). Racks with a group name (`g=`) get their own `<group>_` variables.
- Settings are in the `[AmmoRacks]` section of the config file; `GameDamageExplosions` and `ArmoredStructureDamage` (both off) are under `[Fragments]`.
