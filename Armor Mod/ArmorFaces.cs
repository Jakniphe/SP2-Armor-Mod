using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // The six faces of a fuselage block, in the block's own frame: Front and Back are its ends (the game's front
    // and rear sections), the other four are its sides as seen looking toward the front with the block's top up.
    internal enum BlockFace
    {
        Front = 0,
        Back = 1,
        Left = 2,
        Right = 3,
        Top = 4,
        Bottom = 5,
    }

    // Per-face armor: which face of a block a point of its surface belongs to, and where a line enters and leaves
    // a block.
    //
    // A face is found from the block's real shape (the game's front, rear and, on a curved block, middle sections,
    // its offset and taper), so a
    // sloped, tapered or cut block still gets the face the player sees:
    //  - each face has an outward direction at every depth (the side walls tilted by the taper and trapezium, the
    //    ends along the block), and a surface belongs to the face it points closest to: a cut or slice surface is
    //    one face across its whole width, a cut end stays the end up to 45 degrees of slope, and a rounded
    //    section splits where its surface turns past 45 degrees;
    //  - a section with a trapezium of 1 is a triangle: its slanted walls are left and right, the base is top or
    //    bottom, and the edge that shrank to a point has no area;
    //  - a cone narrows to a point: its surface is the four sides all the way to the tip, and it has a front only
    //    where the tip is cut off.
    // Parts with no fuselage shape (the fixed nose cones and blocks) take the face whose axis is closest to the
    // surface's normal.
    // The surface itself is the block's colliders, which in flight follow the visible shape. The inner walls of a
    // hollow fuselage belong to no face: the plate is the outer surface.
    internal static class ArmorFaces
    {
        public const int Count = 6;
        public const int Inner = -1;   // an inner wall of a hollow fuselage
        public const int HoleWall = Count;   // the wall of a hole carved into the block by a cutter with hole wall armor ([HOL])

        public static readonly string[] Keys = { "fr", "bk", "lt", "rt", "tp", "bm" };
        public static readonly string[] Names = { "Front", "Back", "Left", "Right", "Top", "Bottom", "Hole wall" };

        // The block's shape in its own frame.
        public struct Shape
        {
            public Transform Frame;
            public bool Fuselage;          // false: no fuselage data, faces from the frame's axes alone
            public Vector3 BackCenter;
            public Vector3 FrontCenter;
            public Vector2 BackHalf;       // half width, half height
            public Vector2 FrontHalf;
            public float BackTrapezium;
            public float FrontTrapezium;
            public bool Curved;            // a middle section bends the size between the ends (quadratic, as the game does)
            public Vector2 MidHalf;
            public float MidTrapezium;
            public float MidShift;         // the middle section sits (1 + MidShift) / 2 of the way from back to front
            public bool Hollow;

            // The curve parameter (0..1) at the depth that is 'along' (0..1) of the way from the back to the front.
            // Depth runs (1 + MidShift) t - MidShift t^2 along the curve, as the game's Bezier of section positions.
            public float CurveAt(float along)
            {
                float b = MidShift;
                if (!Curved || Mathf.Abs(b) < 0.0001f) return along;
                float root = Mathf.Sqrt(Mathf.Max((1f + b) * (1f + b) - 4f * b * along, 0f));
                return Mathf.Clamp01(((1f + b) - root) / (2f * b));
            }

            // How fast the depth grows along the curve at t, per 0..1 of the length (1 when evenly spread).
            public float DepthRateAt(float t)
            {
                if (!Curved) return 1f;
                return Mathf.Max(1f + MidShift - 2f * MidShift * t, 0.02f);
            }

            // Half size and trapezium at 0..1 from the back end to the front end.
            public Vector2 HalfAt(float t)
            {
                if (!Curved) return Vector2.Lerp(BackHalf, FrontHalf, t);
                float s = 1f - t;
                return s * s * BackHalf + 2f * s * t * MidHalf + t * t * FrontHalf;
            }

            public float TrapeziumAt(float t)
            {
                if (!Curved) return Mathf.Lerp(BackTrapezium, FrontTrapezium, t);
                float s = 1f - t;
                return s * s * BackTrapezium + 2f * s * t * MidTrapezium + t * t * FrontTrapezium;
            }

            // How fast the half size grows from back to front at t, per 0..1 of the length.
            public Vector2 HalfSlopeAt(float t)
            {
                if (!Curved) return FrontHalf - BackHalf;
                return 2f * (1f - t) * (MidHalf - BackHalf) + 2f * t * (FrontHalf - MidHalf);
            }
        }

        public static bool IsPerFace(PartScript part)
        {
            if (part == null || part.Part == null) return false;
            ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
            return armor != null && armor.PerFace;
        }

        public static Shape ShapeOf(PartScript part)
        {
            Shape shape = new Shape { Frame = part.transform };
            foreach (PartModifierData modifier in part.Part.Modifiers)
            {
                JFuselageData data = modifier as JFuselageData;
                if (data == null || data.NumSlices < 2) continue;

                if (data.JFuselageScript != null) shape.Frame = data.JFuselageScript.transform;
                int back = data.BackIndex;
                int front = data.FrontIndex;
                var backSection = data[back];
                var frontSection = data[front];

                shape.Fuselage = true;
                shape.BackCenter = data.Offset * data.Slices[back].OffsetCoefficient;
                shape.FrontCenter = data.Offset * data.Slices[front].OffsetCoefficient;
                shape.BackHalf = new Vector2(backSection.HalfSize.x, backSection.HalfSize.y);
                shape.FrontHalf = new Vector2(frontSection.HalfSize.x, frontSection.HalfSize.y);
                shape.BackTrapezium = backSection.Trapezium;
                shape.FrontTrapezium = frontSection.Trapezium;
                if (data.IsCone)
                {
                    // A cone narrows to a point at the front. Its front section only shapes the tip: the middle of
                    // the curve is that section blended with the halfway point by the nose roundness, and sits
                    // further forward the rounder the nose (as the game builds it).
                    float roundness = Mathf.Clamp01(data.NoseconeRoundness);
                    shape.Curved = true;
                    shape.FrontHalf = Vector2.zero;
                    shape.FrontTrapezium = 0f;
                    shape.MidHalf = Vector2.Lerp(shape.BackHalf * 0.5f,
                        new Vector2(frontSection.HalfSize.x, frontSection.HalfSize.y), roundness);
                    shape.MidTrapezium = Mathf.Lerp(shape.BackTrapezium * 0.5f, frontSection.Trapezium, roundness);
                    shape.MidShift = roundness;
                }
                else if (data.NumSlices == 3)
                {
                    var middle = data[1];
                    shape.Curved = true;
                    shape.MidHalf = new Vector2(middle.HalfSize.x, middle.HalfSize.y);
                    shape.MidTrapezium = middle.Trapezium;
                }
                shape.Hollow = data.IsHollow;
                break;
            }
            return shape;
        }

        public static int Classify(PartScript part, Vector3 worldPoint, Vector3 worldNormal)
        {
            return Classify(ShapeOf(part), worldPoint, worldNormal);
        }

        // The face (BlockFace) that the surface at worldPoint, with outward normal worldNormal, belongs to;
        // Inner for an inner wall of a hollow fuselage.
        public static int Classify(Shape shape, Vector3 worldPoint, Vector3 worldNormal)
        {
            Vector3 p = shape.Frame.InverseTransformPoint(worldPoint);
            Vector3 n = shape.Frame.InverseTransformDirection(worldNormal).normalized;

            float length = shape.FrontCenter.z - shape.BackCenter.z;
            if (!shape.Fuselage || Mathf.Abs(length) < 0.0001f) return ByAxes(n);
            float forward = length > 0f ? 1f : -1f;   // the front end is toward +z (1) or -z (-1)

            // The section at this depth: its center, half size and trapezium.
            float along = Mathf.Clamp01((p.z - shape.BackCenter.z) / length);
            float t = shape.CurveAt(along);
            Vector2 center = Vector2.Lerp(new Vector2(shape.BackCenter.x, shape.BackCenter.y),
                                          new Vector2(shape.FrontCenter.x, shape.FrontCenter.y), along);
            Vector2 half = shape.HalfAt(t);
            half.x = Mathf.Max(half.x, 0.0001f);
            half.y = Mathf.Max(half.y, 0.0001f);
            float trapezium = shape.TrapeziumAt(t);
            Vector2 halfSlope = shape.HalfSlopeAt(t) / shape.DepthRateAt(t);   // per 0..1 of the length

            float dx = p.x - center.x;
            float dy = p.y - center.y;
            float v = dy / half.y;

            // A hollow fuselage's inner wall faces the block's axis.
            if (shape.Hollow)
            {
                Vector2 radial = new Vector2(dx / half.x, dy / half.y);
                Vector2 across = new Vector2(n.x, n.y);
                if (radial.sqrMagnitude > 0.000001f && across.sqrMagnitude > 0.04f &&
                    Vector2.Dot(radial.normalized, across.normalized) < -0.3f)
                    return Inner;
            }

            // Each face's outward direction at this depth: the four side walls tilted by the block's taper (and the
            // left and right walls leaning in with the trapezium; at a trapezium of 1 they meet as a triangle), and
            // the two ends. The surface belongs to the face it points closest to, so a slice or cut surface is one
            // face across its whole width, wherever it lies in the block.
            float widthAtHeight = 1f + Mathf.Clamp(v, -1f, 1f) * trapezium;
            float lean = -half.x * trapezium / half.y;

            int best = -1;
            float bestDot = float.NegativeInfinity;
            for (int s = -1; s <= 1; s += 2)
            {
                float slopeX = ((shape.FrontCenter.x - shape.BackCenter.x) + s * halfSlope.x * widthAtHeight) / length;
                Vector3 xWall = new Vector3(s, lean, -s * slopeX).normalized;
                int xFace = ((s > 0) == (forward > 0f)) ? (int)BlockFace.Right : (int)BlockFace.Left;
                Closer(Vector3.Dot(n, xWall), xFace, ref best, ref bestDot);

                float slopeY = ((shape.FrontCenter.y - shape.BackCenter.y) + s * halfSlope.y) / length;
                Vector3 yWall = new Vector3(0f, s, -s * slopeY).normalized;
                Closer(Vector3.Dot(n, yWall), s > 0 ? (int)BlockFace.Top : (int)BlockFace.Bottom, ref best, ref bestDot);
            }
            Closer(n.z * forward, (int)BlockFace.Front, ref best, ref bestDot);
            Closer(-n.z * forward, (int)BlockFace.Back, ref best, ref bestDot);
            return best;
        }

        private static void Closer(float dot, int face, ref int best, ref float bestDot)
        {
            if (dot > bestDot)
            {
                bestDot = dot;
                best = face;
            }
        }

        // No fuselage data: the frame's axis closest to the normal (+z front, +y top, +x right).
        private static int ByAxes(Vector3 n)
        {
            float x = Mathf.Abs(n.x), y = Mathf.Abs(n.y), z = Mathf.Abs(n.z);
            if (z >= x && z >= y) return n.z >= 0f ? (int)BlockFace.Front : (int)BlockFace.Back;
            if (y >= x) return n.y >= 0f ? (int)BlockFace.Top : (int)BlockFace.Bottom;
            return n.x >= 0f ? (int)BlockFace.Right : (int)BlockFace.Left;
        }

        // Where a line meets the outer surface of a per-face block.
        public struct Boundary
        {
            public float Distance;   // along the line from its origin
            public Vector3 Point;
            public Vector3 Normal;   // outward
            public bool Leaving;     // the line leaves the block here (false: it gets in)
            public int Face;
            public float Mm;         // that face's thickness (0 = open)
        }

        private struct Span
        {
            public float Enter, Leave;
            public bool Entered;     // false: the line starts inside
            public Vector3 EnterPoint, EnterNormal, LeavePoint, LeaveNormal;
        }

        private static readonly List<Span> Spans = new List<Span>();

        // Every point within maxDistance where the line from origin along dir gets into or out of the block, for
        // each of the block's colliders (one forward cast for the way in, one cast back from beyond for the way
        // out). Touching or overlapping colliders count as one solid, and inner walls of a hollow block are left out.
        public static void Boundaries(PartScript part, ArmorTag armor, Vector3 origin, Vector3 dir, float maxDistance,
                                      List<Boundary> into)
        {
            Shape shape = ShapeOf(part);
            Spans.Clear();
            Ray forward = new Ray(origin, dir);

            foreach (Collider collider in PartPhysics.Colliders(part))
            {
                if (collider == null) continue;
                float far = maxDistance + collider.bounds.size.magnitude + 0.05f;
                RaycastHit backHit;
                if (!collider.Raycast(new Ray(origin + dir * far, -dir), out backHit, far + 0.05f)) continue;
                float leave = far - backHit.distance;
                if (leave <= 0f) continue;   // behind the origin

                RaycastHit frontHit;
                bool entered = collider.Raycast(forward, out frontHit, far);
                float enter = entered ? frontHit.distance : 0f;
                if (enter > maxDistance || leave <= enter) continue;

                Spans.Add(new Span
                {
                    Enter = enter, Leave = leave, Entered = entered,
                    EnterPoint = entered ? frontHit.point : origin, EnterNormal = entered ? frontHit.normal : -dir,
                    LeavePoint = backHit.point, LeaveNormal = backHit.normal,
                });
            }
            if (Spans.Count == 0) return;

            Spans.Sort((a, b) => a.Enter.CompareTo(b.Enter));
            Span current = Spans[0];
            for (int i = 1; i <= Spans.Count; i++)
            {
                if (i < Spans.Count && Spans[i].Enter <= current.Leave + 0.001f)
                {
                    if (Spans[i].Leave > current.Leave)
                    {
                        current.Leave = Spans[i].Leave;
                        current.LeavePoint = Spans[i].LeavePoint;
                        current.LeaveNormal = Spans[i].LeaveNormal;
                    }
                    continue;
                }

                if (current.Entered)
                    Add(into, part, shape, armor, dir, current.Enter, current.EnterPoint, current.EnterNormal, false);
                if (current.Leave <= maxDistance)
                    Add(into, part, shape, armor, dir, current.Leave, current.LeavePoint, current.LeaveNormal, true);
                if (i < Spans.Count) current = Spans[i];
            }

            // Armored walls of holes carved into the block ([HOL] on the cutter): plates the line crosses inside it.
            ArmorSurface.WallBoundaries(part, origin, dir, maxDistance, into);
        }

        // An armored face is open where the line goes through a hole carved into the block (ArmorSurface).
        private static void Add(List<Boundary> into, PartScript part, Shape shape, ArmorTag armor, Vector3 dir,
                                float distance, Vector3 point, Vector3 normal, bool leaving)
        {
            int face = Classify(shape, point, normal);
            if (face == Inner) return;
            float mm = armor.Faces != null && face < armor.Faces.Length ? armor.Faces[face] : 0f;
            if (mm > 0f && !ArmorSurface.SurfaceAt(part, point, dir)) mm = 0f;
            into.Add(new Boundary
            {
                Distance = distance, Point = point, Normal = normal, Leaving = leaving,
                Face = face, Mm = mm,
            });
        }
    }

    // One thing a straight line meets.
    internal struct RayEvent
    {
        public float Distance;   // along the line from its origin (slightly negative: touching the origin)
        public Vector3 Point;
        public Vector3 Normal;   // the surface's outward normal
        public PartScript Part;  // null: not a part (the ground...)
        public Collider Collider;
        public ArmorTag Armor;  // the part's armor tag (whole block or per face), null if none
        public bool Face;        // a face of a per-face block (Leaving, FaceIndex and Mm say which)
        public bool Leaving;
        public int FaceIndex;
        public float Mm;

        // An open face: empty space, the line just passes.
        public bool Open
        {
            get { return Face && Mm <= 0f; }
        }

        public RaycastHit AsHit()
        {
            RaycastHit hit = new RaycastHit();
            hit.point = Point;
            hit.normal = Normal;
            hit.distance = Mathf.Max(Distance, 0f);
            return hit;
        }
    }

    internal static class RayEvents
    {
        // The line is cast from a little behind its origin, so a surface right at the origin (a part touching the
        // plate a shell has just gone through) is seen.
        private const float BackOff = 0.05f;
        public const float Touch = 0.002f;

        private static readonly List<ArmorFaces.Boundary> Boundaries = new List<ArmorFaces.Boundary>();
        private static readonly Dictionary<PartScript, int> FirstOf = new Dictionary<PartScript, int>();
        private static readonly Dictionary<PartScript, ArmorTag> PerFace = new Dictionary<PartScript, ArmorTag>();

        // Everything the line from origin along dir meets within maxDistance, nearest first:
        //  - anything that is not a part;
        //  - each ordinary part and each whole-block armor part where the line first gets into it;
        //  - every place where the line gets into or out of a per-face block (open faces included), also for a
        //    block the origin is inside of.
        // A surface within Touch behind the origin still counts. 'ignore' is left out altogether; 'resolved' (a
        // per-face block whose face was just worked out at the origin) loses its surfaces within Touch of it.
        public static List<RayEvent> Gather(Vector3 origin, Vector3 dir, float maxDistance, PartScript ignore,
                                            PartScript resolved, List<RayEvent> into)
        {
            into.Clear();
            FirstOf.Clear();
            PerFace.Clear();
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            Vector3 start = origin - dir * BackOff;

            foreach (RaycastHit hit in Physics.RaycastAll(start, dir, maxDistance + BackOff, mask, QueryTriggerInteraction.Ignore))
            {
                Collider collider = hit.collider;
                if (collider == null || collider.GetComponentInParent<CannonProjectileScript>() != null) continue;
                float distance = hit.distance - BackOff;
                PartScript part = collider.GetComponentInParent<PartScript>();

                if (part == null)
                {
                    if (distance >= -Touch)
                        into.Add(new RayEvent { Distance = distance, Point = hit.point, Normal = hit.normal, Collider = collider });
                    continue;
                }
                if (part == ignore || part.Part == null) continue;
                if (CrewBodies.IsBody(part)) continue;   // a crew seat: the body sitting in it is met instead (below)

                ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
                if (armor != null && armor.PerFace)
                {
                    PerFace[part] = armor;
                    continue;
                }
                if (distance < -Touch) continue;

                RayEvent e = new RayEvent
                {
                    Distance = distance, Point = hit.point, Normal = hit.normal, Part = part, Collider = collider, Armor = armor,
                };
                int index;
                if (FirstOf.TryGetValue(part, out index))
                {
                    if (distance < into[index].Distance) into[index] = e;   // another collider of it, nearer
                }
                else
                {
                    FirstOf[part] = into.Count;
                    into.Add(e);
                }
            }

            // Per-face blocks the origin is inside of (the line never enters them).
            foreach (Collider collider in Physics.OverlapSphere(origin, 0.005f, mask, QueryTriggerInteraction.Ignore))
            {
                if (collider == null || collider.GetComponentInParent<CannonProjectileScript>() != null) continue;
                PartScript part = collider.GetComponentInParent<PartScript>();
                if (part == null || part == ignore || part.Part == null || PerFace.ContainsKey(part)) continue;
                ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
                if (armor != null && armor.PerFace) PerFace[part] = armor;
            }

            foreach (KeyValuePair<PartScript, ArmorTag> block in PerFace)
            {
                Boundaries.Clear();
                ArmorFaces.Boundaries(block.Key, block.Value, start, dir, maxDistance + BackOff, Boundaries);
                foreach (ArmorFaces.Boundary b in Boundaries)
                {
                    float distance = b.Distance - BackOff;
                    if (distance < -Touch) continue;
                    if (block.Key == resolved && distance < Touch) continue;
                    into.Add(new RayEvent
                    {
                        Distance = distance, Point = b.Point, Normal = b.Normal, Part = block.Key, Armor = block.Value,
                        Face = true, Leaving = b.Leaving, FaceIndex = b.Face, Mm = b.Mm,
                    });
                }
            }

            // Crew bodies (CrewBodies): where the line first gets into each.
            CrewBodies.AddEvents(start, dir, maxDistance + BackOff, BackOff, Touch, ignore, into);

            // Nearest first; where two surfaces touch, the way out of one block before the way into the next.
            into.Sort((a, b) => SortKey(a).CompareTo(SortKey(b)));
            return into;
        }

        private static float SortKey(RayEvent e)
        {
            return e.Leaving ? e.Distance - 0.0005f : e.Distance;
        }
    }
}
