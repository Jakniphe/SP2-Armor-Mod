using System.Collections.Generic;
using System.Reflection;

using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Character;
using UnityEngine;

namespace ArmorFragments
{
    // The crew's bodies. In flight every crew seat ([CRW], Crew.cs) has a character sitting in it: the game's
    // designer character for that seat (IKSeatData.DesignerCharacter; "Chad (WW1)" when it is set to None), posed by
    // the seat's own IK (IKSeatScript.StartPose). A seat the player's own character sits in keeps the player's.
    // Bodies are made on every copy of the vehicle (yours and other players'), since shots are traced in the
    // shooter's game.
    //
    // Hits on crew are decided by the body, not the seat: shells and fragments pass through the seat's own colliders
    // (RayEvents.Gather leaves them out; the game's own shells are set to pass them, CrewShellPatch) and meet capsules
    // around the sitting character's head, torso, arms and legs
    // instead (human body density). What crosses a body is damage to that crew seat (Crew.OnDamaged).
    internal static class CrewBodies
    {
        private sealed class Body
        {
            public PartScript Seat;
            public Transform Mount;      // the seat's SeatScript transform (characters sit relative to it)
            public IKSeatScript Ik;
            public GameObject Chad;      // ours (null if it couldn't be made)
            public bool Posed;
            public Transform BonesOf;    // the character the bones below were found on (found again when it changes)
            public Skeleton Bones;
            public bool HasBones;
            public Collider[] SeatColliders;   // the seat's own colliders, which shells pass through
        }

        private struct Capsule
        {
            public Vector3 A;
            public Vector3 B;
            public float R;
        }

        private const string DefaultCharacter = "Chad (WW1)";
        private const string ChadName = "ArmorFragments crew";
        private const float BodyDensity = 985f;     // kg per cubic meter
        private const float ReachRadius = 1.4f;     // a sitting body fits in this sphere around its torso

        private static readonly Dictionary<PartScript, Body> Bodies = new Dictionary<PartScript, Body>();
        private static readonly List<Capsule> Scratch = new List<Capsule>();
        private static readonly List<Vector2> Spans = new List<Vector2>();
        private static float nextCheck;

        public static bool IsBody(PartScript part)
        {
            return part != null && Bodies.ContainsKey(part);
        }

        public static float Density()
        {
            return BodyDensity * ArmorFragmentsPlugin.DensityModifier.Value;
        }

        public static bool Any
        {
            get { return Bodies.Count > 0; }
        }

        // The first crew body the line from 'from' to 'to' gets into: its seat, where, and the body's surface normal.
        public static bool FirstCrossed(Vector3 from, Vector3 to, out PartScript seat, out Vector3 point, out Vector3 normal)
        {
            seat = null;
            point = Vector3.zero;
            normal = Vector3.zero;
            Vector3 path = to - from;
            float length = path.magnitude;
            if (length < 1e-5f) return false;
            Vector3 dir = path / length;

            float nearest = float.PositiveInfinity;
            foreach (Body body in Bodies.Values)
            {
                if (body.Seat == null || body.Mount == null || !Near(body, from, dir, length)) continue;
                float entry;
                float exit;
                Vector3 n;
                if (!Spanned(body, from, dir, length, out entry, out exit, out n)) continue;
                if (entry < 0f || entry > length || entry >= nearest) continue;   // starting inside: met last step
                nearest = entry;
                seat = body.Seat;
                point = from + dir * entry;
                normal = n;
            }
            return seat != null;
        }

