using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Powertrain;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight.Combat;
using HarmonyLib;
using NWH.VehiclePhysics2.Powertrain;
using UnityEngine;

namespace ArmorFragments
{
    // Crew ([CRW] seats: the game's Seat, Seat Ejection and Car Seat parts).
    //
    // Each crew seat is one crew member with a trade (Commander, Gunner, Loader, Driver, Radio operator). A Driver
    // drives, a Gunner aims, a Loader loads; Commander and Radio operator have no job of their own. The designer can
    // give any crew member more jobs ("Also does", [CRW j=]). The jobs:
    //  - Drive: the vehicle's engines (car engines and turboshafts) give their power x the drive efficiency.
    //  - Aim:   rotators (hinge, small rotator, rotator) marked [AIM] ("Aimed by crew") turn at their speed x the aim
    //           efficiency, whatever their input asks for. Other rotators aren't affected.
    //  - Load:  cannons fed by ammo racks reload in their Firing Delay / the load efficiency. A rack-fed cannon on a
    //           vehicle without crew doesn't fire.
    // A crew member's efficiency at a job = his trade's skill at it (CrewModel.Skill) x his workload (one job 100%,
    // two 90%, three 80%). Crew on the same job stack, best first: one driver; two aimers, the second adding 30% of
    // his efficiency; any number of loaders, scaled by the round's weight (CrewModel.LoadEfficiency, per cannon). Aim
    // and Load can go past 100%. A vehicle has at
    // most one Driver, one Commander and one Radio operator, and one crew member driving (the designer enforces it).
    //
    // A job the vehicle needs that nobody holds (none was given, or its crew died) goes to the living crew member who
    // would do it best. Every crew member can do every job.
    //
    // A crew member dies when his seat has lost all its health or is no longer attached to the vehicle. When the
    // last one dies, the vehicle is killed (VehicleRules.Kill: the game's own death, shell intact, nothing explodes).
    // Like an ammo rack, a crew seat is never destroyed by the game: its damage stops at 99.9% of its health, which
    // counts as dead, and BodyScript.ExplodePart is never allowed on it (a destroyed part of the cockpit's group would
    // make the game blow the whole vehicle apart).
    //
    // Funky Trees: Crew_alive (crew members alive), Crew_drive, Crew_aim, Crew_load (each job's efficiency, 0 to 1).
    internal sealed class CrewSeat
    {
        public PartData Data;
        public PartScript Part;
        public string Label;
        public CrewTrade Trade;
        public CrewJobs Designed;     // the jobs the designer gave
        public CrewJobs Jobs;         // the jobs held now (designed + taken over)
        public bool StartedConnected;
        public bool Dead;

        // Per job (CrewModel.Column): the time he reaches the station of a job he has just taken over (0 = there).
        // Until then he does that job at 0%.
        public readonly float[] ReadyAt = new float[3];
    }

    // The job model, shared by flight and the designer.
    internal static class CrewModel
    {
        public static readonly CrewJobs[] AllJobs = { CrewJobs.Load, CrewJobs.Aim, CrewJobs.Drive };   // filling order

        // Skill of each trade at each job: Drive, Aim, Load.
        private static readonly float[,] Skill =
        {
            { 0.70f, 0.85f, 0.85f },   // Commander
            { 0.65f, 1.00f, 0.70f },   // Gunner
            { 0.65f, 0.70f, 1.00f },   // Loader
            { 1.00f, 0.60f, 0.60f },   // Driver
            { 0.85f, 0.70f, 0.85f },   // Radio operator
            { 0.60f, 0.60f, 0.60f },   // No role: untrained, still stands in when a job is left without crew
        };

        public static float SkillOf(CrewTrade trade, CrewJobs job)
        {
            int column = job == CrewJobs.Drive ? 0 : job == CrewJobs.Aim ? 1 : 2;
            return Skill[(int)trade, column];
        }

        // The job a trade does on its own: Driver drives, Gunner aims, Loader loads. Commander and Radio operator have
        // none: they are the spares who take over first.
        public static CrewJobs OwnJob(CrewTrade trade)
        {
            switch (trade)
            {
                case CrewTrade.Driver: return CrewJobs.Drive;
                case CrewTrade.Gunner: return CrewJobs.Aim;
                case CrewTrade.Loader: return CrewJobs.Load;
                default: return CrewJobs.None;
            }
        }

        public static int Count(CrewJobs jobs)
        {
            int count = 0;
            foreach (CrewJobs job in AllJobs) if ((jobs & job) != 0) count++;
            return count;
        }

        // One job 100%, each more job 10% less.
        public static float Workload(int jobs)
        {
            return Mathf.Clamp01(1f - 0.1f * Mathf.Max(jobs - 1, 0));
        }

