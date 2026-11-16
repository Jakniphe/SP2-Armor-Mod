using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using UnityEngine;

namespace ArmorFragments
{
    // Where the rounds sit inside one ammo rack, and which of them are still there.
    //
    // The rack's frame: Z the direction the rounds lie in, X and Y across them; measured in the part's rotation
    // only (no scale), in meters. Across, a grid with a pitch of one round diameter plus spacing: square, or
    // staggered (every other layer nested half a pitch into the gaps of the one below, the layers sqrt(3)/2 of a
    // pitch apart). Each row of that grid is packed along Z on its own, with as many whole rounds as fit where
    // the row's circle is inside the shape.
    //
    // The layout is searched (Search): the rounds may lie along the fuselage, across it, upright, or along any
    // of the six 45-degree diagonals between those; the cross grid may be square to the part or turned 45
    // degrees about the rounds; square or staggered; centered or shifted. The layout that holds the most rounds
    // wins. The lengthwise, square, centered layout is tried first and only a layout that holds more replaces
    // it. The winner is remembered per shape and round (LayoutCache), so measuring again is one fill.
    //
    // Rounds are solid, not liquid: a round counts only if the whole cylinder is inside the part's shape,
    // read from its drawn mesh (FillFromMesh; the colliders are only a fallback, lengthwise and square only). A
    // tapered, rounded or shifted fuselage loses the rounds that would poke out, and a rack shorter than one
    // round in every direction holds none.
    //
    // Rounds are taken from the top down, so the filled part shrinks from the top: a hit only counts if its
    // line crosses a cell that still holds a round (HitsRounds). The real colliders never change.
    internal sealed class RackGrid
    {
        private struct Round
        {
            public Vector3 Center;   // frame coordinates, middle of the round
            public int Slot, Column, Layer;
        }

        // A grid bigger than this is not counted (it would take too long): tiny rounds in a huge rack.
        private const int MaxCandidates = 60000;
        private const float RingTolerance = 0.97f;   // colliders are faceted: test the ring slightly inside

        public int Slots, Columns, Layers;   // grid size before the shape check
        public string Problem;               // why the rack holds nothing (null = it was measured)
        public string Status = "OK";         // the same, in a few words (designer)
        public float InsideLength, InsideWidth, InsideHeight;   // the part's extent along / across / up (m)
        public string Orientation = "-";     // how the rounds lie, in a few words (designer, log)

        // Grid cells the shape cuts off (tapers, rounded corners...).
        public int LostToShape
        {
            get { return Problem == null || rounds.Count == 0 ? Mathf.Max(0, Slots * Columns * Layers - rounds.Count) : 0; }
        }

        public int Capacity
        {
            get { return rounds.Count; }
        }

        public int Remaining
        {
            get { return rounds.Count - taken; }
        }

        private Vector3 baseX, baseY, baseZ;   // the part's across, up and along (the fuselage's length)
        private Vector3 axisX, axisY, axisZ;   // the chosen frame
        private float radius, diameter, pitch, layerPitch, length;
        private bool staggered;
        private float caseLength, shellRadius;   // for drawing: the case, then the narrower shell in front
        private float firstX, firstY;            // center of column 0 / layer 0 (layer 0 unshifted)
        private Vector3 boundsMin, boundsMax;    // frame coordinates
        private Vector3 centerLocal;             // middle of the part's shape, part rotation, meters

        private readonly List<Round> rounds = new List<Round>();
        private readonly Dictionary<long, List<int>> rows = new Dictionary<long, List<int>>();   // (column, layer) -> rounds
        private bool[] present = new bool[0];
        private int[] removalOrder = new int[0];
        private int taken;

        // ---- Measuring ---------------------------------------------------------------