        // A shell just fired passes through every crew seat (the game's physics would stop it on the chair): it meets
        // the bodies instead (CrewShellFlightPatch), and the mod's rays carry on from wherever it does hit.
        public static void PassSeats(Collider[] shell)
        {
            if (shell == null || shell.Length == 0 || Bodies.Count == 0) return;
            foreach (Body body in Bodies.Values)
            {
                if (body.Seat == null) continue;
                if (body.SeatColliders == null)
                {
                    List<Collider> own = new List<Collider>();
                    foreach (Collider collider in body.Seat.GetComponentsInChildren<Collider>(true))
                        if (!collider.isTrigger && collider.GetComponentInParent<PartScript>() == body.Seat) own.Add(collider);
                    body.SeatColliders = own.ToArray();
                }
                foreach (Collider seat in body.SeatColliders)
                {
                    if (seat == null) continue;
                    foreach (Collider piece in shell)
                        if (piece != null) Physics.IgnoreCollision(piece, seat, true);
                }
            }
        }

        // ---- Seating the crew ------------------------------------------------------------

        // A vehicle in flight (yours or another player's): every crew seat gets its body.
        public static void Register(AircraftScript aircraft)
        {
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return;
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || Bodies.ContainsKey(part)) continue;
                if (!Crew.CountsAsCrew(data, TagCodec.Parse(data.Name))) continue;