        public static float Efficiency(CrewSeat seat, CrewJobs job)
        {
            if (seat.Dead || (seat.Jobs & job) == 0) return 0f;
            if (seat.ReadyAt[Column(job)] > Time.time) return 0f;   // still getting to that station
            return SkillOf(seat.Trade, job) * Workload(Count(seat.Jobs));
        }

        public static int Column(CrewJobs job)
        {
            return job == CrewJobs.Drive ? 0 : job == CrewJobs.Aim ? 1 : 2;
        }

        // Does a living crew member hold the job (whether or not he has reached its station yet)?
        public static bool Held(IList<CrewSeat> seats, CrewJobs job)
        {
            foreach (CrewSeat seat in seats)
                if (!seat.Dead && (seat.Jobs & job) != 0) return true;
            return false;
        }

        // What each crew member on the same job adds, best first: one driver counts; two aimers, the second adding
        // 30% of his efficiency. Loaders: see LoadEfficiency. Aim and Load can go past 100% (rotators faster than
        // their set speed, reload quicker than the Firing Delay).
        private static readonly float[] DriveStack = { 1f };
        private static readonly float[] AimStack = { 1f, 0.3f };

        // Loading a round takes one loader per KgPerLoader of its weight, rounded ("hands", at least 1). Loaders count
        // best first and the sum is divided by 'hands': the first 'hands' each add their efficiency / hands, so fewer
        // loaders than it takes is slower in proportion; the k-th one beyond adds his efficiency x ExtraLoader x
        // sqrt(hands) / k. Past the hands it takes there's no ceiling, but each extra loader adds less (the total grows
        // like a logarithm), and the heavier the round, the more each extra loader adds, so heavier guns end up higher.
        // With 100% loaders, a light round: 100%, 120%, 130%, 137%...; a two-loader round: 50%, 100%, 128%, 142%...
        public const float KgPerLoader = 20f;
        public const float ExtraLoader = 0.2f;

        public static int Hands(float roundKg)
        {
            return Mathf.Max(1, Mathf.RoundToInt(roundKg / KgPerLoader));
        }

        // The load efficiency for a round of this weight (0 kg: a light round).
        public static float LoadEfficiency(IList<CrewSeat> seats, float roundKg)
        {
            List<float> doing = Doers(seats, CrewJobs.Load);
            int hands = Hands(roundKg);
            float extra = ExtraLoader * hands * Mathf.Sqrt(hands);   // / hands below: ExtraLoader x sqrt(hands) / k
            float total = 0f;
            for (int i = 0; i < doing.Count; i++)
                total += i < hands ? doing[i] : doing[i] * extra / (i - hands + 1);
            return total / hands;
        }

        // The job's efficiency on the vehicle: the crew doing it, best first, each times his share of the stack
        // (0 = nobody). Load: for a light round.
        public static float JobEfficiency(IList<CrewSeat> seats, CrewJobs job)
        {
            if (job == CrewJobs.Load) return LoadEfficiency(seats, 0f);
            List<float> doing = Doers(seats, job);
            float[] stack = job == CrewJobs.Drive ? DriveStack : AimStack;
            float total = 0f;
            for (int i = 0; i < doing.Count && i < stack.Length; i++) total += doing[i] * stack[i];
            return total;
        }

        // Each crew member's efficiency at the job, best first (those not doing it left out).
        private static List<float> Doers(IList<CrewSeat> seats, CrewJobs job)
        {
            List<float> doing = new List<float>();
            foreach (CrewSeat seat in seats)
            {
                float efficiency = Efficiency(seat, job);
                if (efficiency > 0f) doing.Add(efficiency);
            }
            doing.Sort((a, b) => b.CompareTo(a));
            return doing;
        }

        public static int Doing(IList<CrewSeat> seats, CrewJobs job)
        {
            int count = 0;
            foreach (CrewSeat seat in seats) if (Efficiency(seat, job) > 0f) count++;
            return count;
        }

        // Trades a vehicle has one of at most: Driver, Commander, Radio operator.
        public static bool Unique(CrewTrade trade)
        {
            return trade == CrewTrade.Driver || trade == CrewTrade.Commander || trade == CrewTrade.Radio;
        }

