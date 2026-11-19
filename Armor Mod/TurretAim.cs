using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Cameras;
using Assets.Scripts.Input;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ArmorFragments
{
    // Mouse aim for turrets, in the style of War Thunder's third person view.
    //
    //  - Rotators marked [TUR] (the designer's "Turret" toggle) are turned by this code; cannons marked [PTR] ("Pointer" toggle)
    //    are the ones it points. The mouse turns the game's orbit camera, and the turret turns until the pointer cannon points
    //    at what is under the circle in the middle of the screen. A small marker shows where the cannon really points.
    //  - Hold Alt: the cursor comes back for the game's own interface. Hold C: the camera is freed from the turret, which keeps
    //    pointing where it was while you look around; let go and the camera comes back gradually. Right button: gradual zoom.
    //    Left button: fire.
    //
    // How the turret is turned. A rotator already is a position servo with a speed limit (JointRotatorScript turns its angle
    // toward _targetAngle at its own speed), so what is missing is only which angle to ask for. Every physics step this works out
    // the rotation that takes the cannon's barrel onto the wanted direction, splits it between the marked rotators by their
    // world axes (a small least-squares problem, so any layout of turret ring and elevation works), and asks each rotator for
    // its current angle plus its share of the rotation. The barrel is measured every step, so a vehicle that pitches or rolls
    // is corrected at once (the gun is stabilized). Which way each rotator turns the barrel is not assumed: when the aim
    // starts, each rotator is moved a few degrees and the barrel's motion is measured.
    internal static class TurretAim
    {
        public static bool Active { get; private set; }

        // Shown in the panel.
        public static string Status = "Off";

        private sealed class Turret
        {
            public JointRotatorScript Script;
            public string Name;
            public float Range;          // degrees either way (a rotator that wraps has no limit)
            public bool Wraps;
            public bool Done;            // calibrated (or given up on)
            public bool Usable;
            public float Sign = 1f;      // +1 / -1: the barrel turns this way about Axis when the angle grows
        }

        private sealed class Calibration
        {
            public Turret Turret;
            public int Phase;            // 0: moving, 1: settling
            public float Angle0, Delta, Timer;
            public Vector3 Barrel0, Axis;
        }

        private static AircraftScript craft;
        private static readonly List<Turret> turrets = new List<Turret>();
        private static readonly List<CannonScript> pointers = new List<CannonScript>();
        private static readonly Dictionary<JointRotatorScript, float> commands = new Dictionary<JointRotatorScript, float>();
        private static Calibration calibration;
        private static bool calibrated;
        private static float lastStep = -1f;
        private static bool stepFailed;

        // The wanted direction of the barrel (world space) and how far the aim point is.
        private static Vector3 desired = Vector3.forward;
        private static bool haveDesired;
        private static float aimDistance = 1000f;
        private static float errorDegrees;
        private static bool atLimit;

        // Camera and controls.
        private static InteractiveCameraController controller;
        private static CameraController previousController;
        private static bool decoupled, returning;
        private static Vector2 savedRotation;
        private static float returnVelocityX, returnVelocityY, fovVelocity;
        private static bool fireHeld;
        private static float nextStatus;

        // ---- Access to the game's private members (looked up once) ----------------------------------------------

        private static AccessTools.FieldRef<JointRotatorScript, ConfigurableJoint> jointRef;
        private static AccessTools.FieldRef<JointRotatorScript, float> angleRef, targetRef;
        private static AccessTools.FieldRef<JointRotatorScript, bool> freeSpinRef, floppyRef;
        private static AccessTools.FieldRef<InteractiveCameraController, Vector2> rotationRef;
        private static AccessTools.FieldRef<InteractiveCameraController, float> sensitivityRef;
        private static System.Reflection.MethodInfo selectCamera;
        private static bool refsTried, refsOk;

        private static bool EnsureRefs()
        {
            if (refsTried) return refsOk;
            refsTried = true;
            try
            {
                jointRef = AccessTools.FieldRefAccess<JointRotatorScript, ConfigurableJoint>("_joint");
                angleRef = AccessTools.FieldRefAccess<JointRotatorScript, float>("_angle");
                targetRef = AccessTools.FieldRefAccess<JointRotatorScript, float>("_targetAngle");
                freeSpinRef = AccessTools.FieldRefAccess<JointRotatorScript, bool>("_freeSpin");
                floppyRef = AccessTools.FieldRefAccess<JointRotatorScript, bool>("_floppyJoint");
                rotationRef = AccessTools.FieldRefAccess<InteractiveCameraController, Vector2>("_deltaRotation");
                sensitivityRef = AccessTools.FieldRefAccess<InteractiveCameraController, float>("_rotationSensitivity");
                selectCamera = AccessTools.Method(typeof(CameraManagerScript), "SelectCamera");
                refsOk = selectCamera != null;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the game's rotator or camera changed, it is off (" + e.Message + ")");
                refsOk = false;
            }
            return refsOk;
        }

        // ---- Switching on and off -------------------------------------------------------------------------------

        public static void Toggle()
        {
            if (Active) Deactivate("switched off");
            else Activate();
        }

        private static void Activate()
        {
            try
            {
                FlightSceneScript scene = FlightSceneScript.Instance;
                AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
                if (aircraft == null) { Status = "Off: no vehicle in flight"; return; }
                if (!EnsureRefs()) { Status = "Off: the game changed"; return; }

                Collect(aircraft);
                if (turrets.Count == 0) { Status = "Off: mark a rotator as Turret in the designer"; return; }
                if (pointers.Count == 0) { Status = "Off: mark a cannon as Pointer in the designer"; return; }

                CameraManagerScript cm = CameraManagerScript.Instance;
                if (cm == null) { Status = "Off: no camera"; return; }
                previousController = cm.Controller;
                cm.SelectCameraType<OrbitCameraController>();
                controller = cm.Controller as InteractiveCameraController;
                if (controller == null)
                {
                    RestoreCamera(cm);
                    Status = "Off: the camera view can't be mouse-aimed";
                    return;
                }

                craft = aircraft;
                craft.Controls.SetInputOverride(GameInputs.Instance.FireWeapons, FireFunc);

                commands.Clear();
                calibration = null;
                calibrated = false;
                hullBody = null;
                lastStep = -1f;
                stepFailed = false;
                haveDesired = false;
                decoupled = returning = false;
                fireHeld = false;
                fovVelocity = 0f;
                atLimit = false;
                errorDegrees = 0f;
                nextStatus = 0f;
                controller.MouseLook = true;
                MakeHud();
                Active = true;
                Status = "Calibrating the turret...";
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim on: " + turrets.Count + " turret rotator(s), " + pointers.Count + " pointer cannon(s)");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret aim could not start: " + e);
                Deactivate("error");
            }
        }

        private static void Collect(AircraftScript aircraft)
        {
            turrets.Clear();
            pointers.Clear();
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.Turret != null)
                {
                    JointRotatorScript script = part.GetModifier<JointRotatorScript>();
                    if (script != null && script.JointRotator != null)
                        turrets.Add(new Turret
                        {
                            Script = script,
                            Name = string.IsNullOrEmpty(tags.Label) ? data.PartType.Name : tags.Label,
                            Range = script.JointRotator.Range,
                            Wraps = script.JointRotator.ShortestAngle,
                        });
                }
                if (tags.Pointer != null)
                {
                    CannonScript cannon = part.GetModifier<CannonScript>();
                    if (cannon != null) pointers.Add(cannon);
                }
            }
        }

        public static void Deactivate(string reason)
        {
            bool was = Active;
            Active = false;
            try
            {
                if (craft != null && craft.Controls != null && GameInputs.Instance != null)
                    craft.Controls.SetInputOverride(GameInputs.Instance.FireWeapons, null);
            }
            catch (Exception) { }

            commands.Clear();
            calibration = null;
            haveDesired = false;
            fireHeld = false;
            DestroyHud();

            CameraManagerScript cm = CameraManagerScript.Instance;
            try
            {
                if (controller != null)
                {
                    controller.MouseLook = false;
                    if (sensitivityRef != null) sensitivityRef(controller) = 1f;
                }
            }
            catch (Exception) { }
            try
            {
                if (cm != null)
                {
                    cm.SetCameraFov(cm.CurrentCameraFieldOfView);
                    if (was) RestoreCamera(cm);
                }
            }
            catch (Exception) { }

            controller = null;
            previousController = null;
            craft = null;
            if (was)
            {
                Status = "Off (" + reason + ")";
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim off: " + reason);
            }
        }

        private static void RestoreCamera(CameraManagerScript cm)
        {
            try
            {
                if (previousController != null && cm.Controller != previousController && selectCamera != null)
                    selectCamera.Invoke(cm, new object[] { previousController, false, false });
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: could not put the camera view back: " + e.Message);
            }
        }

        // ---- Every frame ----------------------------------------------------------------------------------------

        public static void Tick()
        {
            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    Key key = ArmorFragmentsPlugin.TurretAimKey.Value;
                    if (key != Key.None && keyboard[key].wasPressedThisFrame) Toggle();
                }
                if (Active) Frame();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret aim failed: " + e);
                Deactivate("error");
            }
        }

        private static void Frame()
        {
            FlightSceneScript scene = FlightSceneScript.Instance;
            CameraManagerScript cm = CameraManagerScript.Instance;
            if (scene == null || scene.LocalPlayer == null || craft == null || scene.LocalPlayer.Aircraft != craft || cm == null)
            {
                Deactivate("the vehicle is gone");
                return;
            }
            if (cm.Controller != controller)
            {
                Deactivate("the camera view was changed");
                return;
            }
            Camera cam = cm.MainCamera;
            if (cam == null) return;

            CannonScript pointer = FirstPointer();
            if (pointer == null)
            {
                Deactivate("the pointer cannon is gone");
                return;
            }

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            bool alt = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
            Key lookKey = ArmorFragmentsPlugin.TurretLookKey.Value;
            bool look = keyboard != null && lookKey != Key.None && keyboard[lookKey].isPressed;
            bool paused = PauseManager.Paused;
            bool free = alt || paused;

            // C: the camera is freed from the turret, which keeps its wanted direction. Let go: the camera comes back to where it was,
            // gradually, and the aim follows it again once it is there.
            if (look && !decoupled)
            {
                decoupled = true;
                returning = false;
                savedRotation = rotationRef(controller);
            }
            else if (!look && decoupled && !returning)
            {
                returning = true;
                returnVelocityX = returnVelocityY = 0f;
            }
            if (returning)
            {
                Vector2 current = rotationRef(controller);
                float targetY = current.y + Mathf.DeltaAngle(current.y, savedRotation.y);
                float x = Mathf.SmoothDamp(current.x, savedRotation.x, ref returnVelocityX, 0.3f);
                float y = Mathf.SmoothDamp(current.y, targetY, ref returnVelocityY, 0.3f);
                rotationRef(controller) = new Vector2(x, y);
                if (Mathf.Abs(x - savedRotation.x) < 0.4f && Mathf.Abs(Mathf.DeltaAngle(y, savedRotation.y)) < 0.4f)
                {
                    returning = false;
                    decoupled = false;
                }
            }

            // The mouse turns the camera through the game's own mouse look (it also hides and locks the cursor); Alt gives the cursor back.
            bool wantLook = !alt && !returning;
            if (controller.MouseLook != wantLook) controller.MouseLook = wantLook;

            // Right button: gradual zoom (and a slower mouse while zoomed, so the aim stays fine).
            float baseFov = cm.CurrentCameraFieldOfView;
            bool zoom = mouse != null && mouse.rightButton.isPressed && !free && !returning;
            float targetFov = zoom ? Mathf.Max(baseFov / ArmorFragmentsPlugin.TurretZoom.Value, 4f) : baseFov;
            float fov = Mathf.SmoothDamp(cam.fieldOfView, targetFov, ref fovVelocity, 0.35f, float.PositiveInfinity, Time.unscaledDeltaTime);
            cm.SetCameraFov(fov);
            sensitivityRef(controller) = Mathf.Clamp(fov / Mathf.Max(baseFov, 1f), 0.08f, 1f);

            // Left button: fire (read by the override of the Fire Weapons input).
            fireHeld = mouse != null && mouse.leftButton.isPressed && !free && !returning;

            // The wanted direction: from the cannon's muzzle to what the camera sees at the middle of the screen (the camera is behind and above the
            // gun, so the gun must aim from where it is, not parallel to the camera).
            Vector3 muzzle = pointer.TipPosition;
            if (!decoupled)
            {
                Vector3 from = cam.transform.position, forward = cam.transform.forward;
                Vector3 point = AimPoint(from, forward);
                Vector3 to = point - muzzle;
                float distance = to.magnitude;
                if (distance < 3f || Vector3.Dot(to, forward) <= 0f)
                {
                    desired = forward;
                    aimDistance = 1000f;
                }
                else
                {
                    desired = to / distance;
                    aimDistance = distance;
                }
                haveDesired = true;
            }

            UpdateHud(cam, muzzle, pointer.transform.forward, look || returning);

            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 0.25f;
                Status = !calibrated ? "Calibrating the turret..."
                    : decoupled ? "Free look (the turret holds its aim)"
                    : atLimit ? "At the turret's limit"
                    : errorDegrees < 0.5f ? "On target"
                    : "Turning (" + errorDegrees.ToString("F1") + " deg off)";
            }
        }

        private static CannonScript FirstPointer()
        {
            foreach (CannonScript cannon in pointers)
                if (cannon != null && cannon.PartScript != null) return cannon;
            return null;
        }

        // What the camera sees along a ray, ignoring this vehicle (the camera sits outside it, so the first hits are usually its own parts).
        private static Vector3 AimPoint(Vector3 from, Vector3 direction)
        {
            const float Far = 3000f;
            int mask = ~LayerMask.GetMask("WorldSpaceUI");
            Vector3 origin = from;
            float travelled = 0f;
            for (int i = 0; i < 24 && travelled < Far; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, direction, out hit, Far - travelled, mask, QueryTriggerInteraction.Ignore))
                    break;
                PartScript part = hit.collider.GetComponentInParent<PartScript>();
                if (part == null || part.Aircraft != craft) return hit.point;
                origin = hit.point + direction * 0.05f;
                travelled += hit.distance + 0.05f;
            }
            return from + direction * Far;
        }

        // Fire Weapons: the left mouse button, or the player's own button.
        private static float? FireFunc()
        {
            bool real = false;
            try
            {
                GameInputs inputs = GameInputs.Instance;
                real = inputs != null && inputs.FireWeapons.Enabled && inputs.FireWeapons.GetButton();
            }
            catch (Exception) { }
            return fireHeld || real ? 1f : 0f;
        }

        // ---- The turret ------------------------------------------------------------------------------------------

        // Called from every rotator's physics step (see TurretRotatorPatch): the first one of a step works everything out, each marked rotator
        // then takes its own angle.
        internal static void OnRotatorFixedUpdate(JointRotatorScript script)
        {
            if (!Active || craft == null) return;
            float now = Time.fixedTime;
            if (now != lastStep)
            {
                lastStep = now;
                try
                {
                    Step(Time.fixedDeltaTime);
                }
                catch (Exception e)
                {
                    if (!stepFailed) ArmorFragmentsPlugin.Log.LogError("Turret aim step failed: " + e);
                    stepFailed = true;
                }
            }
            float target;
            if (commands.TryGetValue(script, out target) && targetRef != null) targetRef(script) = target;
        }

        private static bool Moves(Turret turret)
        {
            JointRotatorScript script = turret.Script;
            return script != null && !script.IsDamaged && jointRef(script) != null && !freeSpinRef(script) && !floppyRef(script);
        }

        private static Vector3 AxisOf(JointRotatorScript script)
        {
            ConfigurableJoint joint = jointRef(script);
            return joint.transform.TransformVector(-joint.axis).normalized;
        }

        private static void HoldAll()
        {
            foreach (Turret turret in turrets)
                if (turret.Script != null) commands[turret.Script] = angleRef(turret.Script);
        }

        private static void Step(float dt)
        {
            CannonScript pointer = FirstPointer();
            if (pointer == null) return;
            Vector3 barrel = pointer.transform.forward;

            if (!calibrated)
            {
                Calibrate(dt, barrel);
                return;
            }
            if (!haveDesired)
            {
                HoldAll();
                return;
            }

            Vector3 rotation = RotationVector(barrel, desired);
            errorDegrees = rotation.magnitude * Mathf.Rad2Deg;

            List<Turret> use = new List<Turret>();
            foreach (Turret turret in turrets)
                if (turret.Usable && Moves(turret)) use.Add(turret);
            if (use.Count == 0)
            {
                HoldAll();
                return;
            }

            // The rotation split between the rotators by their axes: the small least-squares problem  sum(theta_i * axis_i) = rotation.
            Vector3[] axes = new Vector3[use.Count];
            for (int i = 0; i < use.Count; i++) axes[i] = AxisOf(use[i].Script);
            double[] theta = errorDegrees < 0.02f ? new double[use.Count] : Solve(axes, rotation);

            // Stabilizer: while the hull turns, pitches or rolls, the rotators must turn the other way at the same rate, or the barrel would
            // always trail behind it (the error alone only catches up). The same split, for the hull's angular velocity.
            double[] hull = new double[use.Count];
            if (hullBody != null) hull = Solve(axes, -hullBody.angularVelocity);

            float gain = 1f - Mathf.Exp(-dt / Mathf.Max(ArmorFragmentsPlugin.TurretResponse.Value, 0.02f));
            bool limit = false;
            for (int i = 0; i < use.Count; i++)
            {
                Turret turret = use[i];
                float current = angleRef(turret.Script);
                float target = current + turret.Sign * ((float)theta[i] * gain + (float)hull[i] * dt) * Mathf.Rad2Deg;
                if (!turret.Wraps)
                {
                    float clamped = Mathf.Clamp(target, -turret.Range, turret.Range);
                    if (Mathf.Abs(clamped - target) > 0.001f && Mathf.Abs(current - clamped) < 0.1f && Mathf.Abs((float)theta[i]) * Mathf.Rad2Deg > 0.3f)
                        limit = true;
                    target = clamped;
                }
                commands[turret.Script] = target;
            }
            // The rotators that don't take part keep their place.
            foreach (Turret turret in turrets)
                if (!use.Contains(turret) && turret.Script != null) commands[turret.Script] = angleRef(turret.Script);
            atLimit = limit;
        }

        // Which way does each rotator turn the barrel? Move it a few degrees, wait for the barrel to follow, measure. A rotator that doesn't
        // move the pointer cannon (not between the hull and the barrel, broken) is left out.
        private static void Calibrate(float dt, Vector3 barrel)
        {
            if (calibration == null)
            {
                Turret next = null;
                foreach (Turret turret in turrets) if (!turret.Done) { next = turret; break; }
                if (next == null)
                {
                    calibrated = true;
                    int usable = 0;
                    foreach (Turret turret in turrets) if (turret.Usable) usable++;
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: " + usable + " of " + turrets.Count + " rotator(s) turn the pointer cannon");
                    if (usable == 0) { Deactivate("no marked rotator moves the pointer cannon"); return; }
                    FindHull();
                    return;
                }
                if (!Moves(next) || next.Range < 1f)
                {
                    next.Done = true;
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim: rotator '" + next.Name + "' can't be driven (broken, free spin, floppy or no range)");
                    return;
                }
                float angle0 = angleRef(next.Script);
                calibration = new Calibration
                {
                    Turret = next,
                    Angle0 = angle0,
                    Delta = next.Wraps || angle0 + 5f <= next.Range ? 5f : -5f,
                    Barrel0 = barrel,
                    Axis = AxisOf(next.Script),
                };
            }

            Calibration c = calibration;
            foreach (Turret turret in turrets)
                if (turret.Script != null) commands[turret.Script] = turret == c.Turret ? c.Angle0 + c.Delta : angleRef(turret.Script);

            c.Timer += dt;
            if (c.Phase == 0)
            {
                bool reached = Mathf.Abs(angleRef(c.Turret.Script) - (c.Angle0 + c.Delta)) < 0.05f;
                if (reached || c.Timer > 4f) { c.Phase = 1; c.Timer = 0f; }
                return;
            }
            if (c.Timer < 0.35f) return;

            Vector3 moved = RotationVector(c.Barrel0, barrel);
            float along = Vector3.Dot(moved, c.Axis);
            float expected = c.Delta * Mathf.Deg2Rad;
            c.Turret.Done = true;
            if (Mathf.Abs(along) >= 0.3f * Mathf.Abs(expected))
            {
                c.Turret.Sign = along / expected > 0f ? 1f : -1f;
                c.Turret.Usable = true;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: rotator '" + c.Turret.Name + "' turns the barrel " + (along * Mathf.Rad2Deg).ToString("F1") +
                    " deg for " + c.Delta.ToString("F0") + " deg (sign " + (c.Turret.Sign > 0f ? "+" : "-") + ", range " +
                    (c.Turret.Wraps ? "all the way round" : c.Turret.Range.ToString("F0") + " deg") + ")");
            }
            else
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: rotator '" + c.Turret.Name + "' did not move the pointer cannon (" +
                    (along * Mathf.Rad2Deg).ToString("F2") + " deg for " + c.Delta.ToString("F0") + "): it is left out");
            }
            calibration = null;
        }

        // The hull: from the pointer cannon's body, each rotator that has the body on one side leads to the body on its other side, and so on
        // to the vehicle's base. That body's angular velocity is what the stabilizer works against.
        private static Rigidbody hullBody;

        private static Rigidbody BodyOf(PartScript part)
        {
            return part != null && part.Body != null && part.Body.RigidBody != null ? part.Body.RigidBody.PhysxRigidBody : null;
        }

        private static void FindHull()
        {
            hullBody = null;
            CannonScript pointer = FirstPointer();
            Rigidbody current = pointer != null ? BodyOf(pointer.PartScript) : null;
            if (current == null) return;
            List<Turret> left = turrets.FindAll(t => t.Usable && t.Script != null && jointRef(t.Script) != null);
            for (int guard = 0; guard < 8 && left.Count > 0; guard++)
            {
                Turret found = null;
                Rigidbody parent = null;
                foreach (Turret turret in left)
                {
                    ConfigurableJoint joint = jointRef(turret.Script);
                    Rigidbody own = joint.GetComponent<Rigidbody>(), other = joint.connectedBody;
                    if (own == current) { found = turret; parent = other; break; }
                    if (other == current) { found = turret; parent = own; break; }
                }
                if (found == null || parent == null) break;
                left.Remove(found);
                current = parent;
            }
            hullBody = current;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the hull is the body '" + (current != null ? current.name : "?") + "' (" +
                                             (left.Count == 0 ? "every rotator is on the way to it" : left.Count + " rotator(s) are not on the way") + ")");
        }

        // The rotation (axis x angle, radians) that takes direction a onto direction b.
        private static Vector3 RotationVector(Vector3 a, Vector3 b)
        {
            Vector3 cross = Vector3.Cross(a, b);
            float sin = cross.magnitude;
            if (sin < 1e-7f) return Vector3.zero;
            return cross / sin * Mathf.Atan2(sin, Vector3.Dot(a, b));
        }

        // Least squares: theta minimizing |sum(theta_i * axes_i) - target|, with a little damping so a rotator whose axis is almost
        // along the barrel (it can't do anything useful) is not asked for wild angles.
        private static double[] Solve(Vector3[] axes, Vector3 target)
        {
            int n = axes.Length;
            double[,] m = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) m[i, j] = Vector3.Dot(axes[i], axes[j]) + (i == j ? 0.02 : 0.0);
                m[i, n] = Vector3.Dot(axes[i], target);
            }
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < n; row++) if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
                if (pivot != col)
                    for (int k = 0; k <= n; k++) { double swap = m[col, k]; m[col, k] = m[pivot, k]; m[pivot, k] = swap; }
                double diagonal = m[col, col];
                if (Math.Abs(diagonal) < 1e-9) continue;
                for (int row = 0; row < n; row++)
                {
                    if (row == col) continue;
                    double factor = m[row, col] / diagonal;
                    for (int k = col; k <= n; k++) m[row, k] -= factor * m[col, k];
                }
            }
            double[] result = new double[n];
            for (int i = 0; i < n; i++) result[i] = Math.Abs(m[i, i]) < 1e-9 ? 0.0 : m[i, n] / m[i, i];
            return result;
        }

        // ---- The circle and the marker on the screen ----------------------------------------------------------------

        private static GameObject hudRoot;
        private static RectTransform ringRect, markerRect;
        private static Image ringImage, markerImage;
        private static Sprite ringSprite, dotSprite;

        private static Sprite MakeSprite(int size, Func<float, float> alphaAtRadius)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "ArmorFragments turret hud",
            };
            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float alpha = Mathf.Clamp01(alphaAtRadius(Mathf.Sqrt(dx * dx + dy * dy)));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Image MakeImage(Transform parent, string name, Sprite sprite, float size, out RectTransform rect)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            rect = go.AddComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            Image image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.raycastTarget = false;
            return image;
        }

        private static void MakeHud()
        {
            DestroyHud();
            if (ringSprite == null)
                ringSprite = MakeSprite(128, r => 1f - Mathf.SmoothStep(0.03f, 0.075f, Mathf.Abs(r - 0.88f)));
            if (dotSprite == null)
                dotSprite = MakeSprite(48, r => (1f - Mathf.SmoothStep(0.5f, 0.7f, r)) * 0.9f + (1f - Mathf.SmoothStep(0.86f, 0.95f, Mathf.Abs(r - 0.9f))) * 0.3f);

            hudRoot = new GameObject("ArmorFragments turret aim");
            Canvas canvas = hudRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            ringImage = MakeImage(hudRoot.transform, "circle", ringSprite, 64f, out ringRect);
            markerImage = MakeImage(hudRoot.transform, "gun marker", dotSprite, 16f, out markerRect);
        }

        private static void DestroyHud()
        {
            if (hudRoot != null) UnityEngine.Object.Destroy(hudRoot);
            hudRoot = null;
            ringRect = markerRect = null;
            ringImage = markerImage = null;
        }

        // The circle is where the turret is told to point (the aim point, seen from the camera); the marker is where the barrel really points at
        // that distance. They meet when the gun is on target.
        private static void UpdateHud(Camera cam, Vector3 muzzle, Vector3 barrel, bool freeLook)
        {
            if (hudRoot == null || ringImage == null) return;
            bool show = haveDesired;
            ringImage.enabled = markerImage.enabled = show;
            if (!show) return;

            Vector3 ring = cam.WorldToScreenPoint(muzzle + desired * aimDistance);
            Vector3 marker = cam.WorldToScreenPoint(muzzle + barrel.normalized * aimDistance);
            ringImage.enabled = ring.z > 0f;
            markerImage.enabled = marker.z > 0f;
            ringRect.position = new Vector3(ring.x, ring.y, 0f);
            markerRect.position = new Vector3(marker.x, marker.y, 0f);

            ringImage.color = freeLook ? new Color(0.45f, 0.85f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.85f);
            markerImage.color = !calibrated ? new Color(0.7f, 0.7f, 0.7f, 0.9f)
                : atLimit ? new Color(1f, 0.3f, 0.25f, 0.95f)
                : errorDegrees < 0.6f ? new Color(0.35f, 1f, 0.45f, 0.95f)
                : new Color(1f, 0.8f, 0.25f, 0.95f);
        }
    }

    // Each marked rotator takes the angle the aim works out, in the physics step that moves it.
    [HarmonyPatch(typeof(JointRotatorScript), "OnFixedUpdate")]
    internal static class TurretRotatorPatch
    {
        private static void Prefix(JointRotatorScript __instance)
        {
            TurretAim.OnRotatorFixedUpdate(__instance);
        }
    }
}