                SeatScript seat = part.GetModifier<SeatScript>();
                if (seat == null) continue;
                Body body = new Body { Seat = part, Mount = seat.transform, Ik = part.GetModifier<IKSeatScript>() };
                Bodies[part] = body;
                Seat(body, seat);
            }
        }

        // The seat's designer character, made the way the designer makes it (IKSeatScript.SetDesignerCharacter).
        private static void Seat(Body body, SeatScript seat)
        {
            try
            {
                string path = CharacterPath(body.Ik);
                GameObject prefab = path != null ? Resources.Load<GameObject>(path) : null;
                if (prefab == null)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Crew: no character for seat '" + body.Seat.Part.Name + "'");
                    return;
                }

                Transform chad = Object.Instantiate(prefab).transform;
                chad.name = ChadName;
                chad.SetParent(body.Mount, true);
                Vector3 scale = seat.Data.Part.PartScale ?? Vector3.one;
                chad.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
                chad.SetLocalPositionAndRotation(seat.Data.SeatedPosition, Quaternion.Euler(seat.Data.SeatedRotation));

                // A picture only: no physics of its own on the vehicle.
                foreach (Collider collider in chad.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (Rigidbody rigidbody in chad.GetComponentsInChildren<Rigidbody>(true))
                {
                    rigidbody.isKinematic = true;
                    rigidbody.detectCollisions = false;
                }
                int layer = LayerMask.NameToLayer("Chad");
                if (layer >= 0)
                    foreach (Transform t in chad.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;

                if (!string.IsNullOrWhiteSpace(seat.Data.Animation))
                {
                    RuntimeAnimatorController controller =
                        Assets.Scripts.Game.Instance.ResourceLoader.Load<RuntimeAnimatorController>(seat.Data.Animation);
                    Animator animator = chad.GetComponentInChildren<Animator>();
                    if (controller != null && animator != null)
                    {
                        animator.runtimeAnimatorController = controller;
                        animator.Update(0f);
                    }
                }
                body.Chad = chad.gameObject;
                body.Posed = false;
                Show(body);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew: could not seat a character in '" + body.Seat.Part.Name + "': " + e.Message);
            }
        }

        private static string CharacterPath(IKSeatScript ik)
        {
            if (ik == null || ik.Data == null) return null;
            string path;
            string name = ik.Data.DesignerCharacter;
            if (!string.IsNullOrEmpty(name) && ik.Data.CharacterPaths.TryGetValue(name, out path)) return path;
            return ik.Data.CharacterPaths.TryGetValue(DefaultCharacter, out path) ? path : null;
        }

        // Our character sits there unless another one (the player's) does. Whenever the character in the seat
        // changes, its skeleton is looked up (and the log says which one the body uses).
        private static void Show(Body body)
        {
            if (body.Chad == null) return;
            bool taken = Foreign(body) != null;
            if (taken)
            {
                if (body.Chad.activeSelf) body.Chad.SetActive(false);
                body.Posed = false;
            }
            else
            {
                if (!body.Chad.activeSelf) body.Chad.SetActive(true);
                if (!body.Posed && body.Ik != null)
                {
                    body.Ik.StartPose(body.Chad.transform);
                    body.Posed = true;
                }
            }
            if (body.Mount.gameObject.activeInHierarchy && Character(body) != body.BonesOf) Capsules(body, Scratch);
        }

        // A character in the seat that isn't ours (the player's own).
        private static Animator Foreign(Body body)
        {
            foreach (Animator animator in body.Mount.GetComponentsInChildren<Animator>(false))
            {
                if (body.Chad != null && animator.transform.IsChildOf(body.Chad.transform)) continue;
                if (animator.isActiveAndEnabled) return animator;
            }
            return null;
        }

        public static void Tick()
        {
            if (Bodies.Count == 0 || Time.time < nextCheck) return;
            nextCheck = Time.time + 0.25f;

            List<PartScript> gone = null;
            foreach (KeyValuePair<PartScript, Body> pair in Bodies)
            {
                if (pair.Key == null || pair.Value.Mount == null)
                {
                    if (gone == null) gone = new List<PartScript>();
                    gone.Add(pair.Key);
                    continue;
                }
                Show(pair.Value);
            }
            if (gone != null)
                foreach (PartScript part in gone) Bodies.Remove(part);
        }

        // ---- The body's shape ------------------------------------------------------------

        // Capsules around the sitting character's bones (head, torso, upper and lower arms and legs), read where the
        // bones are now, so they follow the pose and the vehicle. The bones come from the seat IK's skeleton, or a
        // humanoid rig; without either: a torso and a head above the seat.
        private static void Capsules(Body body, List<Capsule> into)
        {
            into.Clear();
            Transform character = Character(body);
            if (character != body.BonesOf)
            {
                body.BonesOf = character;
                body.HasBones = character != null &&
                                (FromIk(character, out body.Bones) || FromHumanoid(character, out body.Bones));
                if (character != null)
                    ArmorFragmentsPlugin.Log.LogInfo("Crew body in '" + body.Seat.Part.Name + "': " +
                        (body.HasBones ? "capsules on the seated character's " + body.Bones.From
                                       : "no skeleton found on '" + character.name + "', an average body above the seat"));
            }
            Skeleton bones = body.Bones;
            if (body.HasBones && bones.Pelvis != null && bones.Chest != null)
            {
                Add(into, bones.Pelvis.position, bones.Chest.position, 0.15f);
                if (bones.Head != null)
                {
                    Vector3 up = (bones.Head.position - bones.Chest.position).normalized;
                    if (up.sqrMagnitude < 0.5f) up = body.Mount.up;
                    Add(into, bones.Head.position + up * 0.02f, bones.Head.position + up * 0.14f, 0.1f);
                }
                Limb(into, bones.LeftThigh, bones.LeftCalf, 0.075f);
                Limb(into, bones.LeftCalf, bones.LeftFoot, 0.055f);
                Limb(into, bones.RightThigh, bones.RightCalf, 0.075f);
                Limb(into, bones.RightCalf, bones.RightFoot, 0.055f);
                Limb(into, bones.LeftUpperArm, bones.LeftForearm, 0.05f);
                Limb(into, bones.LeftForearm, bones.LeftHand, 0.04f);
                Limb(into, bones.RightUpperArm, bones.RightForearm, 0.05f);
                Limb(into, bones.RightForearm, bones.RightHand, 0.04f);
                return;
            }

            Vector3 seat = body.Mount.position;
            Vector3 upward = body.Mount.up;
            Add(into, seat + upward * 0.15f, seat + upward * 0.7f, 0.17f);
            Add(into, seat + upward * 0.85f, seat + upward * 0.95f, 0.1f);
        }

        // The character sitting in the seat: ours, or the player's when it sits there.
        private static Transform Character(Body body)
        {
            if (body.Chad != null && body.Chad.activeInHierarchy) return body.Chad.transform;
            Animator foreign = Foreign(body);
            if (foreign == null) return null;
            Transform t = foreign.transform;
            while (t.parent != null && t.parent != body.Mount) t = t.parent;   // up to the seat's direct child
            return t;
        }

        private struct Skeleton
        {
            public string From;
            public Transform Pelvis, Chest, Head;
            public Transform LeftThigh, LeftCalf, LeftFoot, RightThigh, RightCalf, RightFoot;
            public Transform LeftUpperArm, LeftForearm, LeftHand, RightUpperArm, RightForearm, RightHand;
        }

        // The skeleton the seat's IK poses the character with (FinalIK's FullBodyBipedIK.references, in the game's
        // ThirdParty.dll; read by name, so the mod doesn't depend on that library). The torso runs from the pelvis to
        // the last spine bone.
        private static bool FromIk(Transform character, out Skeleton bones)
        {
            bones = new Skeleton();
            Component ik = null;
            foreach (Component component in character.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != "FullBodyBipedIK") continue;
                ik = component;
                break;
            }
            if (ik == null) return false;

            FieldInfo referencesField = ik.GetType().GetField("references", BindingFlags.Public | BindingFlags.Instance);
            object references = referencesField != null ? referencesField.GetValue(ik) : null;
            if (references == null) return false;

            Transform[] spine = Field<Transform[]>(references, "spine");
            bones.From = "IK skeleton";
            bones.Pelvis = Field<Transform>(references, "pelvis");
            bones.Head = Field<Transform>(references, "head");
            bones.Chest = spine != null && spine.Length > 0 && spine[spine.Length - 1] != null
                ? spine[spine.Length - 1]
                : bones.Head != null ? bones.Head.parent : null;
            bones.LeftThigh = Field<Transform>(references, "leftThigh");
            bones.LeftCalf = Field<Transform>(references, "leftCalf");
            bones.LeftFoot = Field<Transform>(references, "leftFoot");
            bones.RightThigh = Field<Transform>(references, "rightThigh");
            bones.RightCalf = Field<Transform>(references, "rightCalf");
            bones.RightFoot = Field<Transform>(references, "rightFoot");
            bones.LeftUpperArm = Field<Transform>(references, "leftUpperArm");
            bones.LeftForearm = Field<Transform>(references, "leftForearm");
            bones.LeftHand = Field<Transform>(references, "leftHand");
            bones.RightUpperArm = Field<Transform>(references, "rightUpperArm");
            bones.RightForearm = Field<Transform>(references, "rightForearm");
            bones.RightHand = Field<Transform>(references, "rightHand");
            return bones.Pelvis != null && bones.Chest != null;
        }

        private static T Field<T>(object owner, string name) where T : class
        {
            FieldInfo field = owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(owner) as T : null;
        }

        // A Unity humanoid rig.
        private static bool FromHumanoid(Transform character, out Skeleton bones)
        {
            bones = new Skeleton();
            Animator animator = character.GetComponentInChildren<Animator>();
            if (animator == null || !animator.isHuman) return false;

            bones.From = "humanoid rig";
            bones.Pelvis = animator.GetBoneTransform(HumanBodyBones.Hips);
            bones.Chest = animator.GetBoneTransform(HumanBodyBones.Neck) ??
                          animator.GetBoneTransform(HumanBodyBones.UpperChest) ??
                          animator.GetBoneTransform(HumanBodyBones.Chest);
            bones.Head = animator.GetBoneTransform(HumanBodyBones.Head);
            bones.LeftThigh = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            bones.LeftCalf = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            bones.LeftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            bones.RightThigh = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            bones.RightCalf = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            bones.RightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            bones.LeftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            bones.LeftForearm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            bones.LeftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            bones.RightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            bones.RightForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            bones.RightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            return bones.Pelvis != null && bones.Chest != null;
        }

        private static void Limb(List<Capsule> into, Transform from, Transform to, float radius)
        {
            if (from != null && to != null) Add(into, from.position, to.position, radius);
        }

        private static void Add(List<Capsule> into, Vector3 a, Vector3 b, float radius)
        {
            into.Add(new Capsule { A = a, B = b, R = radius });
        }

        // ---- Rays (RayEvents.Gather, PartPhysics.Chord) ------------------------------------

        // Each crew body the line from 'start' along 'dir' meets within 'length': one event where it first gets into
        // the body. Distances in the events are measured from start + dir x backOff (the caller's origin).
        public static void AddEvents(Vector3 start, Vector3 dir, float length, float backOff, float touch,
                                     PartScript ignore, List<RayEvent> into)
        {
            if (Bodies.Count == 0) return;
            foreach (Body body in Bodies.Values)
            {
                if (body.Seat == null || body.Seat == ignore || body.Mount == null) continue;
                if (!Near(body, start, dir, length)) continue;

                float entry;
                float exit;
                Vector3 normal;
                if (!Spanned(body, start, dir, length, out entry, out exit, out normal)) continue;
                float distance = entry - backOff;
                if (distance < -touch) continue;   // the line starts inside it, as for a collider
                into.Add(new RayEvent
                {
                    Distance = distance, Point = start + dir * entry, Normal = normal, Part = body.Seat,
                });
            }
        }

        // How far the line runs inside the body (entry, exit and the length inside, the capsules' overlaps counted
        // once). Starting inside the body: entry 0.
        public static float Chord(PartScript part, Vector3 origin, Vector3 direction, float maxDistance,
                                  out float entryDistance, out float exitDistance)
        {
            entryDistance = 0f;
            exitDistance = 0f;
            Body body;
            if (part == null || !Bodies.TryGetValue(part, out body) || body.Mount == null) return 0f;

            Capsules(body, Scratch);
            Spans.Clear();
            foreach (Capsule capsule in Scratch)
            {
                float t0;
                float t1;
                Vector3 normal;
                if (!Hit(capsule, origin, direction, out t0, out t1, out normal)) continue;
                t0 = Mathf.Max(t0, 0f);
                t1 = Mathf.Min(t1, maxDistance);
                if (t1 > t0) Spans.Add(new Vector2(t0, t1));
            }
            if (Spans.Count == 0) return 0f;

            Spans.Sort((a, b) => a.x.CompareTo(b.x));
            entryDistance = Spans[0].x;
            float total = 0f;
            float reached = float.NegativeInfinity;
            foreach (Vector2 span in Spans)
            {
                float from = Mathf.Max(span.x, reached);
                if (span.y > from) total += span.y - from;
                reached = Mathf.Max(reached, span.y);
                exitDistance = Mathf.Max(exitDistance, span.y);
            }
            return total;
        }

        private static bool Near(Body body, Vector3 start, Vector3 dir, float length)
        {
            Vector3 center = body.Mount.position + body.Mount.up * 0.4f;
            float along = Mathf.Clamp(Vector3.Dot(center - start, dir), 0f, length);
            return (start + dir * along - center).sqrMagnitude <= ReachRadius * ReachRadius;
        }

        private static bool Spanned(Body body, Vector3 origin, Vector3 dir, float length,
                                    out float entry, out float exit, out Vector3 normal)
        {
            entry = float.PositiveInfinity;
            exit = float.NegativeInfinity;
            normal = -dir;
            Capsules(body, Scratch);
            foreach (Capsule capsule in Scratch)
            {
                float t0;
                float t1;
                Vector3 n;
                if (!Hit(capsule, origin, dir, out t0, out t1, out n)) continue;
                if (t1 < 0f || t0 > length) continue;
                if (t0 < entry)
                {
                    entry = t0;
                    normal = n;
                }
                exit = Mathf.Max(exit, t1);
            }
            return entry <= exit;
        }

        // Where the line origin + t x dir (dir of length 1) is inside the capsule: [t0, t1], and the surface's normal
        // at t0. A capsule is convex: the span is the union of its two end spheres and its cylinder.
        private static bool Hit(Capsule c, Vector3 origin, Vector3 dir, out float t0, out float t1, out Vector3 normal)
        {
            t0 = float.PositiveInfinity;
            t1 = float.NegativeInfinity;
            normal = -dir;
            float a0, a1;
            if (Sphere(origin, dir, c.A, c.R, out a0, out a1)) { t0 = Mathf.Min(t0, a0); t1 = Mathf.Max(t1, a1); }
            if (Sphere(origin, dir, c.B, c.R, out a0, out a1)) { t0 = Mathf.Min(t0, a0); t1 = Mathf.Max(t1, a1); }
            if (Cylinder(origin, dir, c.A, c.B, c.R, out a0, out a1)) { t0 = Mathf.Min(t0, a0); t1 = Mathf.Max(t1, a1); }
            if (t0 > t1) return false;

            Vector3 point = origin + dir * t0;
            Vector3 axis = c.B - c.A;
            float s = axis.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - c.A, axis) / axis.sqrMagnitude) : 0f;
            Vector3 out_ = point - (c.A + axis * s);
            if (out_.sqrMagnitude > 1e-10f) normal = out_.normalized;
            return true;
        }

        private static bool Sphere(Vector3 origin, Vector3 dir, Vector3 center, float r, out float t0, out float t1)
        {
            Vector3 m = origin - center;
            float b = Vector3.Dot(m, dir);
            float disc = b * b - (m.sqrMagnitude - r * r);
            if (disc < 0f)
            {
                t0 = t1 = 0f;
                return false;
            }
            float root = Mathf.Sqrt(disc);
            t0 = -b - root;
            t1 = -b + root;
            return true;
        }

        private static bool Cylinder(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, float r, out float t0, out float t1)
        {
            t0 = t1 = 0f;
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-5f) return false;
            Vector3 n = axis / length;
            Vector3 m = origin - a;
            Vector3 dp = dir - Vector3.Dot(dir, n) * n;
            Vector3 mp = m - Vector3.Dot(m, n) * n;

            float qa = dp.sqrMagnitude;
            float qb = 2f * Vector3.Dot(dp, mp);
            float qc = mp.sqrMagnitude - r * r;
            float lo, hi;
            if (qa < 1e-10f)
            {
                if (qc > 0f) return false;   // parallel to the axis, outside
                lo = float.NegativeInfinity;
                hi = float.PositiveInfinity;
            }
            else
            {
                float disc = qb * qb - 4f * qa * qc;
                if (disc < 0f) return false;
                float root = Mathf.Sqrt(disc);
                lo = (-qb - root) / (2f * qa);
                hi = (-qb + root) / (2f * qa);
            }

            // Between the two end caps' planes.
            float dn = Vector3.Dot(dir, n);
            float mn = Vector3.Dot(m, n);
            if (Mathf.Abs(dn) < 1e-10f)
            {
                if (mn < 0f || mn > length) return false;
            }
            else
            {
                float s0 = -mn / dn;
                float s1 = (length - mn) / dn;
                if (s0 > s1)
                {
                    float swap = s0;
                    s0 = s1;
                    s1 = swap;
                }
                lo = Mathf.Max(lo, s0);
                hi = Mathf.Min(hi, s1);
            }
            if (lo > hi) return false;
            t0 = lo;
            t1 = hi;
            return true;
        }
    }
}