        // Gives every needed job nobody holds to the living crew member who would do it best (fewer jobs on a tie).
        // Returns what changed, for the log.
        // switchSeconds: how long the new holder takes to reach the station (0 at the start of a flight).
        public static List<string> Fill(IList<CrewSeat> seats, CrewJobs needed, float switchSeconds = 0f)
        {
            List<string> changes = new List<string>();
            foreach (CrewJobs job in AllJobs)
            {
                if ((needed & job) == 0 || Held(seats, job)) continue;
                CrewSeat best = null;
                float bestScore = -1f;
                foreach (CrewSeat seat in seats)
                {
                    if (seat.Dead) continue;
                    float score = SkillOf(seat.Trade, job) * Workload(Count(seat.Jobs) + 1);
                    if (score > bestScore + 1e-4f ||
                        (Mathf.Abs(score - bestScore) <= 1e-4f && best != null && Count(seat.Jobs) < Count(best.Jobs)))
                    {
                        best = seat;
                        bestScore = score;
                    }
                }
                if (best == null) continue;
                best.Jobs |= job;
                best.ReadyAt[Column(job)] = switchSeconds > 0f ? Time.time + switchSeconds : 0f;
                changes.Add(JobName(job) + " -> '" + best.Label + "' (" + TradeName(best.Trade) + ")" +
                            (switchSeconds > 0f ? " in " + switchSeconds.ToString("0.#") + " s" : ""));
            }
            return changes;
        }

        // The jobs a vehicle has a use for: driving if it has an engine, aiming if it has a rotator marked [AIM],
        // loading if it has a cannon fed by ammo racks.
        public static CrewJobs Needed(IEnumerable<PartData> parts)
        {
            CrewJobs needed = CrewJobs.None;
            foreach (PartData data in parts)
            {
                if (data == null || data.Modifiers == null) continue;
                foreach (PartModifierData modifier in data.Modifiers)
                {
                    if (modifier is JEngineData || modifier is TurboshaftData) needed |= CrewJobs.Drive;
                }
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.Aim != null && IsRotator(data)) needed |= CrewJobs.Aim;
                if (tags.Cannon != null && tags.Cannon.Racks.Count > 0) needed |= CrewJobs.Load;
            }
            return needed;
        }

        // Hinge, small rotator and rotator: the parts with the game's JointRotator modifier.
        public static bool IsRotator(PartData data)
        {
            if (data == null || data.Modifiers == null) return false;
            foreach (PartModifierData modifier in data.Modifiers)
                if (modifier is JointRotatorData) return true;
            return false;
        }

        public static string JobName(CrewJobs job)
        {
            return job == CrewJobs.Drive ? "Drive" : job == CrewJobs.Aim ? "Aim" : "Load";
        }

        public static string TradeName(CrewTrade trade)
        {
            return trade == CrewTrade.Radio ? "Radio operator" : trade == CrewTrade.None ? "No role" : trade.ToString();
        }