        // Is a part being dragged in the designer? Racks aren't re-measured then (a fuselage may reshape live
        // while something attached to it moves); they catch up once it is let go.
        public static bool DesignerDragging()
        {
            try
            {
                Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
                return designer != null && designer.Tools != null && designer.Tools.MovePartTool != null &&
                       designer.Tools.MovePartTool.IsManipulatingPart;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        public static RackGrid Measure(PartScript part, AmmoRound round)
        {
            RackGrid grid = new RackGrid();
            grid.Problem = grid.Fill(part, round);
            if (grid.Problem != null)
            {
                grid.rounds.Clear();
                grid.rows.Clear();
            }
            grid.present = new bool[grid.rounds.Count];
            for (int i = 0; i < grid.present.Length; i++) grid.present[i] = true;
            grid.BuildRemovalOrder();
            grid.LoadToFullness(part);
            return grid;
        }

        // The rack's Fullness (f=, percent): the rounds above it are taken out, from the top, as if already fired.
        // Capacity stays what the shape holds; Remaining is what is loaded.
        public float FullnessPercent = 100f;

        private void LoadToFullness(PartScript part)
        {
            AmmoRackTag tag = part != null && part.Part != null ? TagCodec.Parse(part.Part.Name).AmmoRack : null;
            FullnessPercent = tag != null && tag.Fill.HasValue ? Mathf.Clamp(tag.Fill.Value, 0f, 100f) : 100f;
            int loaded = Mathf.RoundToInt(rounds.Count * FullnessPercent / 100f);
            while (Remaining > loaded && TakeRound()) { }
        }

        // Returns null if measured, or the reason the rack can hold nothing.
        private string Fill(PartScript part, AmmoRound round)
        {
            if (part == null || part.Part == null) return Fail("Part is gone", "the part is gone");

            JFuselageData fuselage = null;
            foreach (PartModifierData modifier in part.Part.Modifiers)
            {
                fuselage = modifier as JFuselageData;
                if (fuselage != null) break;
            }
            if (fuselage == null) return Fail("Not a fuselage", "only fuselages can be ammo racks");
            if (fuselage.IsHollow) return Fail("Hollow: holds nothing", "hollow fuselages can't hold rounds");
            // A cut fuselage is packed to its cut shape, like any other: the mesh below is the block as drawn.
            if (round.Diameter <= 0f || round.Length <= 0f)
                return Fail("No cannon draws from it", "no cannon draws from this rack");

            // The part's own axes: Z along the fuselage (JFuselageData.Offset places its slices), Y up, X across.
            Vector3 offset = fuselage.Offset;
            baseZ = offset.sqrMagnitude > 1e-8f ? offset.normalized : Vector3.forward;
            baseX = Vector3.ProjectOnPlane(Vector3.right, baseZ);
            if (baseX.sqrMagnitude < 1e-6f) baseX = Vector3.ProjectOnPlane(Vector3.up, baseZ);
            baseX.Normalize();
            baseY = Vector3.Cross(baseZ, baseX).normalized;

            radius = round.Diameter * 0.5f;
            diameter = round.Diameter;
            length = round.Length;
            caseLength = round.CaseLength;
            shellRadius = round.CaliberMm / 2000f;
            pitch = round.Diameter * (1f + ArmorFragmentsPlugin.RoundSpacingPercent.Value / 100f);

            // The shape: the fuselage's own drawn mesh (exact for any shape). The colliders are only a
            // fallback: in the designer a fuselage's colliders can be a plain box around it, which would make
            // every cell of the box look "inside".
            bool useMesh = GatherMesh(part);
            List<Collider> colliders = null;
            if (!useMesh)
            {
                colliders = Colliders(part);
                if (colliders.Count == 0) return Fail("Can't measure shape", NoColliders);
            }

            // The part's own extent (read-outs) and middle (where the explosion goes), in its own axes.
            SetFrame(0);
            if (useMesh)
            {
                BuildTriangles(0);
            }
            else if (!Extent(part, colliders))
            {
                return Fail("Can't measure shape", NoColliders);
            }
            InsideLength = boundsMax.z - boundsMin.z;
            InsideWidth = boundsMax.x - boundsMin.x;
            InsideHeight = boundsMax.y - boundsMin.y;
            Vector3 middle = (boundsMin + boundsMax) * 0.5f;
            centerLocal = axisX * middle.x + axisY * middle.y + axisZ * middle.z;

            if (useMesh)
            {
                Method = "mesh";
                Layout best = Choose(part, fuselage);
                if (Evaluate(best, -1) <= 0) return BaselineProblem();
            }
            else
            {
                // Colliders: the lengthwise, square, centered layout only (each round is tested point by point).
                Method = "colliders";
                Layout baseline = new Layout();
                SetLayout(baseline);
                if (!GridFor(baseline)) return BaselineProblem();
                int alongSamples = Mathf.Max(2, Mathf.CeilToInt(length / 0.15f) + 1);
                for (int slot = 0; slot < Slots; slot++)
                    for (int column = 0; column < Columns; column++)
                        for (int layer = 0; layer < Layers; layer++)
                        {
                            Vector3 center = new Vector3(ColumnX(column, layer), LayerY(layer),
                                                         boundsMin.z + (InsideLength - Slots * length) * 0.5f + (slot + 0.5f) * length);
                            if (!RoundFits(part.transform, colliders, center, alongSamples)) continue;
                            AddRound(center, column, layer);
                        }
                if (rounds.Count == 0) return BaselineProblem();
                Orientation = Describe(baseline);
            }
            Status = "OK";
            return null;
        }

        // Why nothing fits, told for the plain lengthwise layout (the one the player expects).
        private string BaselineProblem()
        {
            rounds.Clear();
            rows.Clear();
            Layout baseline = new Layout();
            SetLayout(baseline);
            if (triangles != null && builtFrame != 0) BuildTriangles(0);
            GridFor(baseline);
            float span = boundsMax.z - boundsMin.z, width = boundsMax.x - boundsMin.x, height = boundsMax.y - boundsMin.y;
            if (Slots == 0)
                return Fail("Too short", "too short: a round needs " + Cm(length) + ", the rack is " + Cm(span) + " long");
            if (Columns == 0 || Layers == 0)
                return Fail("Too narrow", "too narrow: a round needs " + Cm(diameter) + ", the rack is " +
                            Cm(width) + " x " + Cm(height));
            if ((long)Slots * Columns * Layers > MaxCandidates)
                return Fail("Too many to count", "too many rounds to count (over " + MaxCandidates + ")");
            return Fail("Shape too tight", "the shape leaves no room for a whole round in any direction (" + Slots +
                        " slots x " + Columns + " x " + Layers + " grid lengthwise)");
        }

        public const string NoColliders = "no mesh or colliders to measure the shape";

        public string Method = "-";   // "mesh" or "colliders": what the shape was measured from

        private void AddRound(Vector3 center, int column, int layer)
        {
            List<int> row;
            long key = RowKey(column, layer);
            if (!rows.TryGetValue(key, out row))
            {
                row = new List<int>();
                rows[key] = row;
            }
            row.Add(rounds.Count);
            rounds.Add(new Round { Center = center, Slot = row.Count - 1, Column = column, Layer = layer });
        }

        private static long RowKey(int column, int layer)
        {
            return (long)column * 65536L + layer;
        }

        // ---- Layouts -------------------------------------------------------------------

        private struct Layout
        {
            public int Frame;          // direction x 2 + 1 if the cross grid is turned 45 degrees
            public bool Staggered;
            public int ShiftX, ShiftY; // 0 = centered, 1 = flush with the low side, 2 = flush plus half a pitch
        }

        private const int FrameCount = 18;   // 9 directions x (square, turned)
        private const float Half = 0.70710678f;

        private static readonly string[] DirectionNames =
        {
            "lengthwise", "across", "upright",
            "diagonal (length/across)", "diagonal (length/across)", "diagonal (length/up)", "diagonal (length/up)",
            "diagonal (across/up)", "diagonal (across/up)",
        };

        private static string Describe(Layout layout)
        {
            return DirectionNames[layout.Frame / 2] + ((layout.Frame & 1) == 1 ? ", rows turned 45 deg" : "") +
                   (layout.Staggered ? ", staggered" : "");
        }

        // The frame of a layout: Z along the rounds, then X and Y across them.
        private void SetFrame(int frame)
        {
            Vector3 l = baseZ, a = baseX, u = baseY;
            Vector3 z;
            switch (frame / 2)
            {
                case 0: z = l; break;
                case 1: z = a; break;
                case 2: z = u; break;
                case 3: z = (l + a) * Half; break;
                case 4: z = (l - a) * Half; break;
                case 5: z = (l + u) * Half; break;
                case 6: z = (l - u) * Half; break;
                case 7: z = (a + u) * Half; break;
                default: z = (a - u) * Half; break;
            }
            z.Normalize();
            Vector3 x = Vector3.ProjectOnPlane(a, z);
            if (x.sqrMagnitude < 1e-4f) x = Vector3.ProjectOnPlane(l, z);
            x.Normalize();
            Vector3 y = Vector3.Cross(z, x).normalized;
            if ((frame & 1) == 1)
            {
                x = (x + y).normalized;
                y = Vector3.Cross(z, x).normalized;
            }
            axisX = x;
            axisY = y;
            axisZ = z;
        }

        private void SetLayout(Layout layout)
        {
            SetFrame(layout.Frame);
            staggered = layout.Staggered;
            layerPitch = staggered ? pitch * 0.8660254f : pitch;
        }

        private float RowShift(int layer)
        {
            return staggered && (layer & 1) == 1 ? pitch * 0.5f : 0f;
        }

        private float ColumnX(int column, int layer)
        {
            return firstX + RowShift(layer) + column * pitch;
        }

        private float LayerY(int layer)
        {
            return firstY + layer * layerPitch;
        }

        // The grid of a layout in the current bounds. False if no round fits the box or there are too many.
        private bool GridFor(Layout layout)
        {
            float width = boundsMax.x - boundsMin.x;
            float height = boundsMax.y - boundsMin.y;
            float span = boundsMax.z - boundsMin.z;

            Slots = Mathf.FloorToInt(span / length + 1e-4f);
            Columns = width >= diameter ? Mathf.FloorToInt((width - diameter) / pitch + 1e-4f) + 1 : 0;
            Layers = height >= diameter ? Mathf.FloorToInt((height - diameter) / layerPitch + 1e-4f) + 1 : 0;
            if (Slots == 0 || Columns == 0 || Layers == 0) return false;
            if ((long)Slots * Columns * Layers > MaxCandidates) return false;

            float slackX = width - ((Columns - 1) * pitch + diameter);
            if (staggered && Layers > 1 && slackX >= pitch * 0.5f) slackX -= pitch * 0.5f;   // room for the nested rows
            float slackY = height - ((Layers - 1) * layerPitch + diameter);
            firstX = boundsMin.x + radius + Shift(layout.ShiftX, slackX);
            firstY = boundsMin.y + radius + Shift(layout.ShiftY, slackY);
            return true;
        }

        private float Shift(int mode, float slack)
        {
            switch (mode)
            {
                case 1: return 0f;
                case 2: return pitch * 0.5f;
                default: return Mathf.Max(slack, 0f) * 0.5f;
            }
        }

        // Fills the rounds of one layout (mesh). Returns how many fit, or -1 without filling when even a full
        // box couldn't beat 'toBeat'.
        private int Evaluate(Layout layout, int toBeat)
        {
            rounds.Clear();
            rows.Clear();
            SetLayout(layout);
            BuildTriangles(layout.Frame);
            if (!GridFor(layout)) return 0;
            if ((long)Slots * Columns * Layers <= toBeat) return -1;
            FillFromMesh();
            Orientation = Describe(layout);
            return rounds.Count;
        }

        // The layout that holds the most rounds (remembered per shape and round).
        private static readonly Dictionary<string, Layout> LayoutCache = new Dictionary<string, Layout>();
        private const int LayoutCacheLimit = 512;

        private Layout Choose(PartScript part, JFuselageData fuselage)
        {
            string key = GeometryKey(part, fuselage);
            Layout cached;
            if (LayoutCache.TryGetValue(key, out cached)) return cached;

            Layout best = Search();
            if (LayoutCache.Count >= LayoutCacheLimit) LayoutCache.Clear();
            LayoutCache[key] = best;
            return best;
        }

        // 1. Every direction and turn, square and staggered, centered (36 layouts; the plain lengthwise one
        //    first, and a layout replaces the best only with more rounds).
        // 2. The two best of those are tried at every shift (8 more each).
        private Layout Search()
        {
            List<Layout> tried = new List<Layout>();
            List<int> counts = new List<int>();

            Layout best = new Layout();
            int bestCount = Evaluate(best, -1);
            tried.Add(best);
            counts.Add(bestCount);

            for (int frame = 0; frame < FrameCount; frame++)
            {
                for (int pattern = 0; pattern < 2; pattern++)
                {
                    if (frame == 0 && pattern == 0) continue;
                    Layout layout = new Layout { Frame = frame, Staggered = pattern == 1 };
                    int count = Evaluate(layout, bestCount);
                    tried.Add(layout);
                    counts.Add(count);
                    if (count > bestCount)
                    {
                        bestCount = count;
                        best = layout;
                    }
                }
            }

            // The two best centered layouts (earlier first on a tie).
            int first = -1, second = -1;
            for (int i = 0; i < counts.Count; i++)
            {
                if (counts[i] <= 0) continue;
                if (first < 0 || counts[i] > counts[first]) { second = first; first = i; }
                else if (second < 0 || counts[i] > counts[second]) second = i;
            }

            foreach (int index in new[] { first, second })
            {
                if (index < 0) continue;
                for (int sx = 0; sx < 3; sx++)
                    for (int sy = 0; sy < 3; sy++)
                    {
                        if (sx == 0 && sy == 0) continue;
                        Layout layout = tried[index];
                        layout.ShiftX = sx;
                        layout.ShiftY = sy;
                        int count = Evaluate(layout, bestCount);
                        if (count > bestCount)
                        {
                            bestCount = count;
                            best = layout;
                        }
                    }
            }

            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack layout: " + Describe(best) + " holds " + Mathf.Max(bestCount, 0) +
                " rounds (lengthwise, square: " + Mathf.Max(counts[0], 0) + ")");
            return best;
        }

        // The shape and the round: the fuselage's own settings (its saved state: sections, offset, corners...), its
        // part type, its mesh's triangle count (a carving part changes that) and the round's size. Moving or turning
        // the part doesn't change the key, so dragging parts around never starts a new search.
        private string GeometryKey(PartScript part, JFuselageData fuselage)
        {
            string state;
            try
            {
                System.Xml.Linq.XElement xml = fuselage.GenerateStateXml();
                state = xml != null ? xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting) : "";
            }
            catch (System.Exception)
            {
                state = "";
            }
            System.Text.StringBuilder key = new System.Text.StringBuilder();
            key.Append(part.Part.PartType != null ? part.Part.PartType.Name : "").Append('|').Append(state)
               .Append('|').Append(localVertices.Count / 3)
               .Append('|').Append(Mathf.RoundToInt(diameter * 10000f)).Append('/').Append(Mathf.RoundToInt(length * 10000f))
               .Append('/').Append(Mathf.RoundToInt(pitch * 10000f));
            return key.ToString();
        }