namespace ArmorFragments
{
    // Every shell fired (yours or a copy of another player's) passes through crew seats: CannonScript.Fire makes the
    // shell and keeps its colliders in _projectileInBarrelColliders.
    [HarmonyLib.HarmonyPatch(typeof(Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonScript), "Fire", new System.Type[0])]
    internal static class CrewShellPatch
    {
        private static readonly HarmonyLib.AccessTools.FieldRef<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonScript, UnityEngine.Collider[]> ShellColliders =
            HarmonyLib.AccessTools.FieldRefAccess<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonScript, UnityEngine.Collider[]>("_projectileInBarrelColliders");

        private static void Postfix(Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonScript __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;   // no shot (an empty rack, no crew to load)
            try
            {
                CrewBodies.PassSeats(ShellColliders(__instance));
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew: shell through seats failed: " + e.Message);
            }
        }
    }
}

namespace ArmorFragments
{
    // A shell passes crew seats and the crew have no colliders, so a shell that crosses a crew member without touching
    // anything solid first is caught here: each physics step, the stretch it flew since the last one is checked against
    // the crew bodies, and the first body crossed counts as the part it hit (CannonHitPatch.ResolvePart).
    [HarmonyLib.HarmonyPatch(typeof(Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript), "FixedUpdate")]
    internal static class CrewShellFlightPatch
    {
        private static readonly System.Collections.Generic.Dictionary<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript, UnityEngine.Vector3> Last =
            new System.Collections.Generic.Dictionary<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript, UnityEngine.Vector3>();

