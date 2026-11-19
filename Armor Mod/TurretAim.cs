using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Combat;
using Assets.Scripts.Flight.Cameras;
using Assets.Scripts.Input;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace ArmorFragments
{
    internal enum TurretFireMouse { Left, Middle }

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
            public bool Usable;          // moves at least one of the guns
            public float[] Signs;        // per gun: +1 / -1 the gun's barrel turns this way about Axis when the angle grows, 0 = does not move it
            public float[] Scales;       // per gun: how many degrees the barrel turns for a degree of the angle (measured; a bit under 1)
            public float[] Lags;         // per gun: how long (s) the barrel takes to follow the angle (measured; 0 = not known)
            public bool Solo;            // the batch test could not place it: it is turned alone
            public HashSet<int> Expect;  // the guns it should move, from the way the bodies are joined (null = can't tell)
            public readonly List<float> HistoryTime = new List<float>();    // the angle the rotator had, step by step (the last second)
            public readonly List<float> HistoryAngle = new List<float>();
        }

        // A turret: a cannon the mouse aim points, in a turret group (the turrets of the active group are aimed at the same point at the same time;
        // they are equals). Each is turned by its own rotators.
        private sealed class Gun
        {
            public CannonScript Cannon;
            public int GroupId;
            public bool FiresByKey;    // fires with the fire key instead of the mouse button
            public float NextFire;     // Time.time of the next shot
            public CameraVantageScript Sight;
            public readonly List<FixedCannon> FixedCannons = new List<FixedCannon>();
            public readonly List<Turret> Drivers = new List<Turret>();   // the rotators that turn this turret and no earlier turret of its group
            public Rigidbody Hull;
            public Vector3 PreviousBarrel;
            public bool BarrelKnown;
            public Vector3 Spin;       // the barrel's angular velocity (rad/s), smoothed
            public Vector3 HullSpin;   // the hull's angular velocity (rad/s), smoothed
            public Vector3 FastSpin;   // the barrel's angular velocity (rad/s), hardly smoothed: the braking term works from it
            public Vector3 Want;       // where the gun should point (the cursor), world direction: what firing is judged against
            public float AimError;     // degrees between the barrel and Want
            public float AimStamp = -10f;   // Time.fixedTime of the last measure
            public float Lag;          // how long the barrel takes to follow its rotators (s), the slowest of them; 0 = not measured

            // Swinging (each turret has its own): the error changing side over and over cuts this turret's gain.
            public float PreviousTheta;   // the last signed gap (degrees) of the rotator with the biggest one
            public int Share = 1;         // turrets of the active group on the same hull body (itself included)
            public float GainScale = 1f;
            public float LagBoost;        // seconds added to the measured lag after the turret swung (the measured one was too short at the speed it swings)
            public float LastSwing, LastRecover;
            public readonly List<float> Flips = new List<float>();
        }

        // A cannon fixed to a turret: it moves with it and fires with its own input, but nothing aims it by itself.
        private sealed class FixedCannon
        {
            public CannonScript Cannon;
            public bool ByKey;
            public float NextFire;
        }

        private static readonly List<int> groupIds = new List<int>();
        private static int activeGroup;                 // index in groupIds
        private static Gun sightGun;                    // the turret whose sight camera is the view now

        private static int ActiveGroupId { get { return groupIds.Count > 0 ? groupIds[Mathf.Clamp(activeGroup, 0, groupIds.Count - 1)] : 0; } }
        private static bool Live(Gun gun) { return gun != null && gun.Cannon != null && gun.Cannon.PartScript != null; }
        // In the test mode only one turret of the active group counts as active: it is the only one controlled, mirrored in the share of the
        // gain, and fired; the others hold where they are.
        private static int testIndex;   // the position of the turret under test among the turrets of the active group

        private static int GroupCount()
        {
            int n = 0;
            foreach (Gun gun in guns) if (gun.GroupId == ActiveGroupId) n++;
            return n;
        }

        private static Gun GroupGun(int position)
        {
            int n = 0;
            foreach (Gun gun in guns)
            {
                if (gun.GroupId != ActiveGroupId) continue;
                if (n == position) return gun;
                n++;
            }
            return null;
        }

        private static bool InActiveGroup(Gun gun)
        {
            if (gun.GroupId != ActiveGroupId) return false;
            if (!ArmorFragmentsPlugin.TurretTestSingle.Value) return true;
            return gun == GroupGun(Mathf.Clamp(testIndex, 0, Mathf.Max(0, GroupCount() - 1)));
        }

        public static string TestStatus
        {
            get
            {
                if (!ArmorFragmentsPlugin.TurretTestSingle.Value) return "Off (all the turrets of the group are controlled)";
                int n = GroupCount();
                return n == 0 ? "No turret" : "Turret " + (Mathf.Clamp(testIndex, 0, n - 1) + 1) + " of " + n + " in group " + ActiveGroupId;
            }
        }

        // The < and > buttons: the next turret of the group is the one controlled (and the mode is switched on).
        public static void TestStep(int delta)
        {
            int n = GroupCount();
            if (n == 0) return;
            ArmorFragmentsPlugin.TurretTestSingle.Value = true;
            testIndex = ((Mathf.Clamp(testIndex, 0, n - 1) + delta) % n + n) % n;
            ResetForTestChange();
            if (Active) Notice("testing turret " + (testIndex + 1) + " of " + n);
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: only turret " + (testIndex + 1) + " of " + n + " in group " + ActiveGroupId + " is controlled now");
        }

        private static bool testWasOn;

        private static void ResetForTestChange()
        {
            CameraManagerScript cm = CameraManagerScript.Instance;
            if (sight && cm != null) LeaveSight(cm);
            foreach (Gun gun in guns) { gun.BarrelKnown = false; ResetSwing(gun); }
            desiredTime.Clear();
            desiredDirection.Clear();
        }

        // The turret the screen follows: the one whose sight camera is on, else the first live one of the active group.
        private static Gun Lead()
        {
            if (sight && Live(sightGun) && InActiveGroup(sightGun)) return sightGun;
            foreach (Gun gun in guns)
                if (InActiveGroup(gun) && Live(gun)) return gun;
            return null;
        }

        // The velocities the control uses are smoothed over this long. A vibrating engine makes the hull's measured angular velocity flip sign
        // almost every physics step (tens of degrees a second); fed straight into the targets, that is a jitter bigger than the small errors
        // the turret is trying to close (found in a recording), so small angles were never followed.
        private const float VelocityFilterSeconds = 0.12f;

        // The hull's own spin is smoothed more: it only feeds the stabilizer, and the turret can't follow a shake faster than about a turn a
        // second anyway (a rotator turns at 10 degrees a second or so). A ship's hull shaking at 2 Hz fed through a short filter made the
        // turrets chase it back and forth at their speed limit, all fourteen together, which shook the ship more (found in a recording).
        private const float HullFilterSeconds = 0.4f;

        // The gain never goes below this per physics step (0.4 a second), however many turrets share a hull and however often the swing detector cut it.
        private const float MinimumGain = 0.004f;

        // The lag the predictor assumes is never less than this, whatever was measured. A turret whose joint is springy measures a tiny lag in the
        // slow calibration move (20 ms in a recording) yet rings at 2-3 Hz when asked to reverse (its barrel moved three times as far as the ring
        // angle, and the lone turret swung for 7 s until the swing detector had added enough lag by itself). In a model of a springy joint, a
        // predictor that assumes about 130 ms ends the swing for every damping from 0.1 to 0.9 and costs a quarter of a second on a 5 degree move.
        private const float MinimumAssumedLag = 0.13f;
        private const float SmallestGapDegrees = 0.12f;   // below this gap the turret rests (the settled gap is noisy by about a tenth of a degree)
        private const float MicroStepDegrees = 0.02f;     // the least a step asks for above it

        // The braking term needs the gun's speed without much delay (a delay in it is what lets a swing grow): only a few physics steps of smoothing.
        private const float BrakeFilterSeconds = 0.03f;

        private static Vector3 Smooth(Vector3 current, Vector3 sample, bool first, float dt, float tau = VelocityFilterSeconds)
        {
            if (first) return sample;
            float a = 1f - Mathf.Exp(-dt / tau);
            return current + (sample - current) * a;
        }

        // One rotator being turned a few degrees to see what it moves.
        private sealed class Jog
        {
            public Turret Turret;
            public float Angle0, Delta;
            public Vector3 Axis;
            public HashSet<int> Expect;   // the guns it should move (from the way the bodies are joined); null = any (it is turned alone)
        }

        private struct Sample
        {
            public float Time;
            public float[] Angles;        // per jog
            public Vector3[] Barrels;     // per gun
        }

        // A batch of rotators that move different guns is turned all at once (a ship with many turrets would take half a minute one by one);
        // a rotator the batch can't place is turned alone, as before.
        private sealed class Calibration
        {
            public readonly List<Jog> Jogs = new List<Jog>();
            public int Phase;            // 0: moving, 1: settling
            public float Timer, Elapsed;
            public Vector3[] Barrels0;   // per gun
            public readonly List<Sample> Samples = new List<Sample>();
        }

        private static AircraftScript craft;
        private static readonly List<Gun> guns = new List<Gun>();
        private static readonly List<Turret> turrets = new List<Turret>();
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
        private static bool fireMouse, fireKey;   // the two fire inputs, as read this frame
        private static float nextStatus;

        // ---- Access to the game's private members (looked up once) ----------------------------------------------

        private static AccessTools.FieldRef<JointRotatorScript, ConfigurableJoint> jointRef;
        private static AccessTools.FieldRef<JointRotatorScript, float> angleRef, targetRef;
        private static AccessTools.FieldRef<JointRotatorScript, bool> freeSpinRef, floppyRef;
        private static AccessTools.FieldRef<InteractiveCameraController, Vector2> rotationRef;
        private static AccessTools.FieldRef<InteractiveCameraController, float> sensitivityRef;
        private static System.Reflection.MethodInfo selectCamera;
        private static AccessTools.FieldRef<OrbitCameraController, Func<Transform>> orbitTargetRef;
        private static AccessTools.FieldRef<OrbitCameraController, bool> orbitCenterRef;
        private static AccessTools.FieldRef<CameraManagerScript, CameraController> orbitCameraRef;   // the game types it as CameraController
        private static AccessTools.FieldRef<CameraVantageScript, CameraController> vantageControllerRef;
        private static AccessTools.FieldRef<InteractiveCameraController, float> fovTargetRef;
        private static bool refsTried, refsOk, orbitRefsOk;

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
            try
            {
                vantageControllerRef = AccessTools.FieldRefAccess<CameraVantageScript, CameraController>("_cameraController");
                fovTargetRef = AccessTools.FieldRefAccess<InteractiveCameraController, float>("_fovZoomTarget");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the sight camera can't be used (" + e.Message + ")");
            }
            try
            {
                orbitTargetRef = AccessTools.FieldRefAccess<OrbitCameraController, Func<Transform>>("_targetTransform");
                orbitCenterRef = AccessTools.FieldRefAccess<OrbitCameraController, bool>("_centerOnRigidBody");
                orbitRefsOk = true;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the orbit camera's pivot can't be moved (" + e.Message + "), it stays where the game puts it");
            }
            try
            {
                orbitCameraRef = AccessTools.FieldRefAccess<CameraManagerScript, CameraController>("_orbitCamera");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: a stale orbit camera pivot can't be repaired (" + e.Message + ")");
            }
            return refsOk;
        }

        // ---- The orbit camera's pivot ---------------------------------------------------------------------------
        // The point the orbit camera turns around (where it sits at full zoom) is, in the game, the vehicle's own position. Here it is a point of
        // our own, centered on the vehicle and TurretAim/CameraHeight meters above the highest point of the vehicle, so the camera looks over
        // the turret from above.

        private static OrbitCameraController orbit;
        private static Func<Transform> originalTarget;
        private static bool originalCenter;
        private static GameObject pivot;
        private static readonly List<Collider> bodyColliders = new List<Collider>();
        private static float nextColliders;

        // What the camera's Update gets for one call: the game's own target and centering are put back at the end of the call.
        internal sealed class PivotSwap
        {
            public Func<Transform> Target;
            public bool Center;
        }

        private static Func<Transform> pivotTarget;

        private static void TakePivot(OrbitCameraController controllerToMove)
        {
            if (!orbitRefsOk || controllerToMove == null) return;
            orbit = controllerToMove;
            pivot = new GameObject("ArmorFragments turret camera pivot");
            pivot.transform.position = craft.transform.position;
            Transform pivotTransform = pivot.transform;
            pivotTarget = () => pivotTransform;
            nextColliders = 0f;
            pivotYKnown = false;
            UpdatePivot();
        }

        private static void ReleasePivot()
        {
            if (pivot != null) UnityEngine.Object.Destroy(pivot);
            pivot = null;
            pivotTarget = null;
            orbit = null;
            bodyColliders.Clear();
        }

        internal static PivotSwap SwapPivotIn(OrbitCameraController instance)
        {
            if (!Active || pivot == null || pivotTarget == null || !orbitRefsOk || instance != orbit) return null;
            PivotSwap swap = new PivotSwap { Target = orbitTargetRef(instance), Center = orbitCenterRef(instance) };
            orbitTargetRef(instance) = pivotTarget;
            orbitCenterRef(instance) = false;
            return swap;
        }

        internal static void SwapPivotOut(OrbitCameraController instance, PivotSwap swap)
        {
            if (swap == null || instance == null) return;
            orbitTargetRef(instance) = swap.Target;
            orbitCenterRef(instance) = swap.Center;
        }

        // An earlier copy of the plugin (before the pivot was swapped only for the duration of a call) replaced the orbit camera's target for
        // good, and when it was reloaded without taking it back, the camera kept following a pivot that stayed where it was while the vehicle
        // drove away. Whatever target comes from this plugin's code is put back as the game makes it: the main cockpit, centered on its body.
        private static float nextRepair;

        private static void RepairOrbitCamera(CameraManagerScript cm)
        {
            if (!orbitRefsOk || cm == null || orbitCameraRef == null) return;
            OrbitCameraController found = orbitCameraRef(cm) as OrbitCameraController;
            if (found == null) return;
            Func<Transform> target = orbitTargetRef(found);
            if (target == null || target.Method == null || target.Method.DeclaringType == null) return;
            if (target.Method.DeclaringType.Assembly.GetName().Name != typeof(TurretAim).Assembly.GetName().Name) return;

            FlightSceneScript scene = FlightSceneScript.Instance;
            AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
            PartScript cockpit = aircraft != null ? aircraft.MainCockpit : null;
            if (cockpit == null) return;
            orbitTargetRef(found) = () => cockpit.transform;
            orbitCenterRef(found) = true;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the orbit camera was still following a pivot an earlier copy of the plugin left behind; it follows the vehicle again");
        }

        private static void UpdatePivot()
        {
            if (pivot == null || craft == null) return;

            // The vehicle's colliders, looked up again now and then (parts get destroyed and torn off).
            if (Time.unscaledTime >= nextColliders)
            {
                nextColliders = Time.unscaledTime + 2f;
                bodyColliders.Clear();
                // The gun (the body the pointer cannons are in) is left out of the highest point: its top moves with the elevation, and the pivot
                // that follows it moves the camera, which changes where the gun is told to point, which moves its top... With the highest point
                // jumping between the barrel's tip and another part, the aim flipped between two positions.
                HashSet<Rigidbody> gunBodies = new HashSet<Rigidbody>();
                foreach (Gun gun in guns)
                {
                    Rigidbody body = gun.Cannon != null ? BodyOf(gun.Cannon.PartScript) : null;
                    if (body != null) gunBodies.Add(body);
                }
                foreach (PartData data in craft.Aircraft.Assembly.Parts)
                {
                    PartScript part = data != null ? data.PartScript : null;
                    if (part == null) continue;
                    foreach (Collider collider in part.GetComponentsInChildren<Collider>())
                    {
                        if (collider == null || collider.isTrigger) continue;
                        Rigidbody attached = collider.attachedRigidbody;
                        if (attached != null && gunBodies.Contains(attached)) continue;
                        bodyColliders.Add(collider);
                    }
                }
            }

            float top = float.NegativeInfinity;
            Vector3 center = Vector3.zero;
            int count = 0;
            foreach (Collider collider in bodyColliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Bounds bounds = collider.bounds;
                if (bounds.max.y > top) top = bounds.max.y;
                center += bounds.center;
                count++;
            }
            if (count == 0) return;
            center /= count;

            // The height eases toward its target (about half a second), so even a jump of the highest point from one part to another moves the
            // camera gently and can't set the aim swinging.
            float wanted = top + ArmorFragmentsPlugin.TurretCameraHeight.Value;
            pivotY = pivotYKnown ? Mathf.Lerp(pivotY, wanted, 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.5f)) : wanted;
            pivotYKnown = true;

            // Centered on the vehicle: the hull's centre of mass once it is known, else the middle of its colliders.
            // The body the turrets turn on can shake (a hull part on soft joints rocking 5 degrees moves its centre of mass, and the camera with it);
            // the centre of mass of all the bodies joined to it does not move when they only push each other.
            Vector3 middle = hullBody != null ? hullBody.worldCenterOfMass : center;
            if (pivotBodies.Count > 0)
            {
                Vector3 sum = Vector3.zero;
                float mass = 0f;
                foreach (Rigidbody body in pivotBodies)
                {
                    if (body == null || !body.gameObject.activeInHierarchy) continue;
                    sum += body.worldCenterOfMass * body.mass;
                    mass += body.mass;
                }
                if (mass > 0f) middle = sum / mass;
            }
            pivot.transform.position = new Vector3(middle.x, pivotY, middle.z);
        }

        private static float pivotY;
        private static bool pivotYKnown;
        private static readonly List<Rigidbody> pivotBodies = new List<Rigidbody>();   // every body joined to the turrets' hull (the vehicle)

        // ---- The optical sight --------------------------------------------------------------------------------
        // The camera part picked on the pointer cannon in the designer (Pointer > Pick sight camera). The sight key switches to its view (the game's
        // own controller for that camera part, a First Person one looks along the part) and back to the orbit view. Through the sight the mouse
        // turns the wanted direction and the turret follows it; the view is the camera on the gun, so it shows where the gun really points.

        private static CameraVantageScript sightScript;
        private static InteractiveCameraController sightController;
        private static bool sight;
        private static float sightBase = -1f;   // the sight's normal field of view, set with the wheel (-1: the game's)
        private static string notice;
        private static float noticeUntil;

        private static void Notice(string text)
        {
            notice = text;
            noticeUntil = Time.unscaledTime + 4f;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: " + text);
        }

        // The game asks its camera controller to look after the cursor every frame; in the sight this code does.
        internal static bool OwnsCursor(InteractiveCameraController asked)
        {
            return Active && sight && asked == sightController;
        }

        private static void EnterSight(CameraManagerScript cm)
        {
            // The first turret of the active group that has a sight camera.
            Gun sightTurret = null;
            foreach (Gun gun in guns)
                if (InActiveGroup(gun) && Live(gun) && gun.Sight != null) { sightTurret = gun; break; }
            sightScript = sightTurret != null ? sightTurret.Sight : null;
            if (sightScript == null || vantageControllerRef == null || fovTargetRef == null)
            {
                Notice("no sight camera on this group's turrets: pick one on a turret in the designer (Turret group > Pick sight camera)");
                return;
            }
            InteractiveCameraController target = vantageControllerRef(sightScript) as InteractiveCameraController;
            if (target == null)
            {
                Notice("the sight camera's view can't be used (the camera part's view mode should be First Person)");
                return;
            }
            selectCamera.Invoke(cm, new object[] { target, false, false });
            if (cm.Controller != target)
            {
                Notice("the game did not switch to the sight camera");
                return;
            }
            sightController = target;
            sightGun = sightTurret;
            sight = true;
            target.MouseLook = false;
            rotationRef(target) = Vector2.zero;
            if (!haveDesired)
            {
                desired = sightTurret.Cannon.transform.forward;
                haveDesired = true;
            }
            decoupled = returning = false;
            fovVelocity = 0f;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: looking through the sight camera");
        }

        // Back to the orbit view, looking where the gun is told to point.
        private static void LeaveSight(CameraManagerScript cm)
        {
            if (!sight) return;
            // The sight view goes back to looking along the gun for next time.
            if (sightController != null)
            {
                sightController.MouseLook = false;
                rotationRef(sightController) = Vector2.zero;
            }
            decoupled = returning = false;
            sight = false;
            sightController = null;
            sightGun = null;
            fovVelocity = 0f;
            try
            {
                selectCamera.Invoke(cm, new object[] { controller, false, false });
                Vector3 direction = haveDesired ? desired : Vector3.forward;
                Vector2 now = rotationRef(controller);
                float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
                rotationRef(controller) = new Vector2(Mathf.Clamp(pitch, -89f, 89f), now.y + Mathf.DeltaAngle(now.y, yaw));
                controller.MouseLook = true;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: could not leave the sight: " + e.Message);
            }
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: back to the orbit view");
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
                if (guns.Count == 0) { Status = "Off: create a turret group on a cannon in the designer"; return; }
                activeGroup = 0;
                sightGun = null;

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
                TakePivot(controller as OrbitCameraController);

                commands.Clear();
                calibration = null;
                calibrated = false;
                graphTried = false;
                edges = null;
                calBase = null;
                hullBody = null;
                sight = false;
                sightController = null;
                sightGun = null;
                notice = null;
                foreach (Gun gun in guns) ResetSwing(gun);
                desiredTime.Clear();
                desiredDirection.Clear();
                lastStep = -1f;
                stepFailed = false;
                haveDesired = false;
                decoupled = returning = false;
                fireMouse = fireKey = false;
                fovVelocity = 0f;
                atLimit = false;
                errorDegrees = 0f;
                nextStatus = 0f;
                controller.MouseLook = true;
                MakeHud();
                Active = true;
                Status = "Calibrating the turret...";
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim on: " + turrets.Count + " turret rotator(s), " + guns.Count + " turret(s) in " + groupIds.Count +
                                                 " group(s)");
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
            guns.Clear();
            groupIds.Clear();
            sightScript = null;
            sightGun = null;
            Dictionary<int, PartData> byId = new Dictionary<int, PartData>();
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
                if (data != null && !byId.ContainsKey(data.Id)) byId.Add(data.Id, data);

            List<PointerTag> pointerTags = new List<PointerTag>();
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
                    if (cannon != null)
                    {
                        guns.Add(new Gun { Cannon = cannon, GroupId = Mathf.Max(1, tags.Pointer.Group), FiresByKey = tags.Pointer.ByKey });
                        pointerTags.Add(tags.Pointer);
                    }
                }
            }

            // The sight cameras and the fixed cannons of each turret (a cannon is in one place only: a turret, or fixed to one turret).
            HashSet<CannonScript> used = new HashSet<CannonScript>();
            foreach (Gun gun in guns) used.Add(gun.Cannon);
            for (int i = 0; i < guns.Count; i++)
            {
                PointerTag tag = pointerTags[i];
                Gun gun = guns[i];
                PartData cameraPart;
                if (tag.Camera.HasValue && byId.TryGetValue(tag.Camera.Value, out cameraPart) && cameraPart.PartScript != null)
                    gun.Sight = cameraPart.PartScript.GetModifier<CameraVantageScript>();
                foreach (int id in tag.Fixed)
                {
                    PartData fixedPart;
                    if (!byId.TryGetValue(id, out fixedPart) || fixedPart.PartScript == null) continue;
                    CannonScript fixedCannon = fixedPart.PartScript.GetModifier<CannonScript>();
                    if (fixedCannon == null || !used.Add(fixedCannon)) continue;
                    gun.FixedCannons.Add(new FixedCannon { Cannon = fixedCannon, ByKey = tag.FixedByKey.Contains(id) });
                }
                if (!groupIds.Contains(gun.GroupId)) groupIds.Add(gun.GroupId);
            }
            groupIds.Sort();
            foreach (Turret turret in turrets)
            {
                turret.Signs = new float[guns.Count];
                turret.Scales = new float[guns.Count];
                turret.Lags = new float[guns.Count];
            }
        }

        // The next turret group takes the mouse aim (the key in the config).
        private static void NextGroup(CameraManagerScript cm)
        {
            if (sight) LeaveSight(cm);
            activeGroup = (activeGroup + 1) % groupIds.Count;
            foreach (Gun gun in guns) { gun.BarrelKnown = false; ResetSwing(gun); }
            desiredTime.Clear();
            desiredDirection.Clear();
            int count = 0;
            foreach (Gun gun in guns) if (InActiveGroup(gun)) count++;
            Notice("turret group " + ActiveGroupId + " (" + count + (count == 1 ? " turret)" : " turrets)"));
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: turret group " + ActiveGroupId + " is aimed now (" + count + " turret(s))");
        }

        public static void Deactivate(string reason)
        {
            bool was = Active;
            Active = false;
            // RELEASE-STRIP-BEGIN
            StopRecording();
            // RELEASE-STRIP-END
            commands.Clear();
            calibration = null;
            haveDesired = false;
            fireMouse = fireKey = false;
            try
            {
                if (sight && sightController != null)
                {
                    sightController.MouseLook = false;
                    rotationRef(sightController) = Vector2.zero;
                }
            }
            catch (Exception) { }
            sight = false;
            sightController = null;
            sightGun = null;
            DestroyHud();
            ReleasePivot();

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

        // RELEASE-STRIP-BEGIN
        // The plugin is being unloaded (a hot reload): the aim is switched off (camera, pivot, fire input, circle and marker are taken back) and
        // what was made for the crosshair is freed.
        public static void Shutdown()
        {
            Deactivate("the plugin is unloading");
            if (invertMaterial != null) UnityEngine.Object.Destroy(invertMaterial);
            if (ringTexture != null) UnityEngine.Object.Destroy(ringTexture);
            if (dotTexture != null) UnityEngine.Object.Destroy(dotTexture);
            invertMaterial = null;
            ringTexture = dotTexture = null;
        }

        // What an earlier copy of the plugin left in the scene when it was unloaded without this clean-up: its circle and marker (frozen on the
        // screen) and its camera pivot. Called a moment after loading, before this copy has made any.
        public static int RemoveLeftovers()
        {
            int removed = 0;
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (canvas == null || canvas.gameObject.name != "ArmorFragments turret aim") continue;
                UnityEngine.Object.Destroy(canvas.gameObject);
                removed++;
            }
            foreach (Transform transform in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform == null || transform.gameObject.name != "ArmorFragments turret camera pivot") continue;
                UnityEngine.Object.Destroy(transform.gameObject);
                removed++;
            }
            return removed;
        }
        // RELEASE-STRIP-END

        // ---- Changing a key from the panel: the next key pressed is the new one (Escape cancels) --------------------

        private static ConfigEntry<Key> capturing;

        public static void CaptureKey(ConfigEntry<Key> entry)
        {
            capturing = entry;
        }

        public static string KeyText(ConfigEntry<Key> entry)
        {
            return capturing == entry ? "Press the new key... (Esc cancels)" : entry.Value.ToString();
        }

        private static void TickCapture(Keyboard keyboard)
        {
            if (keyboard == null) return;
            foreach (UnityEngine.InputSystem.Controls.KeyControl control in keyboard.allKeys)
            {
                if (!control.wasPressedThisFrame) continue;
                if (control.keyCode != Key.Escape)
                {
                    capturing.Value = control.keyCode;
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: " + capturing.Definition.Key + " is now " + control.keyCode);
                }
                capturing = null;
                return;
            }
        }

        public static void Tick()
        {
            try
            {
                Keyboard keyboard = Keyboard.current;
                if (capturing != null)
                {
                    TickCapture(keyboard);
                    return;
                }
                if (keyboard != null)
                {
                    Key key = ArmorFragmentsPlugin.TurretAimKey.Value;
                    if (key != Key.None && keyboard[key].wasPressedThisFrame) Toggle();
                }
                if (Active) Frame();
                else if (Time.unscaledTime >= nextRepair)
                {
                    nextRepair = Time.unscaledTime + 2f;
                    if (EnsureRefs()) RepairOrbitCamera(CameraManagerScript.Instance);
                }
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
            if (cm.Controller != (sight ? (CameraController)sightController : controller))
            {
                Deactivate("the camera view was changed");
                return;
            }
            if (ArmorFragmentsPlugin.TurretTestSingle.Value != testWasOn)
            {
                testWasOn = ArmorFragmentsPlugin.TurretTestSingle.Value;
                ResetForTestChange();
            }
            Camera cam = cm.MainCamera;
            if (cam == null) return;
            UpdatePivot();

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            bool alt = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
            Key groupKey = ArmorFragmentsPlugin.TurretGroupKey.Value;
            if (keyboard != null && groupKey != Key.None && groupIds.Count > 1 && keyboard[groupKey].wasPressedThisFrame) NextGroup(cm);
            Key sightKey = ArmorFragmentsPlugin.TurretSightKey.Value;
            if (keyboard != null && sightKey != Key.None && keyboard[sightKey].wasPressedThisFrame)
            {
                if (sight) LeaveSight(cm);
                else EnterSight(cm);
                if (!Active) return;
            }

            Gun leadGun = Lead();
            if (leadGun == null)
            {
                Deactivate("the cannons of the turret group are gone");
                return;
            }
            CannonScript pointer = leadGun.Cannon;
            hullBody = leadGun.Hull;

            Key lookKey = ArmorFragmentsPlugin.TurretLookKey.Value;
            bool look = keyboard != null && lookKey != Key.None && keyboard[lookKey].isPressed;
            InteractiveCameraController viewer = sight ? sightController : controller;   // the view C frees
            bool paused = PauseManager.Paused;
            bool free = alt || paused;

            // C: the camera is freed from the turret, which keeps its wanted direction. Let go: the camera comes back to where it was,
            // gradually, and the aim follows it again once it is there.
            if (look && !decoupled)
            {
                decoupled = true;
                returning = false;
                savedRotation = sight ? Vector2.zero : rotationRef(controller);   // through the sight the view comes back to looking along the gun
            }
            else if (!look && decoupled && !returning)
            {
                returning = true;
                returnVelocityX = returnVelocityY = 0f;
            }
            if (returning)
            {
                Vector2 current = rotationRef(viewer);
                float targetY = current.y + Mathf.DeltaAngle(current.y, savedRotation.y);
                float x = Mathf.SmoothDamp(current.x, savedRotation.x, ref returnVelocityX, 0.3f);
                float y = Mathf.SmoothDamp(current.y, targetY, ref returnVelocityY, 0.3f);
                rotationRef(viewer) = new Vector2(x, y);
                if (Mathf.Abs(x - savedRotation.x) < 0.4f && Mathf.Abs(Mathf.DeltaAngle(y, savedRotation.y)) < 0.4f)
                {
                    returning = false;
                    decoupled = false;
                }
            }

            // The mouse turns the camera through the game's own mouse look (it also hides and locks the cursor); Alt gives the cursor back.
            // Through the sight the game's mouse look is off: this code turns the aim with the mouse and looks after the cursor itself.
            if (sight)
            {
                Cursor.lockState = alt ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = alt;
                // Held C: the game's own mouse look turns the view about the sight camera (to look round the inside of the tank) while the turret
                // keeps its aim. Otherwise the view stays on the gun and the mouse turns the aim.
                bool wantLook = look && !returning && !alt;
                if (sightController.MouseLook != wantLook) sightController.MouseLook = wantLook;
            }
            else
            {
                bool wantLook = !alt && !returning;
                if (controller.MouseLook != wantLook) controller.MouseLook = wantLook;
            }

            // Right button: gradual zoom (and a slower mouse while zoomed, so the aim stays fine).
            float baseFov = cm.CurrentCameraFieldOfView;
            bool zoom = mouse != null && mouse.rightButton.isPressed && !free && !returning;
            float restFov = baseFov;
            if (sight)
            {
                // Through the sight the wheel sets the normal zoom (it stays), and the right button zooms in further while it is held.
                if (sightBase <= 0f || sightBase > baseFov) sightBase = baseFov;
                float wheel = mouse != null && !free ? mouse.scroll.ReadValue().y : 0f;
                if (Mathf.Abs(wheel) > 0.01f) sightBase = Mathf.Clamp(sightBase * (wheel > 0f ? 0.85f : 1f / 0.85f), 2f, baseFov);
                restFov = sightBase;
            }
            float zoomTimes = sight ? ArmorFragmentsPlugin.TurretSightZoom.Value : ArmorFragmentsPlugin.TurretZoom.Value;
            float targetFov = zoom ? Mathf.Max(restFov / zoomTimes, 2f) : restFov;
            float fov = Mathf.SmoothDamp(cam.fieldOfView, targetFov, ref fovVelocity, 0.35f, float.PositiveInfinity, Time.unscaledDeltaTime);
            cm.SetCameraFov(fov);
            if (sight)
            {
                fovTargetRef(sightController) = fov;   // the first-person view eases its own zoom toward this: keep it where it is
                sensitivityRef(sightController) = Mathf.Clamp(fov / Mathf.Max(baseFov, 1f), 0.03f, 1f);   // for the free look
            }
            else sensitivityRef(controller) = Mathf.Clamp(fov / Mathf.Max(baseFov, 1f), 0.08f, 1f);

            // Left button: fire (read by the override of the Fire Weapons input).
            // The turret group's cannons are fired here, one by one, by the fire inputs: the game's own weapon selection is not used (see
            // TurretGameFirePatch).
            bool canFire = !free && !returning;
            ButtonControl fireButton = mouse == null ? null
                : ArmorFragmentsPlugin.TurretFireMouseButton.Value == TurretFireMouse.Middle ? mouse.middleButton : mouse.leftButton;
            Key fireKeyCode = ArmorFragmentsPlugin.TurretFireKey.Value;
            fireMouse = canFire && fireButton != null && fireButton.isPressed;
            fireKey = canFire && keyboard != null && fireKeyCode != Key.None && keyboard[fireKeyCode].isPressed;
            if (fireMouse || fireKey) FireGuns();

            // The wanted direction: from the cannon's muzzle to what the camera sees at the middle of the screen (the camera is behind and above the
            // gun, so the gun must aim from where it is, not parallel to the camera).
            Vector3 muzzle = pointer.TipPosition;
            if (sight)
            {
                // Through the sight the mouse turns the wanted direction itself (the sight camera sits on the gun, so it can't say where to aim):
                // right turns it right and up turns it up, about the sight camera's own axes, less when zoomed in.
                if (mouse != null && !free && !decoupled)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    float degrees = ArmorFragmentsPlugin.TurretSightSensitivity.Value * Mathf.Clamp(fov / Mathf.Max(baseFov, 1f), 0.03f, 1f);
                    Transform view = cam.transform;
                    desired = (Quaternion.AngleAxis(delta.x * degrees, view.up) * Quaternion.AngleAxis(-delta.y * degrees, view.right) * desired).normalized;
                }
                haveDesired = true;
                aimDistance = Mathf.Max(5f, (AimPoint(muzzle, desired) - muzzle).magnitude);
            }
            else if (!decoupled)
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

                // For the log if the aim swings: how far the aim point jumped about in the last couple of seconds.
                if (Time.unscaledTime >= aimWindowEnd)
                {
                    aimWindowEnd = Time.unscaledTime + 2f;
                    aimMin = aimMax = aimDistance;
                }
                aimMin = Mathf.Min(aimMin, aimDistance);
                aimMax = Mathf.Max(aimMax, aimDistance);
            }

            UpdateHud(cam, muzzle, look || returning);

            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 0.25f;
                string state = !calibrated ? "Calibrating the turret..."
                    : decoupled ? "Free look (the turret holds its aim)"
                    : atLimit ? "At the turret's limit"
                    : errorDegrees < 0.5f ? "On target"
                    : "Turning (" + errorDegrees.ToString("F1") + " deg off)";
                Status = notice != null && Time.unscaledTime < noticeUntil ? notice
                    : (groupIds.Count > 1 ? "Group " + ActiveGroupId + ": " : "") + (sight ? "Sight: " : "") + state;
            }

            // RELEASE-STRIP-BEGIN
            RecordFrame(cam);
            // RELEASE-STRIP-END
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

        // While the aim is on, the game's keys that switch the camera view (C by default) do nothing: C is the free look key, and a view switch
        // would end the aim. (CameraManagerScript.Update reads them with GetButtonDownIfEnabled.)
        internal static void BlockViewKeys(object input, ref bool result)
        {
            if (!result || !Active) return;
            GameInputs inputs = GameInputs.Instance;
            if (inputs == null) return;
            // The right button toggles the game's "mouse as joystick" (a saved setting!): it is the zoom here.
            if (ReferenceEquals(input, inputs.NextView) || ReferenceEquals(input, inputs.PreviousView) || ReferenceEquals(input, inputs.ToggleMouseJoystick))
                result = false;
        }

        // Smoothstep between two edges (Mathf.SmoothStep is not that: it eases between two values by a 0..1 amount).
        private static float Edge(float from, float to, float x)
        {
            float t = Mathf.Clamp01((x - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        // Fires each cannon of the active group (the turrets, and the cannons fixed to them) whose input is held and whose own delay is over: the
        // Firing Delay (stretched by the crew's load) and the game's checks (ammo, barrel clear of the last shell; a rack-fed cannon needs a round
        // and a loader in its own patch of CannonScript.Fire). On top of that: only turrets that point at the cursor fire (the others hold),
        // none fires into its own vehicle, and a salvo goes one cannon at a time (a ripple) with the order rotating, so the same turrets do not
        // always go first.
        private sealed class Shooter
        {
            public CannonScript Cannon;
            public bool ByKey;
            public Gun Owner;
            public FixedCannon Fixed;
        }

        private static readonly List<Shooter> shooters = new List<Shooter>();
        private static int fireStart;
        private static float gateMouse, gateKey;   // no cannon of that input fires before this time (the ripple)
        private static readonly RaycastHit[] lineHits = new RaycastHit[16];
        private const float LineOfFireMeters = 20f;
        private const float LineOfFireIgnoreMeters = 0.3f;   // what is this close to the muzzle is the barrel's own fittings

        private static void FireGuns()
        {
            float now = Time.time;
            shooters.Clear();
            foreach (Gun gun in guns)
            {
                if (!InActiveGroup(gun)) continue;
                shooters.Add(new Shooter { Cannon = gun.Cannon, ByKey = gun.FiresByKey, Owner = gun });
                foreach (FixedCannon fixedCannon in gun.FixedCannons)
                    shooters.Add(new Shooter { Cannon = fixedCannon.Cannon, ByKey = fixedCannon.ByKey, Owner = gun, Fixed = fixedCannon });
            }
            int count = shooters.Count;
            if (count == 0) return;
            if (fireStart >= count) fireStart = 0;
            float stagger = ArmorFragmentsPlugin.TurretFireStagger.Value;
            for (int k = 0; k < count; k++)
            {
                int index = (fireStart + k) % count;
                Shooter shooter = shooters[index];
                if (!(shooter.ByKey ? fireKey : fireMouse)) continue;
                if (stagger > 0f && now < (shooter.ByKey ? gateKey : gateMouse)) continue;
                bool fired = shooter.Fixed != null ? TryFire(shooter.Cannon, shooter.ByKey, shooter.Owner, now, ref shooter.Fixed.NextFire)
                                                   : TryFire(shooter.Cannon, shooter.ByKey, shooter.Owner, now, ref shooter.Owner.NextFire);
                if (!fired) continue;
                fireStart = (index + 1) % count;
                if (stagger > 0f)
                {
                    if (shooter.ByKey) gateKey = now + stagger; else gateMouse = now + stagger;
                }
            }
        }

        // A turret may fire when its gun is within the tolerance of the cursor (the cannons fixed to it follow its turret's state).
        private static bool OnTarget(Gun owner)
        {
            if (!ArmorFragmentsPlugin.TurretFireOnTarget.Value) return true;
            return calibrated && Time.fixedTime - owner.AimStamp < 0.25f && owner.AimError <= ArmorFragmentsPlugin.TurretFireTolerance.Value;
        }

        // Nothing of the cannon's own vehicle within a few meters in front of the muzzle (a turret's roof, another turret).
        private static bool LineClear(CannonScript cannon)
        {
            if (!ArmorFragmentsPlugin.TurretLineOfFire.Value || craft == null) return true;
            Vector3 direction = cannon.transform.forward;
            int hits = Physics.RaycastNonAlloc(cannon.TipPosition, direction, lineHits, LineOfFireMeters, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                if (lineHits[i].distance < LineOfFireIgnoreMeters) continue;
                PartScript part = lineHits[i].collider != null ? lineHits[i].collider.GetComponentInParent<PartScript>() : null;
                if (part != null && part != cannon.PartScript && part.Aircraft == craft) return false;
            }
            return true;
        }

        private static bool TryFire(CannonScript cannon, bool byKey, Gun owner, float now, ref float nextFire)
        {
            if (cannon == null || cannon.PartScript == null || !(byKey ? fireKey : fireMouse) || now < nextFire) return false;
            IWeapon weapon = cannon;
            if (weapon.IsDestroyed || weapon.CurrentAmmo <= 0 || !cannon.CanFire || !AmmoRacks.CanFire(cannon)) return false;
            if (!OnTarget(owner) || !LineClear(cannon)) return false;
            try
            {
                weapon.Fire(null);
                nextFire = now + Mathf.Max(cannon.FiringDelay * Crew.ReloadFactor(cannon), 0.02f);
                return true;
            }
            catch (Exception e)
            {
                nextFire = now + 1f;
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: a cannon could not fire: " + e.Message);
                return false;
            }
        }

        // While the aim is on, the game's weapon systems fire no cannon of this vehicle (the selection of the game is ignored: only the cannons
        // of the turret group fire, by their own inputs).
        internal static bool BlocksGameCannons(CannonWeaponSystem system)
        {
            if (!Active || craft == null || system == null) return false;
            foreach (WeaponPart weapon in system.Weapons)
                return weapon.Part != null && weapon.Part.Aircraft == craft;
            return false;
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

        // What angle a rotator had at a time (the last second is kept): the angle it was asked for a moment ago is the part of the barrel's
        // travel that has not happened yet.
        private static void RememberAngles(float now)
        {
            foreach (Turret turret in turrets)
            {
                if (turret.Script == null) continue;
                turret.HistoryTime.Add(now);
                turret.HistoryAngle.Add(angleRef(turret.Script));
                int old = 0;
                while (old < turret.HistoryTime.Count - 2 && now - turret.HistoryTime[old] > 1f) old++;
                if (old > 0)
                {
                    turret.HistoryTime.RemoveRange(0, old);
                    turret.HistoryAngle.RemoveRange(0, old);
                }
            }
        }

        private static float AngleAt(Turret turret, float time, float fallback)
        {
            List<float> times = turret.HistoryTime, angles = turret.HistoryAngle;
            int n = times.Count;
            if (n == 0) return fallback;
            if (time <= times[0]) return angles[0];
            for (int i = n - 1; i > 0; i--)
            {
                if (times[i - 1] > time) continue;
                float span = times[i] - times[i - 1];
                float f = span > 1e-6f ? Mathf.Clamp01((time - times[i - 1]) / span) : 1f;
                return angles[i - 1] + (angles[i] - angles[i - 1]) * f;
            }
            return angles[0];
        }

        // Where the cursor pointed a moment ago (the last second), for the turrets that start late.
        private static readonly List<float> desiredTime = new List<float>();
        private static readonly List<Vector3> desiredDirection = new List<Vector3>();

        private static bool aimIsMoving;   // the cursor moved more than a degree in the last 0.3 s: a gap that changes side is the cursor, not a swing

        private static void RememberDesired(float now)
        {
            desiredTime.Add(now);
            desiredDirection.Add(desired);
            int old = 0;
            while (old < desiredTime.Count - 2 && now - desiredTime[old] > 1f) old++;
            if (old > 0)
            {
                desiredTime.RemoveRange(0, old);
                desiredDirection.RemoveRange(0, old);
            }
        }

        private static Vector3 DesiredAt(float time)
        {
            int n = desiredTime.Count;
            if (n == 0) return desired;
            if (time <= desiredTime[0]) return desiredDirection[0];
            for (int i = n - 1; i > 0; i--)
            {
                if (desiredTime[i - 1] > time) continue;
                float span = desiredTime[i] - desiredTime[i - 1];
                float f = span > 1e-6f ? Mathf.Clamp01((time - desiredTime[i - 1]) / span) : 1f;
                return Vector3.Slerp(desiredDirection[i - 1], desiredDirection[i], f).normalized;
            }
            return desiredDirection[0];
        }

        private static void Step(float dt)
        {
            RememberAngles(Time.fixedTime);
            Gun lead = Lead();
            if (lead == null) return;

            if (!calibrated)
            {
                Calibrate(dt);
                return;
            }
            if (!haveDesired)
            {
                HoldAll();
                return;
            }

            // Every turret of the active group is turned toward the same point: the lead along the wanted direction, the others from where they are
            // to the point the lead's line reaches at the aim distance. The rotators of the other groups keep their place.
            Vector3 leadTip = lead.Cannon.TipPosition;
            Vector3 aimPoint = leadTip + desired * aimDistance;
            HashSet<Turret> driven = new HashSet<Turret>();
            bool limit = false;
            float now = Time.fixedTime;
            // RELEASE-STRIP-BEGIN
            if (recorder != null)
            {
                Camera recorded = Camera.main;
                recordCamera = recorded != null ? recorded.transform.position : Vector3.zero;
            }
            // RELEASE-STRIP-END
            RememberDesired(now);
            aimIsMoving = Vector3.Angle(desired, DesiredAt(now - 0.3f)) > 1f;
            float stagger = ArmorFragmentsPlugin.TurretMoveStagger.Value;
            int rank = 0;
            foreach (Gun gun in guns)
            {
                if (!InActiveGroup(gun) || !Live(gun)) continue;
                // The turrets of a group are on one vehicle, and the bodies of a vehicle are joined (in a recording of 14 turrets on a ship, 13
                // sat on one body and the lead on another joined to it: the lone one kept the full gain and was the only one that swung).
                int share = 0;
                foreach (Gun other in guns)
                    if (InActiveGroup(other) && Live(other)) share++;
                gun.Share = Mathf.Max(1, share);
            }
            for (int g = 0; g < guns.Count; g++)
            {
                Gun gun = guns[g];
                if (!InActiveGroup(gun) || !Live(gun)) continue;

                // Where the gun should point, for judging whether it may fire: the cursor itself.
                Vector3 want = desired;
                if (gun != lead)
                {
                    Vector3 toPoint = aimPoint - gun.Cannon.TipPosition;
                    if (toPoint.sqrMagnitude > 1f) want = toPoint.normalized;
                }
                gun.Want = want;
                gun.AimError = Vector3.Angle(gun.Cannon.transform.forward, want);
                gun.AimStamp = now;

                // The turrets of a group start one after another: each follows the cursor as it was a few hundredths of a second earlier than the
                // one before it (the lead follows it live), so fourteen of them do not push the hull at the same instant.
                int mine = gun == lead ? 0 : ++rank;
                if (gun.Drivers.Count == 0) continue;
                Vector3 follow = want;
                if (stagger > 0f && mine > 0)
                {
                    Vector3 toPoint = leadTip + DesiredAt(now - mine * stagger) * aimDistance - gun.Cannon.TipPosition;
                    if (toPoint.sqrMagnitude > 1f) follow = toPoint.normalized;
                }
                StepGun(g, gun, gun == lead, follow, dt, driven, ref limit);
            }

            foreach (Turret turret in turrets)
                if (!driven.Contains(turret) && turret.Script != null) commands[turret.Script] = angleRef(turret.Script);
            atLimit = limit;
        }

        private static void StepGun(int index, Gun gun, bool isLead, Vector3 want, float dt, HashSet<Turret> driven, ref bool limit)
        {
            Vector3 barrel = gun.Cannon.transform.forward;

            // The real error (for the marker) and the one the control works from: the barrel is still moving by its own momentum and the joint's
            // lag, so the control aims from where the barrel will be a moment from now (its own angular velocity times the damping time). Without
            // this the angle keeps being asked for what the barrel is already on its way to, and it overshoots and swings.
            Vector3 trueRotation = RotationVector(barrel, want);
            Vector3 rotation = trueRotation;
            float lead = ArmorFragmentsPlugin.TurretDamping.Value;
            if (gun.BarrelKnown && dt > 1e-4f)
            {
                Vector3 turning = RotationVector(gun.PreviousBarrel, barrel) / dt;   // rad/s, world
                gun.Spin = Smooth(gun.Spin, turning, false, dt);
                gun.FastSpin = Smooth(gun.FastSpin, turning, false, dt, BrakeFilterSeconds);
            }
            else gun.FastSpin = Vector3.zero;
            if (gun.Hull != null)
                gun.HullSpin = Smooth(gun.HullSpin, gun.Hull.angularVelocity, !gun.BarrelKnown, dt, HullFilterSeconds);

            List<Turret> use = new List<Turret>();
            foreach (Turret turret in gun.Drivers)
                if (turret.Usable && Moves(turret)) use.Add(turret);

            Vector3[] axes = new Vector3[use.Count];
            for (int i = 0; i < use.Count; i++) axes[i] = AxisOf(use[i].Script);

            // The barrel follows the angle late (it was measured when the aim started: a heavy turret takes a tenth of a second or more). The
            // travel the barrel still has to make is what the rotators were asked for in that time, so the control works from where the barrel
            // WILL be once it has arrived. Without this it keeps asking for what is already on its way, overshoots, and the turret swings.
            float pendingDegrees = 0f;
            float now = Time.fixedTime;
            float lagUsed = Mathf.Max(gun.Lag, MinimumAssumedLag) + gun.LagBoost;
            if (gun.Lag > 0.005f && use.Count > 0)
            {
                Vector3 settled = barrel;
                for (int i = 0; i < use.Count; i++)
                {
                    float angleNow = angleRef(use[i].Script);
                    float pending = angleNow - AngleAt(use[i], now - lagUsed, angleNow);
                    float sign = use[i].Signs[index];
                    float scale = use[i].Scales[index] > 0.05f ? use[i].Scales[index] : 1f;
                    float travel = sign * scale * pending;
                    settled = Quaternion.AngleAxis(travel, axes[i]) * settled;
                    pendingDegrees += Mathf.Abs(travel);
                }
                rotation = RotationVector(settled, want);
            }
            else if (lead > 0f && gun.BarrelKnown && dt > 1e-4f)
            {
                // A turret whose lag could not be measured: the barrel's own velocity times a damping time (the old way).
                Vector3 spin = gun.Spin;
                float spinSpeed = spin.magnitude;
                if (spinSpeed > 1e-5f)
                {
                    Vector3 predicted = Quaternion.AngleAxis(spinSpeed * lead * Mathf.Rad2Deg, spin / spinSpeed) * barrel;
                    rotation = RotationVector(predicted, want);
                }
            }
            gun.PreviousBarrel = barrel;
            gun.BarrelKnown = true;
            if (isLead) errorDegrees = trueRotation.magnitude * Mathf.Rad2Deg;
            if (use.Count == 0) return;

            // The rotation split between the rotators by their axes: the small least-squares problem  sum(theta_i * axis_i) = rotation.
            double[] theta = rotation.magnitude * Mathf.Rad2Deg < 0.02f ? new double[use.Count] : Solve(axes, rotation);

            // Stabilizer: while the hull turns, pitches or rolls, the rotators must turn the other way at the same rate, or the barrel would
            // always trail behind it (the error alone only catches up). The same split, for the hull's angular velocity.
            double[] hull = new double[use.Count];
            if (gun.Hull != null) hull = Solve(axes, -gun.HullSpin);

            // The braking (derivative) term: the gun's own speed in the world, split between the rotators the same way, taken off the gap.
            // Hull and turrets pushed each other into a growing swing (a recording of a 14-turret ship: 5 deg/s to 95 deg/s of hull spin in three
            // seconds); in a model fitted to it, braking by the gun's speed kept it stable over every coupling and delay tried and took the
            // overshoot of a 20 degree move from 18 degrees to none, where lowering the gain did not help.
            float brake = ArmorFragmentsPlugin.TurretDerivative.Value;
            double[] speed = brake > 0f && gun.BarrelKnown ? Solve(axes, gun.FastSpin) : new double[use.Count];

            // The gap's sign, for the swing detector: the rotator with the biggest gap (the signed value of the one that turns it most).
            int biggest = 0;
            for (int i = 1; i < use.Count; i++) if (Math.Abs(theta[i]) > Math.Abs(theta[biggest])) biggest = i;
            WatchForSwinging(gun, (float)theta[biggest] * Mathf.Rad2Deg, trueRotation.magnitude * Mathf.Rad2Deg, isLead);

            // Turrets on one hull body push it together: the more of them, the gentler each is (the hull rocked 5 degrees at 2 Hz in a recording of
            // 14 turrets, even with the ship on land, and a model fitted to it says the push grows with the number of turrets that turn at once).
            float share = ArmorFragmentsPlugin.TurretShareHull.Value ? Mathf.Pow(Mathf.Max(1, gun.Share), -ArmorFragmentsPlugin.TurretShareStrength.Value) : 1f;
            float baseGain = 1f - Mathf.Exp(-dt / Mathf.Max(ArmorFragmentsPlugin.TurretResponse.Value, 0.02f));
            float gain = Mathf.Max(baseGain * share * gun.GainScale, Mathf.Min(baseGain, MinimumGain));
            for (int i = 0; i < use.Count; i++)
            {
                Turret turret = use[i];
                float current = angleRef(turret.Script);
                // A very small error asks for a very small step (gain x error), which the joint may not answer at all (friction, the game's own
                // rounding): the turret then stops short of the cursor. So a step is never smaller than a hundredth of a degree, or the error
                // itself when that is smaller (it never overshoots). It was a twentieth, which made every turret creep at 5 degrees a second
                // all the time (fourteen of them shook the ship).
                float step = ((float)theta[i] - brake * (float)speed[i]) * gain;
                float smallest = Mathf.Min(Mathf.Abs((float)theta[i]), 0.01f * Mathf.Deg2Rad);
                if (brake <= 0f && Mathf.Abs(step) < smallest) step = Mathf.Sign((float)theta[i]) * smallest;

                // Small errors: with the gain divided among many turrets a gap of a fraction of a degree asks for a step the joint does not answer
                // (a hundredth of a degree), and the turret sat still short of the cursor. So while the gap is bigger than a twentieth of a degree
                // and the step goes the right way, a step is at least 0.02 degrees (2 degrees a second), or the gap itself when that is smaller.
                // The gap is the settled one (what is still on its way is already taken off), so this ends where the gap does and does not creep.
                float gap = (float)theta[i];
                if (Mathf.Abs(gap) > SmallestGapDegrees * Mathf.Deg2Rad && Mathf.Sign(step) == Mathf.Sign(gap) && Mathf.Abs(step) < MicroStepDegrees * Mathf.Deg2Rad)
                    step = Mathf.Sign(gap) * Mathf.Min(Mathf.Abs(gap), MicroStepDegrees * Mathf.Deg2Rad);
                float target = current + turret.Signs[index] * (step + (float)hull[i] * dt * share) * Mathf.Rad2Deg;
                if (!turret.Wraps)
                {
                    float clamped = Mathf.Clamp(target, -turret.Range, turret.Range);
                    if (Mathf.Abs(clamped - target) > 0.001f && Mathf.Abs(current - clamped) < 0.1f && Mathf.Abs((float)theta[i]) * Mathf.Rad2Deg > 0.3f)
                        limit = true;
                    target = clamped;
                }
                commands[turret.Script] = target;
                driven.Add(turret);
                // RELEASE-STRIP-BEGIN
                if (recorder != null) RecordRow(index, gun, trueRotation, rotation, turret, current, target, (float)theta[i], (float)hull[i], step, gain, lagUsed, pendingDegrees,
                                                    gun.Hull != null ? (float)Solve(axes, -gun.Hull.angularVelocity)[i] * Mathf.Rad2Deg : 0f);
                // RELEASE-STRIP-END
            }
        }

        // RELEASE-STRIP-BEGIN
        // ---- Recording for diagnosis (the panel's "Record turret data" button) ---------------------------------------------------------
        // Three files in the BepInEx folder, written when the recording stops (writing while playing made the game stutter; it stops by itself
        // after 120 s):
        //   turret-aim-log.csv           one row per rotator per physics step (100 a second), with the cursor, the barrel, the wanted direction
        //                                of the gun and the camera position at that step
        //   turret-aim-frames.csv        one row per turret per rendered frame (the pose the player sees: interpolated, ~30 a second), with the
        //                                camera's position and direction, to tell a physics swing from something that is only drawn
        //   turret-aim-log-settings.txt  the settings the recording was made with, and each turret's measured lag, added lag, gain and share at the end

        private static System.Text.StringBuilder recorder;   // kept in memory and written when it stops
        private static System.Text.StringBuilder frameRecorder;
        private static float recordStarted;
        private static int recordRows, frameRows;
        private static string recordPath, framePath, settingsPath, recordSettings;
        private static Vector3 recordCamera;   // the camera's position at the current physics step

        public static string RecordStatus
        {
            get { return recorder != null ? "Recording: " + recordRows + " rows, " + frameRows + " frames (" + (Time.unscaledTime - recordStarted).ToString("F0") + " s) -> " + recordPath : "Not recording"; }
        }

        private static string F(float value)
        {
            return value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string V(Vector3 value)
        {
            return F(value.x) + "," + F(value.y) + "," + F(value.z);
        }

        // The settings that decide how the turrets behave, one per line.
        private static string SettingsText()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.AppendLine("Armor Fragments " + ArmorFragmentsPlugin.Version + ", recorded " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("Response seconds (ResponseSeconds): " + ArmorFragmentsPlugin.TurretResponse.Value);
            text.AppendLine("Braking seconds (BrakingSeconds): " + ArmorFragmentsPlugin.TurretDerivative.Value);
            text.AppendLine("Damping seconds (DampingSeconds): " + ArmorFragmentsPlugin.TurretDamping.Value);
            text.AppendLine("Slow turrets sharing a hull (SlowSharedHull): " + ArmorFragmentsPlugin.TurretShareHull.Value);
            text.AppendLine("Shared-hull strength (SharedHullStrength): " + ArmorFragmentsPlugin.TurretShareStrength.Value);
            text.AppendLine("Turret start stagger (MoveStaggerSeconds): " + ArmorFragmentsPlugin.TurretMoveStagger.Value);
            text.AppendLine("Fire only on target (FireOnlyOnTarget): " + ArmorFragmentsPlugin.TurretFireOnTarget.Value + ", tolerance " + ArmorFragmentsPlugin.TurretFireTolerance.Value + " deg");
            text.AppendLine("Salvo ripple (FireStaggerSeconds): " + ArmorFragmentsPlugin.TurretFireStagger.Value);
            text.AppendLine("Check line of fire (CheckLineOfFire): " + ArmorFragmentsPlugin.TurretLineOfFire.Value);
            text.AppendLine("Test one turret: " + ArmorFragmentsPlugin.TurretTestSingle.Value + (ArmorFragmentsPlugin.TurretTestSingle.Value ? " (turret " + (testIndex + 1) + " of the group)" : ""));
            text.AppendLine("Active group: " + ActiveGroupId + ", turrets in the group: " + GroupCount() + ", turrets in all: " + guns.Count + ", rotators: " + turrets.Count);
            text.AppendLine("Physics step: " + Time.fixedDeltaTime + " s");
            return text.ToString();
        }

        private static string TurretStateText()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int g = 0; g < guns.Count; g++)
            {
                Gun gun = guns[g];
                text.AppendLine("turret " + (g + 1) + " (group " + gun.GroupId + "): measured lag " + (gun.Lag * 1000f).ToString("F0") + " ms, added lag " + (gun.LagBoost * 1000f).ToString("F0") +
                                " ms, gain scale " + gun.GainScale.ToString("F2") + ", share " + gun.Share + ", rotators " + gun.Drivers.Count);
            }
            return text.ToString();
        }

        public static void ToggleRecording()
        {
            if (recorder != null) { StopRecording(); return; }
            try
            {
                string folder = BepInEx.Paths.BepInExRootPath;
                recordPath = System.IO.Path.Combine(folder, "turret-aim-log.csv");
                framePath = System.IO.Path.Combine(folder, "turret-aim-frames.csv");
                settingsPath = System.IO.Path.Combine(folder, "turret-aim-log-settings.txt");
                recorder = new System.Text.StringBuilder(4 * 1024 * 1024);
                frameRecorder = new System.Text.StringBuilder(1024 * 1024);
                recorder.AppendLine("time_s,gun,error_deg,aimed_from_deg,rotator,angle_deg,target_deg,theta_deg,hull_deg_per_s,step_deg,gain,gain_scale,at_limit,lag_s,pending_deg,hull_raw_deg_per_s," +
                                    "cursor_x,cursor_y,cursor_z,want_x,want_y,want_z,barrel_x,barrel_y,barrel_z,cam_x,cam_y,cam_z,aim_moving,share");
                frameRecorder.AppendLine("time_s,frame_dt,gun,error_deg,barrel_x,barrel_y,barrel_z,want_x,want_y,want_z,cursor_x,cursor_y,cursor_z,cam_x,cam_y,cam_z,cam_fx,cam_fy,cam_fz,fov");
                recordStarted = Time.unscaledTime;
                recordRows = 0;
                frameRows = 0;
                recordSettings = SettingsText();
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: recording to " + recordPath + " and " + framePath + " (it stops by itself after 120 s, or press the button again)");
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: settings of this recording: " + recordSettings.Replace("\r", "").Replace("\n", " | "));
            }
            catch (Exception e)
            {
                recorder = null;
                frameRecorder = null;
                ArmorFragmentsPlugin.Log.LogError("Turret aim: can't record: " + e.Message);
            }
        }

        public static void StopRecording()
        {
            if (recorder == null) return;
            System.Text.StringBuilder text = recorder, frames = frameRecorder;
            recorder = null;
            frameRecorder = null;
            try { System.IO.File.WriteAllText(recordPath, text.ToString()); } catch (Exception e) { ArmorFragmentsPlugin.Log.LogError("Turret aim: can't write the recording: " + e.Message); }
            try { if (frames != null) System.IO.File.WriteAllText(framePath, frames.ToString()); } catch (Exception e) { ArmorFragmentsPlugin.Log.LogError("Turret aim: can't write the frames: " + e.Message); }
            try { System.IO.File.WriteAllText(settingsPath, recordSettings + "\r\nAt the end of the recording:\r\n" + TurretStateText()); } catch (Exception e) { ArmorFragmentsPlugin.Log.LogError("Turret aim: can't write the settings: " + e.Message); }
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: recording stopped, " + recordRows + " rows and " + frameRows + " frames in " + recordPath);
        }

        private static void RecordRow(int gunIndex, Gun gun, Vector3 trueRotation, Vector3 aimedFrom, Turret turret, float angle, float target, float theta, float hull, float step, float gain,
                                      float lag, float pending, float hullRaw)
        {
            try
            {
                float now = Time.unscaledTime;
                recorder.AppendLine(F(now - recordStarted) + "," + gunIndex + "," + F(trueRotation.magnitude * Mathf.Rad2Deg) + "," +
                                   F(aimedFrom.magnitude * Mathf.Rad2Deg) + "," + turret.Name.Replace(',', ' ') + "," + F(angle) + "," +
                                   F(target) + "," + F(theta * Mathf.Rad2Deg) + "," + F(hull * Mathf.Rad2Deg) + "," +
                                   F(step * Mathf.Rad2Deg) + "," + gain.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                   gun.GainScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + (atLimit ? 1 : 0) + "," +
                                   lag.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + F(pending) + "," + F(hullRaw) + "," +
                                   V(desired) + "," + V(gun.Want) + "," + V(gun.Cannon.transform.forward) + "," + V(recordCamera) + "," + (aimIsMoving ? 1 : 0) + "," + gun.Share);
                recordRows++;
                if (now - recordStarted > 120f) StopRecording();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret aim: recording failed: " + e.Message);
                StopRecording();
            }
        }

        // One row per turret of the active group for the frame being drawn: the pose the player sees (the rigidbodies are interpolated between
        // physics steps), and where the camera was and looked.
        private static void RecordFrame(Camera cam)
        {
            if (frameRecorder == null) return;
            try
            {
                float now = Time.unscaledTime;
                Vector3 camPosition = cam != null ? cam.transform.position : Vector3.zero;
                Vector3 camForward = cam != null ? cam.transform.forward : Vector3.zero;
                for (int g = 0; g < guns.Count; g++)
                {
                    Gun gun = guns[g];
                    if (!InActiveGroup(gun) || !Live(gun)) continue;
                    Vector3 barrel = gun.Cannon.transform.forward;
                    frameRecorder.AppendLine(F(now - recordStarted) + "," + F(Time.unscaledDeltaTime) + "," + g + "," + F(Vector3.Angle(barrel, gun.Want)) + "," + V(barrel) + "," + V(gun.Want) + "," +
                                             V(desired) + "," + V(camPosition) + "," + V(camForward) + "," + F(cam != null ? cam.fieldOfView : 0f));
                    frameRows++;
                }
                if (now - recordStarted > 120f) StopRecording();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret aim: frame recording failed: " + e.Message);
                StopRecording();
            }
        }
        // RELEASE-STRIP-END

        // ---- Swinging: if the aim swings back and forth around the target, the gain is lowered by itself ----------------

        private static float aimMin, aimMax, aimWindowEnd;

        private static void ResetSwing(Gun gun)
        {
            gun.PreviousTheta = 0f;
            gun.GainScale = 1f;
            gun.LagBoost = 0f;
            gun.Flips.Clear();
        }

        // The gap closing (the rotator with the biggest gap) changing side over and over (4 times in a second, each time more than a third of a
        // degree: two swings of 2 Hz) is swinging. Each turret has its own detector: a ship's turrets are not all alike.
        //
        // It only counts when the turret is close to the cursor and the cursor is still: a gap that changes side because the cursor was swept
        // across the sky (or flicked to the other side, where the turret is stopped at its limit and the gap jumps by 180 degrees) is not a
        // swing. Counting those cut every turret's gain to a fifth and added 200 ms to every assumed lag in a recording where the player only
        // moved the mouse, which made the turrets slow and gave them a slow wobble of their own.
        //
        // The measured lag comes from a slow 3 degree move; at the speed a turret swings the barrel follows later (a recording of a turret that
        // swung: the barrel trailed the angle by about 180 ms, the measure said 70 ms), and a predictor that thinks the barrel is quicker than
        // it is makes the swing. In a model, raising the assumed lag to the real one ends the swing, while lowering the gain does not (and too
        // much lag makes a small ripple). So a swing first adds 30 ms to the lag the turret assumes (up to 120 ms more); only when that is used up
        // is the gain cut by 30% (down to 30%). When it has been calm for a few seconds the gain creeps back up (the added lag stays).
        private static void WatchForSwinging(Gun gun, float theta, float errorDegrees, bool isLead)
        {
            float now = Time.unscaledTime;
            bool counts = !aimIsMoving && errorDegrees < 8f && Mathf.Abs(theta) < 8f;
            if (!counts)
            {
                gun.Flips.Clear();
                gun.PreviousTheta = 0f;
            }
            else if (Mathf.Abs(theta) > 0.3f)
            {
                if (gun.PreviousTheta != 0f && Mathf.Sign(theta) != Mathf.Sign(gun.PreviousTheta)) gun.Flips.Add(now);
                gun.PreviousTheta = theta;
            }

            gun.Flips.RemoveAll(t => now - t > 1f);
            if (gun.Flips.Count >= 4)
            {
                gun.Flips.Clear();
                gun.LastSwing = now;
                string what;
                if (gun.Lag > 0.005f && gun.LagBoost < 0.06f)
                {
                    gun.LagBoost = Mathf.Min(0.06f, gun.LagBoost + 0.03f);
                    what = "it now assumes the barrel is " + ((Mathf.Max(gun.Lag, MinimumAssumedLag) + gun.LagBoost) * 1000f).ToString("F0") + " ms late";
                }
                else
                {
                    float next = Mathf.Max(0.3f, gun.GainScale * 0.7f);
                    if (next >= gun.GainScale) return;
                    gun.GainScale = next;
                    what = "its gain is now " + (gun.GainScale * 100f).ToString("F0") + "%";
                }
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: turret " + (guns.IndexOf(gun) + 1) + " was swinging (error " + errorDegrees.ToString("F2") + " deg, measured lag " +
                    (gun.Lag * 1000f).ToString("F0") + " ms, " + gun.Share + " turret(s) share the hull" +
                    (isLead ? ", aim point " + aimDistance.ToString("F0") + " m away, between " + aimMin.ToString("F0") + " and " + aimMax.ToString("F0") +
                              " m in the last 2 s, camera pivot height " + pivotY.ToString("F2") : "") + "), " + what);
            }
            else if (gun.GainScale < 1f && now - gun.LastSwing > 3f && now - gun.LastRecover > 1.5f)
            {
                gun.LastRecover = now;
                gun.GainScale = Mathf.Min(1f, gun.GainScale + 0.1f);
            }
        }

        // Which way does each rotator turn each gun's barrel, and how late does the barrel follow it? Move the rotator a few degrees, watch
        // the barrels. A rotator that doesn't move any gun (not between the hull and a barrel, broken) is left out; one that moves several (a
        // turret ring that carries two guns) goes to the first gun it moves.
        //
        // A ship with fourteen turrets would take half a minute one rotator at a time. The way the bodies are joined says which guns each
        // rotator moves (see Predict), so rotators that move different guns are turned together, in a batch. What is measured is checked against
        // what was expected: if a gun that should not have moved did, or one that should have did not, the rotators of that batch are turned
        // alone, the slow way, so a wrong guess costs time and never a wrong answer.
        private const float JogDegrees = 3f;          // how far a rotator is turned to see what it moves
        private const float SettleSeconds = 0.3f;     // how long the barrels get to arrive after that
        private static bool graphTried;
        private static List<BodyEdge> edges;
        private static Rigidbody calBase;   // the vehicle's heaviest body: while calibrating, the barrels are watched from it (a ship shaking at 2 Hz is not a turret turning)

        private static Vector3 LocalDir(Vector3 world)
        {
            return calBase != null ? Quaternion.Inverse(calBase.rotation) * world : world;
        }

        private static Vector3 BarrelLocal(int gun)
        {
            return guns[gun].Cannon != null ? LocalDir(guns[gun].Cannon.transform.forward) : Vector3.forward;
        }

        private sealed class BodyEdge
        {
            public Rigidbody A, B;
            public Turret Turret;   // the marked rotator this joint is, if it is one
        }

        // Every joint between two bodies of the scene (other vehicles are other islands of the same graph and never reached).
        private static void BuildGraph()
        {
            edges = null;
            try
            {
                List<BodyEdge> found = new List<BodyEdge>();
                foreach (Joint joint in UnityEngine.Object.FindObjectsByType<Joint>(FindObjectsSortMode.None))
                {
                    if (joint == null) continue;
                    Rigidbody own = joint.GetComponent<Rigidbody>(), other = joint.connectedBody;
                    if (own == null || other == null || own == other) continue;
                    BodyEdge edge = new BodyEdge { A = own, B = other };
                    foreach (Turret turret in turrets)
                        if (turret.Script != null && jointRef(turret.Script) == joint) { edge.Turret = turret; break; }
                    found.Add(edge);
                }
                calBase = null;
                foreach (Gun gun in guns)
                {
                    Rigidbody body = gun.Cannon != null ? BodyOf(gun.Cannon.PartScript) : null;
                    if (body == null) continue;
                    foreach (Rigidbody candidate in Reach(found, body, null))
                        if (candidate != null && (calBase == null || candidate.mass > calBase.mass)) calBase = candidate;
                    break;
                }
                foreach (Turret turret in turrets) turret.Expect = Predict(found, turret);
                edges = found;
                int known = 0;
                foreach (Turret turret in turrets) if (turret.Expect != null) known++;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: " + found.Count + " joints between bodies; " + known + " of " + turrets.Count +
                                                 " rotator(s) have a known place in the vehicle (they can be calibrated together)");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the joints could not be read, the rotators are calibrated one by one (" + e.Message + ")");
                foreach (Turret turret in turrets) turret.Expect = null;
            }
        }

        // The guns a rotator turns: cut the joint; if the vehicle falls into two pieces, the piece without the heaviest body (the hull) is what it
        // turns. A joint with another joint in parallel (a loop) or one the graph does not know: null, "can't tell", and it is turned alone.
        private static HashSet<int> Predict(List<BodyEdge> all, Turret turret)
        {
            BodyEdge mine = null;
            foreach (BodyEdge edge in all) if (edge.Turret == turret) { mine = edge; break; }
            if (mine == null) return null;
            HashSet<Rigidbody> sideA = Reach(all, mine.A, mine), sideB = Reach(all, mine.B, mine);
            if (sideA.Contains(mine.B)) return null;
            HashSet<Rigidbody> moving = HeaviestMass(sideA) >= HeaviestMass(sideB) ? sideB : sideA;
            HashSet<int> result = new HashSet<int>();
            for (int g = 0; g < guns.Count; g++)
            {
                Rigidbody body = guns[g].Cannon != null ? BodyOf(guns[g].Cannon.PartScript) : null;
                if (body != null && moving.Contains(body)) result.Add(g);
            }
            return result.Count > 0 ? result : null;
        }

        private static HashSet<Rigidbody> Reach(List<BodyEdge> all, Rigidbody start, BodyEdge skip)
        {
            HashSet<Rigidbody> seen = new HashSet<Rigidbody> { start };
            Queue<Rigidbody> queue = new Queue<Rigidbody>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Rigidbody at = queue.Dequeue();
                foreach (BodyEdge edge in all)
                {
                    if (edge == skip) continue;
                    Rigidbody next = edge.A == at ? edge.B : edge.B == at ? edge.A : null;
                    if (next != null && seen.Add(next)) queue.Enqueue(next);
                }
            }
            return seen;
        }

        private static float HeaviestMass(HashSet<Rigidbody> bodies)
        {
            float heaviest = 0f;
            foreach (Rigidbody body in bodies) if (body != null && body.mass > heaviest) heaviest = body.mass;
            return heaviest;
        }

        private static void Calibrate(float dt)
        {
            if (calibration == null)
            {
                if (!graphTried)
                {
                    graphTried = true;
                    BuildGraph();
                }

                Turret first = null;
                foreach (Turret turret in turrets)
                {
                    if (turret.Done) continue;
                    if (!Moves(turret) || turret.Range < 1f)
                    {
                        turret.Done = true;
                        ArmorFragmentsPlugin.Log.LogWarning("Turret aim: rotator '" + turret.Name + "' can't be driven (broken, free spin, floppy or no range)");
                        continue;
                    }
                    first = turret;
                    break;
                }
                if (first == null)
                {
                    calibrated = true;
                    FinishCalibration();
                    return;
                }

                // The batch: the first rotator left, and every later one that either moves other guns than those already in it, or moves a gun
                // that one of them moves too but about a clearly different axis (a turret ring and its elevation: the barrel's rotation then
                // splits between them, see Along).
                List<Turret> members = new List<Turret> { first };
                if (!first.Solo && first.Expect != null)
                {
                    foreach (Turret other in turrets)
                    {
                        if (other == first || other.Done || other.Solo || other.Expect == null || !Moves(other) || other.Range < 1f) continue;
                        Vector3 axisOther = AxisOf(other.Script);
                        bool fits = true;
                        int sharing = 0;
                        foreach (Turret member in members)
                        {
                            if (!member.Expect.Overlaps(other.Expect)) continue;
                            sharing++;
                            if (Mathf.Abs(Vector3.Dot(axisOther, AxisOf(member.Script))) > 0.5f) { fits = false; break; }
                        }
                        if (fits && sharing < 2) members.Add(other);   // at most two rotators share a gun in a batch
                    }
                }
                bool together = members.Count > 1 || (!first.Solo && first.Expect != null);

                Calibration made = new Calibration();
                made.Barrels0 = new Vector3[guns.Count];
                for (int g = 0; g < guns.Count; g++) made.Barrels0[g] = BarrelLocal(g);
                foreach (Turret turret in members)
                {
                    float angle0 = angleRef(turret.Script);
                    made.Jogs.Add(new Jog
                    {
                        Turret = turret,
                        Angle0 = angle0,
                        Delta = turret.Wraps || angle0 + JogDegrees <= turret.Range ? JogDegrees : -JogDegrees,
                        Axis = LocalDir(AxisOf(turret.Script)),
                        Expect = together ? turret.Expect : null,
                    });
                }
                calibration = made;
                if (together)
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: calibrating " + members.Count + " rotator(s) at once (" +
                                                     (turrets.FindAll(t => !t.Done).Count - members.Count) + " more after this)");
            }

            Calibration c = calibration;
            foreach (Turret turret in turrets)
            {
                if (turret.Script == null) continue;
                float command = angleRef(turret.Script);
                foreach (Jog jog in c.Jogs) if (jog.Turret == turret) command = jog.Angle0 + jog.Delta;
                commands[turret.Script] = command;
            }

            c.Timer += dt;
            c.Elapsed += dt;
            Sample sample = new Sample { Time = c.Elapsed, Angles = new float[c.Jogs.Count], Barrels = new Vector3[guns.Count] };
            for (int j = 0; j < c.Jogs.Count; j++) sample.Angles[j] = angleRef(c.Jogs[j].Turret.Script);
            for (int g = 0; g < guns.Count; g++) sample.Barrels[g] = BarrelLocal(g);
            c.Samples.Add(sample);

            if (c.Phase == 0)
            {
                bool reached = true;
                for (int j = 0; j < c.Jogs.Count; j++)
                    if (Mathf.Abs(sample.Angles[j] - (c.Jogs[j].Angle0 + c.Jogs[j].Delta)) >= 0.05f) reached = false;
                if (reached || c.Timer > 4f) { c.Phase = 1; c.Timer = 0f; }
                return;
            }
            if (c.Timer < SettleSeconds) return;

            EvaluateJogs(c);
            calibration = null;
        }

        // How far (radians) a jog's rotator turned the gun's barrel, when the barrel has gone from where it was to `barrelNow`. A gun moved by one
        // rotator of the batch: the rotation along its axis. A gun moved by two (ring and elevation): the rotation split between the two axes.
        private static float Along(Calibration c, int jogIndex, int gun, Vector3 barrelNow)
        {
            Vector3 rotation = RotationVector(c.Barrels0[gun], barrelNow);
            Jog jog = c.Jogs[jogIndex];
            if (jog.Expect == null) return Vector3.Dot(rotation, jog.Axis);
            List<int> sharing = new List<int>();
            for (int j = 0; j < c.Jogs.Count; j++) if (c.Jogs[j].Expect != null && c.Jogs[j].Expect.Contains(gun)) sharing.Add(j);
            if (sharing.Count < 2) return Vector3.Dot(rotation, jog.Axis);
            Vector3[] axes = new Vector3[sharing.Count];
            for (int k = 0; k < sharing.Count; k++) axes[k] = c.Jogs[sharing[k]].Axis;
            double[] split = Solve(axes, rotation);
            return (float)split[sharing.IndexOf(jogIndex)];
        }

        private static void EvaluateJogs(Calibration c)
        {
            Vector3[] moved = new Vector3[guns.Count];
            for (int g = 0; g < guns.Count; g++)
                moved[g] = guns[g].Cannon != null ? RotationVector(c.Barrels0[g], BarrelLocal(g)) : Vector3.zero;

            bool together = c.Jogs[0].Expect != null;
            bool unexpected = false;
            if (together)
            {
                HashSet<int> expectedGuns = new HashSet<int>();
                float smallest = float.MaxValue;
                foreach (Jog jog in c.Jogs) { expectedGuns.UnionWith(jog.Expect); smallest = Mathf.Min(smallest, Mathf.Abs(jog.Delta) * Mathf.Deg2Rad); }
                for (int g = 0; g < guns.Count; g++)
                    if (!expectedGuns.Contains(g) && moved[g].magnitude > 0.5f * smallest) unexpected = true;
                if (unexpected)
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: a gun that was not expected to move did, so the batch of " + c.Jogs.Count +
                                                     " rotator(s) is measured again one by one");
            }

            for (int j = 0; j < c.Jogs.Count; j++)
            {
                Jog jog = c.Jogs[j];
                Turret turret = jog.Turret;
                float expected = jog.Delta * Mathf.Deg2Rad;

                float[] signs = new float[guns.Count], scales = new float[guns.Count], lags = new float[guns.Count];
                bool anyMoved = false, missing = unexpected;
                string effects = "";
                for (int g = 0; g < guns.Count; g++)
                {
                    if (guns[g].Cannon == null) continue;
                    bool wanted = jog.Expect != null && jog.Expect.Contains(g);
                    if (jog.Expect != null && !wanted) continue;
                    float along = Along(c, j, g, BarrelLocal(g));
                    if (Mathf.Abs(along) < 0.3f * Mathf.Abs(expected))
                    {
                        if (wanted) missing = true;
                        continue;
                    }
                    signs[g] = along / expected > 0f ? 1f : -1f;
                    scales[g] = Mathf.Clamp(Mathf.Abs(along / expected), 0.3f, 1.5f);
                    lags[g] = MeasureLag(c, j, g, along);
                    anyMoved = true;
                    effects += (effects.Length > 0 ? ", " : "") + "turret " + (g + 1) + " (group " + guns[g].GroupId + ")" + " " +
                               (along * Mathf.Rad2Deg).ToString("F1") + " deg (sign " + (signs[g] > 0f ? "+" : "-") + ", lag " + (lags[g] * 1000f).ToString("F0") + " ms)";
                }

                if (together && missing)
                {
                    turret.Solo = true;   // the guess was wrong or the rotator did not do what was expected: it is turned alone
                    continue;
                }

                turret.Done = true;
                if (anyMoved)
                {
                    turret.Usable = true;
                    for (int g = 0; g < guns.Count; g++) { turret.Signs[g] = signs[g]; turret.Scales[g] = scales[g]; turret.Lags[g] = lags[g]; }
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: rotator '" + turret.Name + "' for " + jog.Delta.ToString("F0") + " deg turns: " + effects +
                        "; range " + (turret.Wraps ? "all the way round" : turret.Range.ToString("F0") + " deg"));
                }
                else
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim: rotator '" + turret.Name + "' did not move any of the cannons for " +
                        jog.Delta.ToString("F0") + " deg: it is left out");
                }
            }
        }

        // How late the barrel follows the rotator: the time the barrel needed to get half way, minus the time the angle needed (the angle is
        // what the control asks for; the barrel gets there by the joint, which has its own delay and softness).
        private static float MeasureLag(Calibration c, int jogIndex, int gun, float finalAlong)
        {
            Jog jog = c.Jogs[jogIndex];
            float halfAngle = 0.5f * Mathf.Abs(jog.Delta);
            float halfBarrel = 0.5f * Mathf.Abs(finalAlong);
            float sign = Mathf.Sign(finalAlong);
            float timeAngle = -1f, timeBarrel = -1f;
            float previousTime = 0f, previousAngle = 0f, previousBarrel = 0f;
            foreach (Sample sample in c.Samples)
            {
                float angle = Mathf.Abs(sample.Angles[jogIndex] - jog.Angle0);
                float barrel = sign * Along(c, jogIndex, gun, sample.Barrels[gun]);
                if (timeAngle < 0f && angle >= halfAngle)
                    timeAngle = previousTime + (sample.Time - previousTime) * Mathf.Clamp01((halfAngle - previousAngle) / Mathf.Max(angle - previousAngle, 1e-6f));
                if (timeBarrel < 0f && barrel >= halfBarrel)
                    timeBarrel = previousTime + (sample.Time - previousTime) * Mathf.Clamp01((halfBarrel - previousBarrel) / Mathf.Max(barrel - previousBarrel, 1e-6f));
                previousTime = sample.Time;
                previousAngle = angle;
                previousBarrel = barrel;
            }
            if (timeAngle < 0f || timeBarrel < 0f) return 0f;
            return Mathf.Clamp(timeBarrel - timeAngle, 0.02f, 0.8f);
        }

        // After the last rotator: within each group, each turret gets the rotators that turn it and no earlier turret of the group (so no rotator is
        // driven by two turrets of a group); the same rotator can serve turrets of different groups, only one group being aimed at a time.
        private static void FinishCalibration()
        {
            foreach (Gun gun in guns) gun.Drivers.Clear();
            bool any = false;
            foreach (int groupId in groupIds)
            {
                HashSet<Turret> taken = new HashSet<Turret>();
                for (int g = 0; g < guns.Count; g++)
                {
                    Gun gun = guns[g];
                    if (gun.GroupId != groupId || gun.Cannon == null) continue;
                    foreach (Turret turret in turrets)
                    {
                        if (!turret.Usable || turret.Signs[g] == 0f || taken.Contains(turret)) continue;
                        gun.Drivers.Add(turret);
                        taken.Add(turret);
                    }
                    string label = gun.Cannon.PartScript != null && gun.Cannon.PartScript.Part != null ? gun.Cannon.PartScript.Part.Name : "?";
                    gun.Lag = 0f;
                    foreach (Turret driver in gun.Drivers) gun.Lag = Mathf.Max(gun.Lag, driver.Lags[g]);
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: turret " + (g + 1) + " ('" + label + "', group " + groupId + ") is turned by " + gun.Drivers.Count +
                                                     " rotator(s), the barrel follows " + (gun.Lag * 1000f).ToString("F0") + " ms late");
                    if (gun.Drivers.Count == 0)
                        ArmorFragmentsPlugin.Log.LogWarning("Turret aim: turret " + (g + 1) + " ('" + label + "') is not turned by any rotator marked Turret that an earlier turret of its group " +
                                                            "doesn't already use: it only follows that turret");
                    else any = true;
                }
            }
            if (!any) { Deactivate("no marked rotator moves any turret cannon"); return; }
            FindHull();
        }

        // The hull: from a gun's body, each rotator that has the body on one side leads to the body on its other side, and so on to the
        // vehicle's base. That body's angular velocity is what the stabilizer works against.
        private static Rigidbody hullBody;   // the lead turret's (the camera pivot is centered on it)

        private static Rigidbody BodyOf(PartScript part)
        {
            return part != null && part.Body != null && part.Body.RigidBody != null ? part.Body.RigidBody.PhysxRigidBody : null;
        }

        private static void FindHull()
        {
            hullBody = null;
            pivotBodies.Clear();
            for (int g = 0; g < guns.Count; g++)
            {
                Gun gun = guns[g];
                gun.Hull = null;
                Rigidbody current = gun.Cannon != null ? BodyOf(gun.Cannon.PartScript) : null;
                if (current == null) continue;
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
                gun.Hull = current;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the hull of turret " + (g + 1) + " is the body '" +
                                                 (current != null ? current.name : "?") + "' (" +
                                                 (left.Count == 0 ? "every rotator is on the way to it" : left.Count + " rotator(s) are not on the way") + ")");
            }

            // Every body joined to the hull (the whole vehicle): the camera pivot follows their centre of mass, which a rocking hull part does not move.
            if (edges != null)
            {
                foreach (Gun gun in guns)
                {
                    if (gun.Hull == null) continue;
                    foreach (Rigidbody body in Reach(edges, gun.Hull, null)) pivotBodies.Add(body);
                    break;
                }
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the camera pivot follows the centre of mass of " + pivotBodies.Count + " joined bodies");
            }
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
        private static RawImage ringImage, markerImage;
        private static Texture2D ringTexture, dotTexture;

        // A white picture whose alpha is a function of the distance from the middle (0 at the middle, 1 at the edge). Drawn by a RawImage, which
        // takes the texture as it is, so what is drawn is only the alpha and the color the RawImage is given.
        private static Texture2D MakeTexture(int size, Func<float, float> alphaAtRadius)
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
            texture.Apply(false, false);   // stays readable (the check below reads it)
            return texture;
        }

        private static RawImage MakeImage(Transform parent, string name, Texture2D texture, float size, out RectTransform rect)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            rect = go.AddComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            RawImage image = go.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            return image;
        }

        private static void MakeHud()
        {
            DestroyHud();
            if (ringTexture == null)
                ringTexture = MakeTexture(256, r => 1f - Edge(0.006f, 0.022f, Mathf.Abs(r - 0.9f)));
            if (dotTexture == null)
                dotTexture = MakeTexture(48, r => (1f - Edge(0.5f, 0.7f, r)) * 0.9f + (1f - Edge(0.04f, 0.12f, Mathf.Abs(r - 0.86f))) * 0.3f);

            hudRoot = new GameObject("ArmorFragments turret aim");
            Canvas canvas = hudRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            ringImage = MakeImage(hudRoot.transform, "circle", ringTexture, 64f, out ringRect);
            markerImage = MakeImage(hudRoot.transform, "gun marker", dotTexture, 16f, out markerRect);

            // If the circle shows up as a plain square again, this says whether the picture itself is right (alpha 0 in the middle of the ring,
            // 255 on it) and which shader the picture is drawn with.
            try
            {
                Material material = ringImage.materialForRendering;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim HUD: the ring picture has alpha " + ringTexture.GetPixel(64, 64).a.ToString("F2") + " in the middle and " +
                    ringTexture.GetPixel(64 + 56, 64).a.ToString("F2") + " on the ring; drawn with shader '" + (material != null && material.shader != null ? material.shader.name : "none") + "'");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim HUD check failed: " + e.Message);
            }
        }

        private static void DestroyHud()
        {
            if (hudRoot != null) UnityEngine.Object.Destroy(hudRoot);
            hudRoot = null;
            ringRect = markerRect = null;
            ringImage = markerImage = null;
        }

        // The circle is where the turret is told to point (the aim point, seen from the camera); the marker is where the barrel really points at
        // that distance. They meet when the gun is on target. Through the sight the camera sits on the gun, so the marker is not drawn.
        // Both are drawn like the crosshair of Minecraft: not in a color of their own but inverting what is behind them, so they always stand
        // out (see OnGui). The canvas pictures above are only the fallback if that can not be drawn.
        private static bool hudRingShown, hudMarkerShown;
        private static Vector2 hudRingAt, hudMarkerAt;   // screen pixels, y up
        private static readonly List<Vector2> hudMarkers = new List<Vector2>();   // one for each turret of the active group

        private static void UpdateHud(Camera cam, Vector3 muzzle, bool freeLook)
        {
            hudRingShown = hudMarkerShown = false;
            hudMarkers.Clear();
            if (haveDesired)
            {
                Vector3 aimPoint = muzzle + desired * aimDistance;
                Vector3 ring = cam.WorldToScreenPoint(aimPoint);
                hudRingShown = ring.z > 0f;
                hudRingAt = new Vector2(ring.x, ring.y);
                // Where each turret's barrel really points, at the distance of the aim point (not through the sight: the camera is on the gun).
                if (!sight)
                {
                    foreach (Gun gun in guns)
                    {
                        if (!InActiveGroup(gun) || !Live(gun)) continue;
                        Vector3 tip = gun.Cannon.TipPosition;
                        Vector3 marker = cam.WorldToScreenPoint(tip + gun.Cannon.transform.forward.normalized * Mathf.Max((aimPoint - tip).magnitude, 1f));
                        if (marker.z > 0f) hudMarkers.Add(new Vector2(marker.x, marker.y));
                    }
                }
                hudMarkerShown = hudMarkers.Count > 0;
                if (hudMarkerShown) hudMarkerAt = hudMarkers[0];
            }

            if (hudRoot == null || ringImage == null) return;
            bool canvas = !InvertingCrosshairReady();
            ringImage.enabled = canvas && hudRingShown;
            markerImage.enabled = canvas && hudMarkerShown;
            ringRect.position = new Vector3(hudRingAt.x, hudRingAt.y, 0f);
            markerRect.position = new Vector3(hudMarkerAt.x, hudMarkerAt.y, 0f);
            ringImage.color = freeLook ? new Color(0.45f, 0.85f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.85f);
            markerImage.color = new Color(1f, 1f, 1f, 0.9f);
        }

        // ---- The crosshair that inverts what is behind it ------------------------------------------------------------

        private static Material invertMaterial;
        private static bool invertTried;

        // Blend (1 - what is there) x (this) + what is there x (1 - this): white inverts the color behind it, black leaves it alone, grays blend, so
        // a soft edge (a color that falls to black) is anti-aliased. It uses the plain colored shader every Unity player has, with its blend set.
        private static bool InvertingCrosshairReady()
        {
            if (!invertTried)
            {
                invertTried = true;
                try
                {
                    Shader shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader != null)
                    {
                        invertMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        invertMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusDstColor);
                        invertMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcColor);
                        invertMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                        invertMaterial.SetInt("_ZWrite", 0);
                        if (invertMaterial.HasProperty("_ZTest")) invertMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    }
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim HUD: " + (invertMaterial != null
                        ? "the circle and the marker invert the picture behind them (Hidden/Internal-Colored)"
                        : "Hidden/Internal-Colored is not in this build, plain white pictures are used"));
                }
                catch (Exception e)
                {
                    invertMaterial = null;
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim HUD: the inverting crosshair could not be made (" + e.Message + "), plain white pictures are used");
                }
            }
            return invertMaterial != null;
        }

        private static void Vertex(float x, float y, float brightness)
        {
            GL.Color(new Color(brightness, brightness, brightness, 1f));
            GL.Vertex3(x, y, 0f);
        }

        private static void Quad(float x0, float y0, float b0, float x1, float y1, float b1, float x2, float y2, float b2, float x3, float y3, float b3)
        {
            Vertex(x0, y0, b0); Vertex(x1, y1, b1); Vertex(x2, y2, b2);
            Vertex(x0, y0, b0); Vertex(x2, y2, b2); Vertex(x3, y3, b3);
        }

        // A thin ring: full white along the middle, falling to nothing a little to either side.
        private static void DrawRing(float cx, float cy, float radius, float halfWidth)
        {
            const int Segments = 72;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                float inner = radius - halfWidth, outer = radius + halfWidth;
                Quad(cx + c0 * inner, cy + s0 * inner, 0f, cx + c1 * inner, cy + s1 * inner, 0f,
                     cx + c1 * radius, cy + s1 * radius, 1f, cx + c0 * radius, cy + s0 * radius, 1f);
                Quad(cx + c0 * radius, cy + s0 * radius, 1f, cx + c1 * radius, cy + s1 * radius, 1f,
                     cx + c1 * outer, cy + s1 * outer, 0f, cx + c0 * outer, cy + s0 * outer, 0f);
            }
        }

        // A small solid dot with a soft rim.
        private static void DrawDot(float cx, float cy, float radius)
        {
            const int Segments = 20;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                Vertex(cx, cy, 1f); Vertex(cx + c0 * radius, cy + s0 * radius, 1f); Vertex(cx + c1 * radius, cy + s1 * radius, 1f);
                Quad(cx + c0 * radius, cy + s0 * radius, 1f, cx + c1 * radius, cy + s1 * radius, 1f,
                     cx + c1 * (radius + 1f), cy + s1 * (radius + 1f), 0f, cx + c0 * (radius + 1f), cy + s0 * (radius + 1f), 0f);
            }
        }

        // Called by the plugin's OnGUI: the circle and the marker are drawn when the interface is (Repaint), over everything.
        internal static void OnGui()
        {
            if (!Active) return;
            Event current = Event.current;
            if (current == null || current.type != EventType.Repaint) return;
            if ((!hudRingShown && !hudMarkerShown) || !InvertingCrosshairReady()) return;

            float width = Screen.width, height = Screen.height;
            bool flip = ArmorFragmentsPlugin.TurretCrosshairFlipY.Value;
            GL.PushMatrix();
            try
            {
                invertMaterial.SetPass(0);
                GL.LoadPixelMatrix(0f, width, height, 0f);   // pixels, the origin at the top left
                GL.Begin(GL.TRIANGLES);
                if (hudRingShown) DrawRing(hudRingAt.x, flip ? hudRingAt.y : height - hudRingAt.y, 26f, 0.9f);
                foreach (Vector2 marker in hudMarkers) DrawDot(marker.x, flip ? marker.y : height - marker.y, 2.6f);
                GL.End();
            }
            finally
            {
                GL.PopMatrix();
            }
        }
    }

    // In mouse aim the turret group fires its own cannons (TurretAim.FireGuns); the game's weapon systems fire no cannon of the vehicle.
    [HarmonyPatch(typeof(CannonWeaponSystem), "Fire")]
    internal static class TurretGameFirePatch
    {
        private static bool Prefix(CannonWeaponSystem __instance, ref WeaponPart __result)
        {
            if (!TurretAim.BlocksGameCannons(__instance)) return true;
            __result = null;
            return false;
        }
    }

    // The sight view turns the game's mouse look off, and its controller would then free the cursor every frame.
    [HarmonyPatch(typeof(InteractiveCameraController), "UpdateCursor")]
    internal static class TurretCursorPatch
    {
        private static bool Prefix(InteractiveCameraController __instance)
        {
            return !TurretAim.OwnsCursor(__instance);
        }
    }

    // While the aim is on, the game's keys that switch the camera view (C by default) and the right button's mouse-joystick toggle do nothing.
    [HarmonyPatch(typeof(GameInput), "GetButtonDownIfEnabled")]
    internal static class TurretViewKeyPatch
    {
        private static void Postfix(GameInput __instance, ref bool __result)
        {
            TurretAim.BlockViewKeys(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetButtonDown")]
    internal static class TurretViewKeyDownPatch
    {
        private static void Postfix(GameInput __instance, ref bool __result)
        {
            TurretAim.BlockViewKeys(__instance, ref __result);
        }
    }

    // The orbit camera turns around the aim's own pivot while the aim is on. The swap lasts one call of the camera's Update and is undone at its
    // end (also if it throws), so nothing stays changed in the game's camera when the aim is switched off or the plugin is reloaded.
    [HarmonyPatch(typeof(OrbitCameraController), "Update")]
    internal static class TurretPivotPatch
    {
        private static void Prefix(OrbitCameraController __instance, out TurretAim.PivotSwap __state)
        {
            __state = TurretAim.SwapPivotIn(__instance);
        }

        private static Exception Finalizer(Exception __exception, OrbitCameraController __instance, TurretAim.PivotSwap __state)
        {
            TurretAim.SwapPivotOut(__instance, __state);
            return __exception;
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