        // ---- Shape from the drawn mesh -------------------------------------------------------
        //
        // The fuselage's triangles, in the rack's frame. A round fits if, along its whole length, its circle
        // lies inside the outline of the mesh cut at that depth. The outline is read with lines across the
        // circle (5 per round: center, +-0.7 r, +-0.95 r): on each line the circle's chord must sit inside one
        // "inside" stretch of the outline.
        //
        // Inside = nonzero winding: each cut segment is oriented by its triangle's facing (outward normal on
        // its right), and a point is inside when the segments around it don't cancel out. All of the part's
        // meshes count together. Unlike the even-odd rule per mesh, nonzero winding isn't broken by meshes split
        // in pieces, doubled faces (same facing) or mirrored parts.

        private struct Triangle
        {
            public Vector3 A, B, C;
            public Vector2 Facing;   // the outward normal's part across the cut (x, y)
        }

        private List<Vector3> localVertices;   // 3 per triangle, part rotation, meters
        private List<Triangle> triangles;
        private int builtFrame = -1;
        private readonly List<string> meshSources = new List<string>();   // for the shape dump

        private static readonly float[] ScanOffsets = { -0.95f, -0.7f, 0f, 0.7f, 0.95f };

        private bool GatherMesh(PartScript part)
        {
            localVertices = new List<Vector3>();
            triangles = null;
            builtFrame = -1;
            meshSources.Clear();
            Quaternion inverse = Quaternion.Inverse(part.transform.rotation);
            HashSet<Mesh> seen = new HashSet<Mesh>();
            foreach (MeshRenderer renderer in XRayView.GhostSources(part))
            {
                if (renderer == null) continue;
                string name = renderer.name;
                if (name == "AmmoRackGhost" || name == "AmmoRackRounds" || name == "ArmorXRayGhost") continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) continue;

                Vector3[] vertices = mesh.vertices;
                int[] indices = mesh.triangles;
                meshSources.Add(renderer.name + " (mesh '" + mesh.name + "', " + vertices.Length + " vertices, " +
                                indices.Length / 3 + " triangles, " + mesh.subMeshCount + " submeshes, enabled " +
                                renderer.enabled + (seen.Contains(mesh) ? ", SAME MESH AS ANOTHER: skipped" : "") + ")");
                if (!seen.Add(mesh)) continue;   // the same mesh drawn twice is one solid, not two
                if (indices.Length < 12) continue;

                Vector3[] local = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                    local[i] = inverse * (renderer.transform.TransformPoint(vertices[i]) - part.transform.position);
                for (int k = 0; k + 2 < indices.Length; k += 3)
                {
                    localVertices.Add(local[indices[k]]);
                    localVertices.Add(local[indices[k + 1]]);
                    localVertices.Add(local[indices[k + 2]]);
                }
            }
            return localVertices.Count > 0;
        }