        public static string Percent(float value)
        {
            return Mathf.RoundToInt(value * 100f) + "%";
        }
    }

    internal static class Crew
    {
        private sealed class Crewed
        {
            public AircraftScript Aircraft;
            public readonly List<CrewSeat> Seats = new List<CrewSeat>();
            public CrewJobs Needed;
            public float Drive = 1f, Aim = 1f, Load = 1f;
            public AircraftVariable AliveVariable, DriveVariable, AimVariable, LoadVariable;
            public readonly HashSet<EngineComponent> Engines = new HashSet<EngineComponent>();
            public float NextEngineScan;
            public bool Killed;

            public int Alive
            {
                get
                {
                    int alive = 0;
                    foreach (CrewSeat seat in Seats) if (!seat.Dead) alive++;
                    return alive;
                }
            }

            public void Refresh()
            {
                Drive = CrewModel.JobEfficiency(Seats, CrewJobs.Drive);
                Aim = CrewModel.JobEfficiency(Seats, CrewJobs.Aim);
                Load = CrewModel.JobEfficiency(Seats, CrewJobs.Load);
            }
        }

        internal const string AliveVariableName = "Crew_alive";
        internal const string DriveVariableName = "Crew_drive";
        internal const string AimVariableName = "Crew_aim";
        internal const string LoadVariableName = "Crew_load";

        // A crew member is dead at this share of health lost (held at 0.999 by CrewDamagePatch; room for rounding).
        private const float DeadLevel = 0.998f;

        private static readonly Dictionary<AircraftScript, Crewed> Crafts = new Dictionary<AircraftScript, Crewed>();
        private static readonly Dictionary<PartScript, CrewSeat> SeatOf = new Dictionary<PartScript, CrewSeat>();
        private static readonly HashSet<AircraftScript> WarnedNoCrew = new HashSet<AircraftScript>();

        public static bool IsCrew(PartScript part)
        {
            return part != null && SeatOf.ContainsKey(part);
        }

        // A crew member is a seat (a part with the game's Seat modifier) marked [CRW]. Armor takes no damage, so an
        // armored seat is no crew member.
        public static bool CountsAsCrew(PartData data, PartTags tags)
        {
            return tags.Crew != null && tags.Armor == null && IsSeat(data);
        }

        public static bool IsSeat(PartData data)
        {
            if (data == null || data.Modifiers == null) return false;
            foreach (PartModifierData modifier in data.Modifiers)
                if (modifier is SeatData) return true;
            return false;
        }

        // ---- Effects --------------------------------------------------------------------

        private static Crewed CraftOf(AircraftScript aircraft)
        {
            Crewed craft;
            if (aircraft == null || !ArmorFragmentsPlugin.CrewEnabled.Value) return null;
            return Crafts.TryGetValue(aircraft, out craft) ? craft : null;
        }

        // A rotator marked [AIM] turns at its speed x this; other rotators aren't affected.
        // A vehicle whose whole crew is dead: nobody works anything, so every rotator stops (AimFactor 0).
        public static float AimFactor(PartScript rotator)
        {
            if (rotator == null || rotator.Part == null) return 1f;
            Crewed craft = CraftOf(rotator.Aircraft);
            if (craft == null) return 1f;
            if (craft.Killed) return 0f;
            if (TagCodec.Parse(rotator.Part.Name).Aim == null) return 1f;
            return craft.Aim;
        }

        // Nobody driving (the driver's replacement still on his way, or the whole crew dead): the engines are switched
        // off (CrewEnginePatch), as the game does for an engine whose control isn't active, so they don't stall and
        // try to start again. A new driver at the controls starts them again.
        public static bool NobodyDrives(AircraftScript aircraft)
        {
            Crewed craft = CraftOf(aircraft);
            return craft != null && (craft.Killed || craft.Drive <= 0f);
        }

        // A rack-fed cannon's reload is its Firing Delay x this: 1 / the load efficiency for its round's weight.
        public static float ReloadFactor(CannonScript cannon)
        {
            if (cannon == null || !AmmoRacks.IsRackFed(cannon)) return 1f;
            Crewed craft = CraftOf(cannon.PartScript != null ? cannon.PartScript.Aircraft : null);
            if (craft == null) return 1f;
            AmmoRound round;
            float kg = AmmoRound.TryFromPart(cannon.PartScript, out round) ? round.RoundMass : 0f;
            return 1f / Mathf.Max(CrewModel.LoadEfficiency(craft.Seats, kg), 0.05f);
        }

        // A rack-fed cannon fires only if its vehicle has crew to load it.
        public static bool CanLoad(CannonScript cannon)
        {
            if (!ArmorFragmentsPlugin.CrewEnabled.Value || cannon == null || !AmmoRacks.IsRackFed(cannon)) return true;
            AircraftScript aircraft = cannon.PartScript != null ? cannon.PartScript.Aircraft : null;
            if (aircraft == null || aircraft.RemoteAircraft || aircraft.LoadContext != CraftLoadContext.Flight) return true;

            Crewed craft = CraftOf(aircraft);
            if (craft != null) return craft.Load > 0f;
            if (WarnedNoCrew.Add(aircraft))
                ArmorFragmentsPlugin.Log.LogWarning("Cannon '" + cannon.PartScript.Part.Name + "' is fed by ammo racks but " +
                    "the vehicle has no crew to load it: mark a seat as crew. It won't fire.");
            return false;
        }

        // ---- Registering (VariableSystemScript.RefreshVariables) -----------------------

        public static void Register(VariableSystemScript system)
        {
            if (!ArmorFragmentsPlugin.CrewEnabled.Value) return;

            AircraftScript aircraft = system.AircraftScript;
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return;

            // Every copy in flight, yours and other players': the crew's bodies (shots are traced in the shooter's game).
            if (aircraft.LoadContext == CraftLoadContext.Flight) CrewBodies.Register(aircraft);

            // Designer (or a remote copy): only make the variables exist, so they show in the lists.
            if (aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft)
            {
                if (DesignCount(aircraft) > 0) CreateVariables(system, null);
                return;
            }

            // In flight the list may be rebuilt more than once: the crew keep their state.
            Crewed craft;
            if (!Crafts.TryGetValue(aircraft, out craft))
            {
                craft = Build(aircraft);
                if (craft.Seats.Count == 0) return;
                Crafts[aircraft] = craft;
            }
            CreateVariables(system, craft);
        }

        private static void CreateVariables(VariableSystemScript system, Crewed craft)
        {
            AircraftVariable alive = ModuleOutputs.CreateVariable(system, AliveVariableName);
            AircraftVariable drive = ModuleOutputs.CreateVariable(system, DriveVariableName);
            AircraftVariable aim = ModuleOutputs.CreateVariable(system, AimVariableName);
            AircraftVariable load = ModuleOutputs.CreateVariable(system, LoadVariableName);
            if (craft == null) return;
            craft.AliveVariable = alive;
            craft.DriveVariable = drive;
            craft.AimVariable = aim;
            craft.LoadVariable = load;
        }

        private static Crewed Build(AircraftScript aircraft)
        {
            Crewed craft = new Crewed { Aircraft = aircraft };
            foreach (CrewSeat seat in Seats(aircraft.Aircraft.Assembly.Parts, true))
            {
                craft.Seats.Add(seat);
                SeatOf[seat.Part] = seat;
            }
            if (craft.Seats.Count == 0) return craft;

            craft.Needed = CrewModel.Needed(aircraft.Aircraft.Assembly.Parts);
            List<string> filled = CrewModel.Fill(craft.Seats, craft.Needed);
            craft.Refresh();

            List<string> lines = new List<string>();
            foreach (CrewSeat seat in craft.Seats) lines.Add(Describe(seat));
            ArmorFragmentsPlugin.Log.LogInfo("Crew: " + craft.Seats.Count + " crew member(s): " + string.Join("; ", lines.ToArray()) +
                (filled.Count > 0 ? ". Jobs nobody was given: " + string.Join(", ", filled.ToArray()) : "") +
                ". " + JobsLine(craft.Seats) + ". The vehicle dies when the last one dies");
            return craft;
        }

        // The crew seats among these parts. withScripts: flight (each seat needs its PartScript).
        private static List<CrewSeat> Seats(IEnumerable<PartData> parts, bool withScripts)
        {
            List<CrewSeat> seats = new List<CrewSeat>();
            foreach (PartData data in parts)
            {
                if (data == null) continue;
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.Crew == null) continue;
                if (!CountsAsCrew(data, tags))
                {
                    if (withScripts)
                        ArmorFragmentsPlugin.Log.LogWarning("'" + data.Name + "' is marked as crew but " +
                            (!IsSeat(data) ? "is not a seat (only seats can be crew)" : "is armor (armor takes no damage)") +
                            ": its crew tag is ignored.");
                    continue;
                }
                PartScript part = data.PartScript;
                if (withScripts && part == null) continue;

                seats.Add(new CrewSeat
                {
                    Data = data,
                    Part = part,
                    Label = string.IsNullOrEmpty(tags.Label) ? "crew " + data.Id : tags.Label,
                    Trade = tags.Crew.Trade,
                    Designed = CrewModel.OwnJob(tags.Crew.Trade) | tags.Crew.Jobs,   // his trade's job + "also does"
                    Jobs = CrewModel.OwnJob(tags.Crew.Trade) | tags.Crew.Jobs,
                    StartedConnected = part != null && part.ConnectedToMainCockpit,
                });
            }
            return seats;
        }

        public static string Describe(CrewSeat seat)
        {
            List<string> jobs = new List<string>();
            foreach (CrewJobs job in CrewModel.AllJobs)
                if ((seat.Jobs & job) != 0) jobs.Add(CrewModel.JobName(job) + " " + CrewModel.Percent(CrewModel.Efficiency(seat, job)));
            return "'" + seat.Label + "' (" + CrewModel.TradeName(seat.Trade) + ")" +
                   (seat.Dead ? " dead" : jobs.Count > 0 ? ": " + string.Join(", ", jobs.ToArray()) : ": no job");
        }

        private static string JobsLine(IList<CrewSeat> seats)
        {
            List<string> parts = new List<string>();
            foreach (CrewJobs job in CrewModel.AllJobs)
                parts.Add(CrewModel.JobName(job) + " " + CrewModel.Percent(CrewModel.JobEfficiency(seats, job)));
            return string.Join(", ", parts.ToArray());
        }

        // ---- Designer -------------------------------------------------------------------

        // Crew seats on a craft in the designer.
        public static int DesignCount(AircraftScript aircraft)
        {
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return 0;
            return Seats(aircraft.Aircraft.Assembly.Parts, false).Count;
        }

        // The crew as they would start in flight: designed jobs plus the needed jobs nobody was given.
        public static List<CrewSeat> DesignCrew(AircraftScript aircraft, out CrewJobs needed)
        {
            needed = CrewJobs.None;
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return new List<CrewSeat>();
            List<CrewSeat> seats = Seats(aircraft.Aircraft.Assembly.Parts, false);
            needed = CrewModel.Needed(aircraft.Aircraft.Assembly.Parts);
            CrewModel.Fill(seats, needed);
            return seats;
        }

        // ---- Damage (PartScript.OnDamaged) ------------------------------------------------

        public static void OnDamaged(PartScript part)
        {
            CrewSeat seat;
            if (!SeatOf.TryGetValue(part, out seat) || seat.Dead) return;
            AircraftScript aircraft = part.Aircraft;
            if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) return;

            if (AmmoRacks.DamageLevel(part) >= DeadLevel) Die(aircraft, seat, "killed");
        }

        // ---- Every frame -----------------------------------------------------------------

        public static void Tick()
        {
            if (Crafts.Count == 0) return;

            List<AircraftScript> gone = null;
            foreach (KeyValuePair<AircraftScript, Crewed> pair in Crafts)
            {
                AircraftScript aircraft = pair.Key;
                if (aircraft == null)
                {
                    if (gone == null) gone = new List<AircraftScript>();
                    gone.Add(aircraft);
                    continue;
                }
                if (aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) continue;

                try
                {
                    Crewed craft = pair.Value;
                    // A crew member whose seat is gone or broke off the vehicle is lost with it.
                    foreach (CrewSeat seat in craft.Seats)
                    {
                        if (seat.Dead) continue;
                        if (seat.Part == null) Die(aircraft, seat, "lost with the part");
                        else if (seat.StartedConnected && !seat.Part.ConnectedToMainCockpit)
                            Die(aircraft, seat, "lost with a part that broke off");
                    }
                    if (Time.time >= craft.NextEngineScan) HookEngines(craft);
                    Arrive(craft);
                    SetVariable(craft.AliveVariable, craft.Alive);
                    SetVariable(craft.DriveVariable, craft.Drive);
                    SetVariable(craft.AimVariable, craft.Aim);
                    SetVariable(craft.LoadVariable, craft.Load);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Crew: " + e);
                }
            }

            if (gone != null)
                foreach (AircraftScript aircraft in gone) Forget(aircraft);
        }

        private static void SetVariable(AircraftVariable variable, float value)
        {
            if (variable != null) variable.SetValue(value, 0);
        }

        // Engines' power x the drive efficiency, through the vehicle physics' own power modifiers (multiplied into
        // the engine's power every step). Engines get their powertrain after the craft loads, so they are looked
        // for once a second.
        private static void HookEngines(Crewed craft)
        {
            craft.NextEngineScan = Time.time + 1f;
            foreach (PartData data in craft.Aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                EngineComponent engine = EngineOf(part);
                if (engine == null || engine.powerModifiers == null || !craft.Engines.Add(engine)) continue;
                Crewed owner = craft;
                engine.powerModifiers.Add(() => owner.Drive);
                ArmorFragmentsPlugin.Log.LogInfo("Crew: engine '" + data.Name + "' runs at the drive efficiency (" +
                                                 CrewModel.Percent(craft.Drive) + ")");
            }
        }

        internal static EngineComponent EngineOf(PartScript part)
        {
            try
            {
                JEngineScript car = part.GetModifier<JEngineScript>();
                if (car != null) return car.Powertrain != null ? car.Powertrain.engine : null;
                TurboshaftScript turboshaft = part.GetModifier<TurboshaftScript>();
                if (turboshaft != null) return turboshaft.Powertrain != null ? turboshaft.Powertrain.engine : null;
            }
            catch (NullReferenceException)
            {
                // Not set up yet: tried again next scan.
            }
            return null;
        }

        // Crew members reaching the station of a job they took over: the job works again (Drive, Aim and Load are
        // read again; the engines read Drive every update).
        private static void Arrive(Crewed craft)
        {
            bool any = false;
            foreach (CrewSeat seat in craft.Seats)
            {
                foreach (CrewJobs job in CrewModel.AllJobs)
                {
                    int column = CrewModel.Column(job);
                    if (seat.ReadyAt[column] <= 0f || seat.ReadyAt[column] > Time.time) continue;
                    seat.ReadyAt[column] = 0f;
                    if (seat.Dead) continue;
                    any = true;
                    ArmorFragmentsPlugin.Log.LogInfo("Crew member '" + seat.Label + "' (" + CrewModel.TradeName(seat.Trade) +
                        ") has taken over: " + CrewModel.JobName(job) + " " + CrewModel.Percent(CrewModel.Efficiency(seat, job)));
                }
            }
            if (!any) return;
            craft.Refresh();
            ArmorFragmentsPlugin.Log.LogInfo("Crew: " + JobsLine(craft.Seats));
        }

        private static void Die(AircraftScript aircraft, CrewSeat seat, string how)
        {
            Crewed craft;
            if (!Crafts.TryGetValue(aircraft, out craft)) return;

            seat.Dead = true;
            CrewJobs lost = seat.Jobs;
            seat.Jobs = CrewJobs.None;
            List<string> filled = CrewModel.Fill(craft.Seats, craft.Needed | lost,
                                                 Mathf.Max(ArmorFragmentsPlugin.CrewSwitchSeconds.Value, 0f));
            craft.Refresh();

            int alive = craft.Alive;
            ArmorFragmentsPlugin.Log.LogInfo("Crew member '" + seat.Label + "' (" + CrewModel.TradeName(seat.Trade) + ") " + how +
                ": " + alive + " of " + craft.Seats.Count + " crew alive" +
                (filled.Count > 0 ? "; takes over: " + string.Join(", ", filled.ToArray()) : "") +
                (alive > 0 ? ". " + JobsLine(craft.Seats) : ""));
            if (alive > 0 || craft.Killed) return;

            // The last crew member: the vehicle is killed, as when an ammo rack explodes, but nothing explodes.
            craft.Killed = true;
            VehicleRules.KnockOut(aircraft, "its whole crew is dead");
            VehicleRules.Kill(aircraft, "the whole crew (" + craft.Seats.Count + ") is dead");
        }

        private static void Forget(AircraftScript aircraft)
        {
            Crewed craft;
            if (Crafts.TryGetValue(aircraft, out craft))
            {
                foreach (CrewSeat seat in craft.Seats) SeatOf.Remove(seat.Part);
                Crafts.Remove(aircraft);
            }
            WarnedNoCrew.Remove(aircraft);
            VehicleRules.Forget(aircraft);
        }
    }

    // Recreate the crew variables whenever the game rebuilds the variable list (it drops unknown ones).
    [HarmonyPatch(typeof(VariableSystemScript), "RefreshVariables")]
    internal static class CrewVariablesPatch
    {
        private static void Postfix(VariableSystemScript __instance)
        {
            try
            {
                Crew.Register(__instance);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Could not register the crew: " + e);
            }
        }
    }

    // A crew seat is never destroyed by the game: the damage is cut so it stops at 99.9% of its health, which
    // counts as dead (see AmmoRackDamagePatch for why).
    [HarmonyPatch(typeof(PartScript), "OnDamaged")]
    internal static class CrewDamagePatch
    {
        private const float MaxCrewDamage = 0.999f;

        private static void Prefix(PartScript __instance, ref float damage, out bool __state)
        {
            __state = Crew.IsCrew(__instance);
            if (!__state) return;
            float room = __instance.MaxHealth * MaxCrewDamage - __instance.PartDamage;
            if (damage > room) damage = Mathf.Max(0f, room);
        }

        private static void Postfix(PartScript __instance, bool __state)
        {
            if (!__state) return;
            try
            {
                Crew.OnDamaged(__instance);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew damage check failed: " + e);
            }
        }
    }

    // Aim: a rotator marked [AIM] turns toward its target angle at its speed x the aim efficiency. The speed is the rotator's own
    // limit on how fast it moves (JointRotatorScript._speed, degrees per second), so it holds whatever the input asks.
    [HarmonyPatch(typeof(JointRotatorScript), "OnFixedUpdate")]
    internal static class CrewRotatorPatch
    {
        private static readonly AccessTools.FieldRef<JointRotatorScript, float> Speed =
            AccessTools.FieldRefAccess<JointRotatorScript, float>("_speed");

        // The factor last written to the log for each rotator (a new line when it changes).
        private static readonly Dictionary<JointRotatorScript, float> Logged = new Dictionary<JointRotatorScript, float>();

        // A stopped rotator (factor 0) is silent: its motor sound comes from the gap between where its input wants it
        // and where it is, which never closes while it can't move. Its sound source is switched off until it moves
        // again.
        private static readonly System.Reflection.FieldInfo AudioField = AccessTools.Field(typeof(JointRotatorScript), "_audio");
        private static readonly HashSet<JointRotatorScript> Muted = new HashSet<JointRotatorScript>();

        private static void Mute(JointRotatorScript rotator, bool mute)
        {
            if (mute == Muted.Contains(rotator)) return;
            Behaviour audio = AudioField != null ? AudioField.GetValue(rotator) as Behaviour : null;
            if (audio == null) return;
            audio.enabled = !mute;
            if (mute)
            {
                if (Muted.Count > 256) Muted.RemoveWhere(r => r == null);
                Muted.Add(rotator);
            }
            else Muted.Remove(rotator);
        }

        private static void Prefix(JointRotatorScript __instance, out float __state)
        {
            __state = -1f;
            try
            {
                float factor = Crew.AimFactor(__instance.PartScript);
                Mute(__instance, factor <= 0f);
                if (Mathf.Approximately(factor, 1f)) return;
                __state = Speed(__instance);
                Speed(__instance) = __state * factor;

                float logged;
                if (!Logged.TryGetValue(__instance, out logged) || Mathf.Abs(logged - factor) > 0.005f)
                {
                    if (Logged.Count > 256) Logged.Clear();
                    Logged[__instance] = factor;
                    ArmorFragmentsPlugin.Log.LogInfo("Crew: rotator '" + __instance.PartScript.Part.Name + "' turns at " +
                        CrewModel.Percent(factor) + " of its speed (" + __state.ToString("0.#") + " -> " +
                        (__state * factor).ToString("0.#") + " deg/s)");
                }
            }
            catch (Exception)
            {
                __state = -1f;
            }
        }

        private static void Postfix(JointRotatorScript __instance, float __state)
        {
            if (__state >= 0f) Speed(__instance) = __state;
        }
    }

    // Load: the weapon system schedules a group's next shot at Firing Delay after the last one; for a rack-fed cannon
    // that wait is divided by the load efficiency (longer below 100%, shorter above it with extra loaders).
    [HarmonyPatch(typeof(CannonWeaponSystem), "Fire")]
    internal static class CrewReloadPatch
    {
        private static readonly AccessTools.FieldRef<CannonWeaponSystem, float> FireTime =
            AccessTools.FieldRefAccess<CannonWeaponSystem, float>("_fireTime");
        private static readonly AccessTools.FieldRef<WeaponSystem, float> Now =
            AccessTools.FieldRefAccess<WeaponSystem, float>("_time");
        private static readonly AccessTools.FieldRef<CannonWeaponSystem, CannonScript> NextCannon =
            AccessTools.FieldRefAccess<CannonWeaponSystem, CannonScript>("_lastFiredCannonScript");

        private static void Prefix(CannonWeaponSystem __instance, out float __state)
        {
            __state = FireTime(__instance);
        }

        private static void Postfix(CannonWeaponSystem __instance, float __state)
        {
            try
            {
                if (FireTime(__instance) == __state) return;   // no shot scheduled
                CannonScript next = NextCannon(__instance);
                float factor = Crew.ReloadFactor(next);
                if (!Mathf.Approximately(factor, 1f))
                {
                    FireTime(__instance) = Now(__instance) + next.FiringDelay * factor;
                    if (Crew.CanLoad(next)) ArmorFragmentsPlugin.Log.LogInfo("Crew: cannon '" + next.PartScript.Part.Name + "' reloads in " +
                        (next.FiringDelay * factor).ToString("0.0") + " s (Firing Delay " + next.FiringDelay.ToString("0.0") +
                        " s, load " + CrewModel.Percent(1f / factor) + ")");
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew reload failed: " + e.Message);
            }
        }
    }
}