        private static readonly HarmonyLib.AccessTools.FieldRef<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript, UnityEngine.Vector3> Velocity =
            HarmonyLib.AccessTools.FieldRefAccess<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript, UnityEngine.Vector3>("_previousFrameVelocity");
        private static readonly HarmonyLib.AccessTools.FieldRef<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript, Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonScript> Cannon =
            HarmonyLib.AccessTools.FieldRefAccess<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript, Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonScript>("_cannonScript");

        private static void Postfix(Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript __instance)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.IsDead || __instance.HasExploded || !CrewBodies.Any)
                {
                    Last.Remove(__instance);
                    return;
                }

                UnityEngine.Vector3 now = __instance.transform.position;
                UnityEngine.Vector3 before;
                bool had = Last.TryGetValue(__instance, out before);
                if (Last.Count > 512) Forget();
                Last[__instance] = now;
                if (!had) return;

                Assets.Scripts.Craft.Parts.PartScript seat;
                UnityEngine.Vector3 point;
                UnityEngine.Vector3 normal;
                if (!CrewBodies.FirstCrossed(before, now, out seat, out point, out normal)) return;

                UnityEngine.Vector3 velocity = Velocity(__instance);
                if (velocity.sqrMagnitude < 1f) velocity = (now - before) / UnityEngine.Mathf.Max(UnityEngine.Time.fixedDeltaTime, 1e-4f);
                Last.Remove(__instance);
                if (CannonHitPatch.ResolvePart(__instance, seat, point, normal, velocity, Cannon(__instance)))
                    CannonHitPatch.Retire(__instance);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew: shell through a crew member failed: " + e.Message);
            }
        }

        // The game is about to resolve the shell hitting something at 'contact': did it cross a crew member on the way
        // there since the last physics step (CannonHitPatch)?
        internal static bool BodyBefore(Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript shell,
                                        UnityEngine.Vector3 contact, out Assets.Scripts.Craft.Parts.PartScript seat,
                                        out UnityEngine.Vector3 point, out UnityEngine.Vector3 normal)
        {
            seat = null;
            point = contact;
            normal = UnityEngine.Vector3.zero;
            UnityEngine.Vector3 before;
            if (shell == null || !CrewBodies.Any || !Last.TryGetValue(shell, out before)) return false;
            return CrewBodies.FirstCrossed(before, contact, out seat, out point, out normal);
        }

        private static void Forget()
        {
            System.Collections.Generic.List<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript> gone =
                new System.Collections.Generic.List<Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript>();
            foreach (Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript shell in Last.Keys)
                if (shell == null || shell.IsDead) gone.Add(shell);
            foreach (Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonProjectileScript shell in gone) Last.Remove(shell);
            if (Last.Count > 512) Last.Clear();
        }
    }
}