        // The triangles in a frame (kept until another frame is asked for), and their extent.
        private void BuildTriangles(int frame)
        {
            if (builtFrame == frame && triangles != null) return;
            if (triangles == null) triangles = new List<Triangle>(localVertices.Count / 3);
            triangles.Clear();
            boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int k = 0; k + 2 < localVertices.Count; k += 3)
            {
                Vector3 a = InFrame(localVertices[k]), b = InFrame(localVertices[k + 1]), c = InFrame(localVertices[k + 2]);
                Vector3 normal = Vector3.Cross(b - a, c - a);   // Unity: front faces clockwise -> outward
                triangles.Add(new Triangle { A = a, B = b, C = c, Facing = new Vector2(normal.x, normal.y) });
                Include(a);
                Include(b);
                Include(c);
            }
            builtFrame = frame;
        }

        private Vector3 InFrame(Vector3 local)
        {
            return new Vector3(Vector3.Dot(local, axisX), Vector3.Dot(local, axisY), Vector3.Dot(local, axisZ));
        }

        // Each row of rounds (one column and layer of the cross grid) is packed on its own: the mesh is cut
        // every ~1 cm along the rounds, which gives, for every row, the stretches where a round's circle fits;
        // each stretch holds as many whole rounds as its length allows (centered in it). Rows don't share
        // slots, so a slanted, stepped or pointed end still gets filled as far as whole rounds go.
        private const int MaxDepthCuts = 1500;

        private void FillFromMesh()
        {
            List<Vector4> outline = new List<Vector4>();
            List<Vector2>[] stretches = new List<Vector2>[ScanOffsets.Length];
            for (int k = 0; k < stretches.Length; k++) stretches[k] = new List<Vector2>();
            float[] halfChord = new float[ScanOffsets.Length];
            for (int k = 0; k < ScanOffsets.Length; k++)
                halfChord[k] = radius * Mathf.Sqrt(1f - ScanOffsets[k] * ScanOffsets[k]);

            const float EndInset = 0.002f;   // stay off the end caps, which lie in the cutting plane
            float zStart = boundsMin.z + EndInset;
            float zEnd = boundsMax.z - EndInset;
            int cuts = Mathf.Clamp(Mathf.CeilToInt((zEnd - zStart) / Mathf.Clamp(length / 10f, 0.005f, 0.02f)) + 1,
                                   2, MaxDepthCuts);
            float step = (zEnd - zStart) / (cuts - 1);

            bool[,] inRun = new bool[Columns, Layers];
            float[,] runFrom = new float[Columns, Layers];
            float[,] runTo = new float[Columns, Layers];

            for (int s = 0; s < cuts; s++)
            {
                float z = zStart + s * step;
                Cut(z, outline);
                BinOutline(outline);

                for (int layer = 0; layer < Layers; layer++)
                {
                    float y = LayerY(layer);
                    for (int k = 0; k < ScanOffsets.Length; k++)
                        Stretches(outline, y + ScanOffsets[k] * radius, stretches[k]);

                    for (int column = 0; column < Columns; column++)
                    {
                        float x = ColumnX(column, layer);
                        bool fits = true;
                        for (int k = 0; k < ScanOffsets.Length && fits; k++)
                            fits = Covered(stretches[k], x - halfChord[k], x + halfChord[k]);

                        if (fits)
                        {
                            if (!inRun[column, layer])
                            {
                                inRun[column, layer] = true;
                                runFrom[column, layer] = z;
                            }
                            runTo[column, layer] = z;
                        }
                        else if (inRun[column, layer])
                        {
                            PlaceRun(column, layer, runFrom[column, layer], runTo[column, layer]);
                            inRun[column, layer] = false;
                        }
                    }
                }
            }

            for (int column = 0; column < Columns; column++)
                for (int layer = 0; layer < Layers; layer++)
                    if (inRun[column, layer]) PlaceRun(column, layer, runFrom[column, layer], runTo[column, layer]);

            // "Slots" becomes the longest row actually filled (for the read-outs).
            int longest = 0;
            foreach (List<int> row in rows.Values) longest = Mathf.Max(longest, row.Count);
            Slots = longest;
        }

        // As many whole rounds as fit between from and to (where the row's circle fits), centered.
        private void PlaceRun(int column, int layer, float from, float to)
        {
            int count = Mathf.FloorToInt((to - from) / length + 1e-4f);
            if (count <= 0) return;
            float start = from + ((to - from) - count * length) * 0.5f;
            for (int i = 0; i < count; i++)
                AddRound(new Vector3(ColumnX(column, layer), LayerY(layer), start + (i + 0.5f) * length), column, layer);
        }

        // The mesh cut by the plane at depth z: one 2D segment (x1, y1, x2, y2) per crossing triangle,
        // oriented with the triangle's outward side on its right (so the outline runs counterclockwise).
        private void Cut(float z, List<Vector4> outline)
        {
            outline.Clear();
            foreach (Triangle t in triangles)
            {
                bool a = t.A.z > z, b = t.B.z > z, c = t.C.z > z;
                if (a == b && b == c) continue;

                Vector2 p, q;
                if (a == b) { p = Cross(t.A, t.C, z); q = Cross(t.B, t.C, z); }
                else if (a == c) { p = Cross(t.A, t.B, z); q = Cross(t.C, t.B, z); }
                else { p = Cross(t.B, t.A, z); q = Cross(t.C, t.A, z); }

                Vector2 d = q - p;
                if (d.sqrMagnitude < 1e-12f) continue;   // the plane only touches it
                // Right of d is (d.y, -d.x); flip the segment if the outward side is on its left.
                if (t.Facing.x * d.y - t.Facing.y * d.x < 0f) { Vector2 swap = p; p = q; q = swap; }
                outline.Add(new Vector4(p.x, p.y, q.x, q.y));
            }
        }

        private static Vector2 Cross(Vector3 from, Vector3 to, float z)
        {
            float t = (z - from.z) / (to.z - from.z);
            return new Vector2(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t);
        }

        // The outline's segments sorted into bands of height, so a line across only looks at the segments of
        // its own band.
        private const int MaxBins = 256;
        private readonly List<int>[] bins = new List<int>[MaxBins];
        private int binCount;
        private float binFloor, binHeight;

        private void BinOutline(List<Vector4> outline)
        {
            float height = Mathf.Max(boundsMax.y - boundsMin.y, 1e-4f);
            binCount = Mathf.Clamp(Mathf.CeilToInt(height / Mathf.Max(radius * 0.5f, 0.002f)), 1, MaxBins);
            binFloor = boundsMin.y;
            binHeight = height / binCount;
            for (int i = 0; i < binCount; i++)
            {
                if (bins[i] == null) bins[i] = new List<int>();
                bins[i].Clear();
            }
            for (int i = 0; i < outline.Count; i++)
            {
                Vector4 s = outline[i];
                int from = Bin(Mathf.Min(s.y, s.w)), to = Bin(Mathf.Max(s.y, s.w));
                for (int b = from; b <= to; b++) bins[b].Add(i);
            }
        }

        private int Bin(float y)
        {
            return Mathf.Clamp(Mathf.FloorToInt((y - binFloor) / binHeight), 0, binCount - 1);
        }

        // The stretches of the line at height y that are inside the outline. Each crossing counts +1 (segment
        // going up) or -1 (going down). A gap between crossings is inside when the winding counted from the
        // left end AND the one counted from the right end are both nonzero (they are equal for a clean closed
        // outline; requiring both keeps a doubled face, which leaves the two ends unequal, from turning the
        // whole outside into "inside"). Tested outside the game on a wedge: closed, mirrored, split in two
        // along its length, and with doubled faces all give the right stretches.
        private static readonly List<Vector2> Crossings = new List<Vector2>();   // (x, +1 / -1)

        private void Stretches(List<Vector4> outline, float y, List<Vector2> result)
        {
            result.Clear();
            Crossings.Clear();
            if (y < boundsMin.y || y > boundsMax.y) return;
            foreach (int index in bins[Bin(y)])
            {
                Vector4 s = outline[index];
                bool above1 = s.y > y, above2 = s.w > y;
                if (above1 == above2) continue;
                float x = s.x + (y - s.y) * (s.z - s.x) / (s.w - s.y);
                Crossings.Add(new Vector2(x, s.w > s.y ? 1f : -1f));
            }
            if (Crossings.Count < 2) return;
            Crossings.Sort((a, b) => a.x.CompareTo(b.x));

            int total = 0;
            foreach (Vector2 c in Crossings) total += c.y > 0f ? 1 : -1;

            int fromLeft = 0;
            bool open = false;
            float start = 0f;
            for (int i = 0; i + 1 < Crossings.Count; i++)
            {
                fromLeft += Crossings[i].y > 0f ? 1 : -1;
                bool inside = fromLeft != 0 && total - fromLeft != 0;   // the gap after crossing i
                if (inside && !open)
                {
                    open = true;
                    start = Crossings[i].x;
                }
                else if (!inside && open)
                {
                    open = false;
                    if (Crossings[i].x > start) result.Add(new Vector2(start, Crossings[i].x));
                }
            }
            if (open) result.Add(new Vector2(start, Crossings[Crossings.Count - 1].x));
        }

        private static bool Covered(List<Vector2> stretches, float from, float to)
        {
            foreach (Vector2 s in stretches)
                if (s.x <= from && s.y >= to) return true;
            return false;
        }

        private string Fail(string status, string problem)
        {
            Status = status;
            return problem;
        }

        // The part's own solid colliders (the same ones the fragments hit).
        private static List<Collider> Colliders(PartScript part)
        {
            List<Collider> list = new List<Collider>();
            foreach (Collider collider in part.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (collider.GetComponentInParent<PartScript>() != part) continue;
                list.Add(collider);
            }
            return list;
        }

        // The colliders' extent in frame coordinates.
        private bool Extent(PartScript part, List<Collider> colliders)
        {
            boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;

            foreach (Collider collider in colliders)
            {
                MeshCollider mesh = collider as MeshCollider;
                BoxCollider box = collider as BoxCollider;
                if (mesh != null && mesh.sharedMesh != null)
                {
                    foreach (Vector3 vertex in mesh.sharedMesh.vertices)
                    {
                        Include(ToFrame(part.transform, collider.transform.TransformPoint(vertex)));
                        any = true;
                    }
                }
                else if (box != null)
                {
                    Vector3 half = box.size * 0.5f;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = box.center + new Vector3(
                            (i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                        Include(ToFrame(part.transform, box.transform.TransformPoint(corner)));
                    }
                    any = true;
                }
                else
                {
                    Bounds bounds = collider.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = new Vector3(
                            (i & 1) == 0 ? bounds.min.x : bounds.max.x, (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                            (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                        Include(ToFrame(part.transform, corner));
                    }
                    any = true;
                }
            }
            return any;
        }

        private void Include(Vector3 point)
        {
            boundsMin = Vector3.Min(boundsMin, point);
            boundsMax = Vector3.Max(boundsMax, point);
        }

        private bool RoundFits(Transform frame, List<Collider> colliders, Vector3 center, int alongSamples)
        {
            float start = center.z - length * 0.5f;
            float r = radius * RingTolerance;

            // Ends first: that is where a round usually pokes out.
            for (int pass = 0; pass < 2; pass++)
                for (int s = 0; s < alongSamples; s++)
                {
                    bool isEnd = s == 0 || s == alongSamples - 1;
                    if ((pass == 0) != isEnd) continue;

                    float z = start + length * (0.002f + 0.996f * s / (alongSamples - 1));
                    for (int a = 0; a < 8; a++)
                    {
                        float angle = a * Mathf.PI * 0.25f;
                        Vector3 point = new Vector3(center.x + Mathf.Cos(angle) * r, center.y + Mathf.Sin(angle) * r, z);
                        if (!Inside(colliders, FromFrame(frame, point))) return false;
                    }
                }
            return true;
        }

        private static bool Inside(List<Collider> colliders, Vector3 world)
        {
            foreach (Collider collider in colliders)
            {
                MeshCollider mesh = collider as MeshCollider;
                bool exact = mesh == null ? (collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider)
                                          : mesh.convex;
                if (exact)
                {
                    if ((collider.ClosestPoint(world) - world).sqrMagnitude < 1e-8f) return true;
                }
                else if (collider.bounds.Contains(world))
                {
                    return true;   // non-convex mesh: ClosestPoint is not available, the box will do
                }
            }
            return false;
        }

        // ---- Frame <-> world ---------------------------------------------------------

        // Rotation only, so a scaled transform doesn't change the meters.
        private Vector3 ToFrame(Transform part, Vector3 world)
        {
            return InFrame(Quaternion.Inverse(part.rotation) * (world - part.position));
        }

        private Vector3 FromFrame(Transform part, Vector3 frame)
        {
            Vector3 local = axisX * frame.x + axisY * frame.y + axisZ * frame.z;
            return part.position + part.rotation * local;
        }

        private Vector3 DirectionToFrame(Transform part, Vector3 worldDirection)
        {
            return InFrame(Quaternion.Inverse(part.rotation) * worldDirection);
        }

        // The middle of the rack in the world (where the explosion goes).
        public Vector3 WorldCenter(Transform part)
        {
            return part.position + part.rotation * centerLocal;
        }

        // ---- Using rounds ------------------------------------------------------------

        // Top first (by height in the part, to the millimeter), then left to right, then front to back.
        private void BuildRemovalOrder()
        {
            List<int> order = new List<int>();
            int[] height = new int[rounds.Count];
            for (int i = 0; i < rounds.Count; i++)
            {
                order.Add(i);
                height[i] = Mathf.RoundToInt(Vector3.Dot(LocalPoint(rounds[i].Center), Vector3.up) * 1000f);
            }
            order.Sort((a, b) =>
            {
                if (height[a] != height[b]) return height[b].CompareTo(height[a]);
                Round ra = rounds[a], rb = rounds[b];
                if (!Mathf.Approximately(ra.Center.x, rb.Center.x)) return ra.Center.x.CompareTo(rb.Center.x);
                return ra.Center.z.CompareTo(rb.Center.z);
            });
            removalOrder = order.ToArray();
            taken = 0;
        }

        public bool TakeRound()
        {
            if (taken >= removalOrder.Length) return false;
            present[removalOrder[taken]] = false;
            taken++;
            return true;
        }

        public void EmptyAll()
        {
            for (int i = 0; i < present.Length; i++) present[i] = false;
            taken = removalOrder.Length;
        }

        // Does the line through this point (both ways along the direction) cross a cell that still holds a
        // round? Fragments give their direction of travel; the game's own shell impact gives the surface
        // normal, so the line is taken both ways and clipped to the rack. thickness (m): the piece's own radius,
        // so a shell is a tube of its caliber and can't thread between packed rounds (0 = a thin line).
        public bool HitsRounds(Transform part, Vector3 worldPoint, Vector3 worldDirection, float thickness = 0f)
        {
            if (Remaining <= 0) return false;
            thickness = Mathf.Max(thickness, 0f);

            Vector3 p = ToFrame(part, worldPoint);
            Vector3 d = DirectionToFrame(part, worldDirection);
            if (d.sqrMagnitude < 1e-8f) return RoundAt(p, thickness);
            d.Normalize();

            // Clip the line to the rack's box (grown by a radius and the piece's own).
            Vector3 min = boundsMin - Vector3.one * (radius + thickness), max = boundsMax + Vector3.one * (radius + thickness);
            float t0 = float.MinValue, t1 = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(d[axis]) < 1e-6f)
                {
                    if (p[axis] < min[axis] || p[axis] > max[axis]) return false;
                    continue;
                }
                float a = (min[axis] - p[axis]) / d[axis], b = (max[axis] - p[axis]) / d[axis];
                t0 = Mathf.Max(t0, Mathf.Min(a, b));
                t1 = Mathf.Min(t1, Mathf.Max(a, b));
            }
            if (t0 > t1) return false;

            float step = Mathf.Max(radius * 0.5f, 0.005f);
            int steps = Mathf.Min(4000, Mathf.CeilToInt((t1 - t0) / step) + 1);
            for (int i = 0; i <= steps; i++)
                if (RoundAt(p + d * Mathf.Lerp(t0, t1, (float)i / steps), thickness)) return true;
            return false;
        }

        // Is there a round still in the rack within 'thickness' of this point (frame coordinates)? With a
        // thickness the neighboring columns and layers are looked at too.
        private bool RoundAt(Vector3 q, float thickness = 0f)
        {
            if (Layers <= 0 || Columns <= 0) return false;
            float reach = radius * 1.0488f + thickness;   // ~5% margin on the round's radius (1.0488^2 = 1.1)
            int l0 = Mathf.Max(0, Mathf.FloorToInt((q.y - reach - firstY) / layerPitch + 0.5f));
            int l1 = Mathf.Min(Layers - 1, Mathf.FloorToInt((q.y + reach - firstY) / layerPitch + 0.5f));

            for (int layer = l0; layer <= l1; layer++)
            {
                float x0 = firstX + RowShift(layer);
                int c0 = Mathf.Max(0, Mathf.FloorToInt((q.x - reach - x0) / pitch + 0.5f));
                int c1 = Mathf.Min(Columns - 1, Mathf.FloorToInt((q.x + reach - x0) / pitch + 0.5f));
                for (int column = c0; column <= c1; column++)
                {
                    List<int> row;
                    if (!rows.TryGetValue(RowKey(column, layer), out row)) continue;
                    foreach (int index in row)
                    {
                        if (!present[index]) continue;
                        Round round = rounds[index];
                        if (Mathf.Abs(q.z - round.Center.z) > length * 0.5f + thickness) continue;
                        float dx = q.x - round.Center.x, dy = q.y - round.Center.y;
                        if (dx * dx + dy * dy <= reach * reach) return true;
                    }
                }
            }
            return false;
        }

        // ---- Drawing (designer) ----------------------------------------------------------

        // The rounds still in the rack as cylinders: submesh 0 = cases, submesh 1 = shells (narrower, in
        // front, toward +Z of the rack's frame). Coordinates are in the part's rotation without its scale:
        // put the mesh on a child of the part whose scale cancels the part's.
        public Mesh BuildRoundsMesh(int maxRounds, int sides)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<int> cases = new List<int>();
            List<int> shells = new List<int>();

            int drawn = 0;
            for (int i = 0; i < rounds.Count && drawn < maxRounds; i++)
            {
                if (!present[i]) continue;
                drawn++;
                Vector3 c = rounds[i].Center;
                float z0 = c.z - length * 0.5f;
                float zCase = z0 + caseLength;
                float z1 = c.z + length * 0.5f;
                AddCylinder(vertices, normals, cases, c.x, c.y, z0, zCase, radius, sides, true, true);
                AddCylinder(vertices, normals, shells, c.x, c.y, zCase, z1, shellRadius, sides, false, true);
            }

            Mesh mesh = new Mesh { name = "AmmoRackRounds" };
            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(cases, 0);
            mesh.SetTriangles(shells, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        private void AddCylinder(List<Vector3> vertices, List<Vector3> normals, List<int> triangles,
                                 float x, float y, float z0, float z1, float r, int sides, bool backCap, bool frontCap)
        {
            // Side: two rings with outward normals.
            int start = vertices.Count;
            for (int s = 0; s <= sides; s++)
            {
                float angle = s * Mathf.PI * 2f / sides;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                Vector3 normal = Local(new Vector3(cos, sin, 0f));
                vertices.Add(LocalPoint(new Vector3(x + cos * r, y + sin * r, z0)));
                normals.Add(normal);
                vertices.Add(LocalPoint(new Vector3(x + cos * r, y + sin * r, z1)));
                normals.Add(normal);
            }
            for (int s = 0; s < sides; s++)
            {
                int a = start + s * 2;
                // Unity's front faces are clockwise seen from outside.
                triangles.Add(a); triangles.Add(a + 2); triangles.Add(a + 1);
                triangles.Add(a + 1); triangles.Add(a + 2); triangles.Add(a + 3);
            }

            if (backCap) AddCap(vertices, normals, triangles, x, y, z0, r, sides, -1f);
            if (frontCap) AddCap(vertices, normals, triangles, x, y, z1, r, sides, 1f);
        }

        private void AddCap(List<Vector3> vertices, List<Vector3> normals, List<int> triangles,
                            float x, float y, float z, float r, int sides, float facing)
        {
            Vector3 normal = Local(new Vector3(0f, 0f, facing));
            int center = vertices.Count;
            vertices.Add(LocalPoint(new Vector3(x, y, z)));
            normals.Add(normal);
            for (int s = 0; s <= sides; s++)
            {
                float angle = s * Mathf.PI * 2f / sides;
                vertices.Add(LocalPoint(new Vector3(x + Mathf.Cos(angle) * r, y + Mathf.Sin(angle) * r, z)));
                normals.Add(normal);
            }
            for (int s = 0; s < sides; s++)
            {
                int a = center + 1 + s;
                // Clockwise seen from the side the cap faces.
                if (facing > 0f) { triangles.Add(center); triangles.Add(a); triangles.Add(a + 1); }
                else { triangles.Add(center); triangles.Add(a + 1); triangles.Add(a); }
            }
        }

        private Vector3 LocalPoint(Vector3 frame)
        {
            return axisX * frame.x + axisY * frame.y + axisZ * frame.z;
        }

        private Vector3 Local(Vector3 frameDirection)
        {
            return (axisX * frameDirection.x + axisY * frameDirection.y + axisZ * frameDirection.z).normalized;
        }

        // ---- Shape dump (AmmoRacks.DebugDumpShape) ----------------------------------------
        //
        // Writes what was measured to an OBJ file: the part's triangles in the rack's frame (meters; Z along
        // the rounds), the round centers as points, and the measurement as comments. For checking the
        // measuring code against a real fuselage outside the game.
        public void DumpShape(string path, string title)
        {
            System.Text.StringBuilder obj = new System.Text.StringBuilder();
            CultureInfo inv = CultureInfo.InvariantCulture;
            obj.AppendLine("# Ammo rack shape dump: " + title);
            obj.AppendLine("# method " + Method + ", status " + Status + (Problem != null ? ", problem: " + Problem : ""));
            obj.AppendLine("# layout " + Orientation);
            obj.AppendLine("# axes (part local) X " + axisX.ToString("F4") + " Y " + axisY.ToString("F4") + " Z " + axisZ.ToString("F4"));
            obj.AppendLine("# bounds " + boundsMin.ToString("F4") + " .. " + boundsMax.ToString("F4"));
            obj.AppendLine("# round radius " + radius.ToString("F4", inv) + " length " + length.ToString("F4", inv) +
                           " pitch " + pitch.ToString("F4", inv) + " layer pitch " + layerPitch.ToString("F4", inv) +
                           "; first x " + firstX.ToString("F4", inv) + " y " + firstY.ToString("F4", inv));
            obj.AppendLine("# grid " + Columns + " columns x " + Layers + " layers, longest row " + Slots + ", capacity " + Capacity);
            foreach (string source in meshSources) obj.AppendLine("# mesh " + source);

            System.Text.StringBuilder rowsText = new System.Text.StringBuilder("# rounds per row (layer: columns):");
            for (int layer = 0; layer < Layers; layer++)
            {
                rowsText.Append("\n#  L").Append(layer).Append(':');
                for (int column = 0; column < Columns; column++)
                {
                    List<int> row;
                    rowsText.Append(' ').Append(rows.TryGetValue(RowKey(column, layer), out row) ? row.Count : 0);
                }
            }
            obj.AppendLine(rowsText.ToString());

            if (triangles != null)
            {
                obj.AppendLine("o shape");
                foreach (Triangle t in triangles)
                {
                    obj.AppendLine("v " + V(t.A, inv));
                    obj.AppendLine("v " + V(t.B, inv));
                    obj.AppendLine("v " + V(t.C, inv));
                }
                for (int i = 0; i < triangles.Count; i++)
                    obj.AppendLine("f " + (3 * i + 1) + " " + (3 * i + 2) + " " + (3 * i + 3));
            }

            int first = (triangles != null ? triangles.Count * 3 : 0) + 1;
            obj.AppendLine("o rounds");
            foreach (Round round in rounds) obj.AppendLine("v " + V(round.Center, inv));
            for (int i = 0; i < rounds.Count; i++) obj.AppendLine("p " + (first + i));

            System.IO.File.WriteAllText(path, obj.ToString());
        }

        private static string V(Vector3 v, CultureInfo inv)
        {
            return v.x.ToString("F5", inv) + " " + v.y.ToString("F5", inv) + " " + v.z.ToString("F5", inv);
        }

        // ---- Text ----------------------------------------------------------------------

        // "lengthwise: 2 slots x 6 x 2 = 24 rounds", or "upright, staggered: 2 slots x 6 x 2 grid, 18 fit the
        // shape" (slots = longest row)
        public string Describe()
        {
            if (Problem != null) return Problem;
            string grid = Slots + (Slots == 1 ? " slot" : " slots") + " x " + Columns + " x " + Layers;
            int full = Slots * Columns * Layers;
            return Orientation + ": " + (full == Capacity
                ? grid + " = " + Capacity + " rounds"
                : grid + " grid, " + Capacity + " fit the shape");
        }

        private static string Cm(float meters)
        {
            return (meters * 100f).ToString("F1", CultureInfo.InvariantCulture) + " cm";
        }
    }
}