namespace ArmorFragments
{
    // A copied crew seat comes in with no role and no extra jobs, so a copy never makes a second Driver, Commander or
    // Radio operator: clones and mirrors (SymmetryUtility.DuplicatePart, also used by the fuselage and move tools), and
    // pasted parts and subassemblies (Assembly.Absorb). The player then picks its role.
    internal static class CrewCopies
    {
        public static void MakeRoleless(PartData part)
        {
            if (part == null) return;
            PartTags tags = TagCodec.ParseCopy(part.Name);
            if (tags.Crew == null || (tags.Crew.Trade == CrewTrade.None && tags.Crew.Jobs == CrewJobs.None)) return;
            tags.Crew.Trade = CrewTrade.None;
            tags.Crew.Jobs = CrewJobs.None;
            RackLinks.SetName(part, TagCodec.Compose(tags, part.Name));
        }
    }

    [HarmonyPatch(typeof(Assets.Scripts.Design.Symmetry.SymmetryUtility), "DuplicatePart")]
    internal static class CrewDuplicatePatch
    {
        private static void Postfix(PartData __result)
        {
            try
            {
                CrewCopies.MakeRoleless(__result);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew (copied seat) failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Assembly), "Absorb")]
    internal static class CrewAbsorbPatch
    {
        private static void Prefix(Assembly assembly)
        {
            try
            {
                if (assembly == null) return;
                foreach (PartData part in assembly.Parts) CrewCopies.MakeRoleless(part);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew (added parts) failed: " + e.Message);
            }
        }
    }
}

namespace ArmorFragments
{
    // Nobody driving (Crew.NobodyDrives): after the game's own engine step, the engine's ignition is switched off, as
    // the game does for an engine whose control isn't active. Car engines and turboshafts.
    [HarmonyPatch(typeof(JEngineScript), "OnFixedUpdate")]
    internal static class CrewEnginePatch
    {
        private static void Postfix(JEngineScript __instance)
        {
            CrewEngineOff.Apply(__instance.PartScript);
        }
    }

    [HarmonyPatch(typeof(TurboshaftScript), "OnFixedUpdate")]
    internal static class CrewTurboshaftPatch
    {
        private static void Postfix(TurboshaftScript __instance)
        {
            CrewEngineOff.Apply(__instance.PartScript);
        }
    }

    internal static class CrewEngineOff
    {
        public static void Apply(PartScript part)
        {
            try
            {
                if (part == null || !Crew.NobodyDrives(part.Aircraft)) return;
                EngineComponent engine = Crew.EngineOf(part);
                if (engine != null && engine.ignition) engine.ignition = false;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Crew: engine switch-off failed: " + e.Message);
            }
        }
    }
}
