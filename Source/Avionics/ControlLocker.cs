﻿using System;
using System.Collections.Generic;
using UnityEngine;
using KSP.UI;
using System.Linq;
using System.Reflection;

namespace RP0
{
    public class ControlLockerUtils
    {
        public enum LockLevel
        {
            LOCKED,
            AXIAL,
            UNLOCKED
        }

        private static PartResourceDefinition electricChargeDef = null;
        public static LockLevel ShouldLock(List<Part> parts, bool countClamps, out float maxMass, out float vesselMass)
        {
            maxMass = vesselMass = 0f;
            bool forceUnlock = false;   // Defer return until maxMass and avionicsMass are fully calculated
            bool axial = false;

            if (parts == null || parts.Count <= 0)
                return LockLevel.UNLOCKED;
            if (!HighLogic.LoadedSceneIsFlight && !HighLogic.LoadedSceneIsEditor) return LockLevel.UNLOCKED;

            int crewCount = (HighLogic.LoadedSceneIsFlight) ? parts[0].vessel.GetCrewCount() : CrewAssignmentDialog.Instance.GetManifest().GetAllCrew(false).Count;
            electricChargeDef ??= PartResourceLibrary.Instance.GetDefinition("ElectricCharge");

            foreach (Part p in parts)
            {
                // add up mass
                float partMass = p.mass + p.GetResourceMass();

                // get modules
                bool cmd = false, science = false, avionics = false, clamp = false;
                float partAvionicsMass = 0f;
                double ecResource = 0;
                ModuleCommand mC = null;
                foreach (PartModule m in p.Modules)
                {
                    if (m is KerbalEVA)
                        forceUnlock = true;
                    if (m is ModuleCommand || m is ModuleAvionics)
                        p.GetConnectedResourceTotals(electricChargeDef.id, out ecResource, out double _);
                    if (m is ModuleCommand && (ecResource > 0 || HighLogic.LoadedSceneIsEditor))
                    {
                        cmd = true;
                        mC = m as ModuleCommand;
                    }
                    if (m is ModuleScienceCore)
                    {
                        science = true;
                        if (ecResource > 0 || HighLogic.LoadedSceneIsEditor)
                        {
                            ModuleScienceCore mSC = m as ModuleScienceCore;
                            if (mSC.allowAxial)
                                axial = true;
                        }
                    }
                    if (m is ModuleAvionics)
                    {
                        avionics = true;
                        ModuleAvionics mA = m as ModuleAvionics;
                        if (ecResource > 0 || HighLogic.LoadedSceneIsEditor)
                        {
                            partAvionicsMass += mA.CurrentMassLimit;
                            if (mA.allowAxial)
                                axial = true;
                        }
                    }
                    if (m is LaunchClamp)
                    {
                        clamp = true;
                        partMass = 0f;
                    }
                    if (m is ModuleAvionicsModifier)
                    {
                        partMass *= (m as ModuleAvionicsModifier).multiplier;
                    }
                }
                vesselMass += partMass; // done after the clamp check

                // Do we have an unencumbered command module?
                // if we count clamps, they can give control. If we don't, this works only if the part isn't a clamp.
                if ((countClamps || !clamp) && cmd && !science && !avionics)
                    forceUnlock = true;
                if (cmd && avionics && mC.minimumCrew > crewCount) // check if need crew
                    avionics = false; // not operational
                if (avionics)
                    maxMass = HighLogic.CurrentGame.Parameters.CustomParams<RP0Settings>().IsAvionicsStackable ? maxMass + partAvionicsMass : Math.Max(maxMass, partAvionicsMass);
            }

            if (!forceUnlock && vesselMass > maxMass)  // Lock if vessel mass is greater than controlled mass.
                return axial ? LockLevel.AXIAL : LockLevel.LOCKED;

            return LockLevel.UNLOCKED;
        }
    }

    /// <summary>
    /// This class will lock controls if and only if avionics requirements exist and are not met
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    class ControlLocker : MonoBehaviour
    {
        public Vessel vessel = null;
        private ControlLockerUtils.LockLevel oldLockLevel = ControlLockerUtils.LockLevel.UNLOCKED;
        private bool requested = false;
        private bool onRails = false;
        private ControlLockerUtils.LockLevel cachedLockResult = ControlLockerUtils.LockLevel.UNLOCKED;
        private const ControlTypes lockmask = ControlTypes.YAW | ControlTypes.PITCH | ControlTypes.ROLL | ControlTypes.SAS | 
                                              ControlTypes.THROTTLE | ControlTypes.WHEEL_STEER | ControlTypes.WHEEL_THROTTLE;
        private const string lockID = "RP0ControlLocker";
        private float maxMass, vesselMass;

        private const float updateFrequency = 1; // Default check interval

        private readonly ScreenMessage message = new ScreenMessage("", 8f, ScreenMessageStyle.UPPER_CENTER);
        internal const string ModTag = "[RP-1 ControlLocker]";

        // For locking MJ.
        private static bool isFirstLoad = true;
        private static MethodInfo getMasterMechJeb = null;
        private static PropertyInfo mjDeactivateControl = null;
        private object masterMechJeb = null;

        private void Awake()
        {
            if (!isFirstLoad) return;
            isFirstLoad = false;

            if (AssemblyLoader.loadedAssemblies.FirstOrDefault(a => a.assembly.GetName().Name == "MechJeb2") is var mechJebAssembly &&
               Type.GetType("MuMech.MechJebCore, MechJeb2") is Type mechJebCore &&
               Type.GetType("MuMech.VesselExtensions, MechJeb2") is Type mechJebVesselExtensions)
            {
                mjDeactivateControl = mechJebCore.GetProperty("DeactivateControl", BindingFlags.Public | BindingFlags.Instance);
                getMasterMechJeb = mechJebVesselExtensions.GetMethod("GetMasterMechJeb", BindingFlags.Public | BindingFlags.Static);
            }
            if (mjDeactivateControl != null && getMasterMechJeb != null)
                Debug.Log($"{ModTag} MechJeb methods found");
            else
                Debug.Log($"{ModTag} MJ assembly or methods NOT found");
        }

        private void Start()
        {
            GameEvents.onVesselWasModified.Add(OnVesselModifiedHandler);
            GameEvents.onVesselSwitching.Add(OnVesselSwitchingHandler);
            GameEvents.onVesselGoOnRails.Add(OnRailsHandler);
            GameEvents.onVesselGoOffRails.Add(OffRailsHandler);
            vessel = FlightGlobals.ActiveVessel;
            if (vessel && vessel.loaded) vessel.OnPostAutopilotUpdate += FlightInputModifier;
            StartCoroutine(CheckLockCR());
        }

        protected void OnVesselSwitchingHandler(Vessel v1, Vessel v2)
        {
            // Apply only when switching does not result in new scene load.
            if (v1 && v2 && v2.loaded) v2.OnPostAutopilotUpdate += FlightInputModifier;
        }

        protected void OnVesselModifiedHandler(Vessel v) => requested = true;
        private void OnRailsHandler(Vessel v) => onRails = true;
        private void OffRailsHandler(Vessel v)
        {
            onRails = false;
            if (!CheatOptions.InfiniteElectricity && ControlLockerUtils.ShouldLock(vessel.Parts, true, out float _, out float _) != ControlLockerUtils.LockLevel.UNLOCKED)
                DisableAutopilot();
        }

        void FlightInputModifier(FlightCtrlState state)
        {
            if (oldLockLevel != ControlLockerUtils.LockLevel.UNLOCKED)
            {
                state.X = state.Y = 0;                      // Disable X/Y translation
                if (oldLockLevel == ControlLockerUtils.LockLevel.LOCKED)                              // Allow Z(fwd/ reverse) only if science core allows it
                    state.Z = 0;
                state.yaw = state.pitch = state.roll = 0;   // Disable roll control
            }
        }

        public ControlLockerUtils.LockLevel ShouldLock()
        {
            // if we have no active vessel, undo locks
            if (vessel is null)
                return cachedLockResult = ControlLockerUtils.LockLevel.UNLOCKED;
            if (requested)
                cachedLockResult = CheatOptions.InfiniteElectricity ? ControlLockerUtils.LockLevel.UNLOCKED : ControlLockerUtils.ShouldLock(vessel.Parts, true, out maxMass, out vesselMass);
            requested = false;
            return cachedLockResult;
        }

        private System.Collections.IEnumerator CheckLockCR()
        {
            while (HighLogic.LoadedSceneIsFlight)
            {
                yield return new WaitForSeconds(updateFrequency);
                cachedLockResult = CheatOptions.InfiniteElectricity ? ControlLockerUtils.LockLevel.UNLOCKED : ControlLockerUtils.ShouldLock(vessel.Parts, true, out maxMass, out vesselMass);
            }
        }

        public void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
            {
                InputLockManager.RemoveControlLock(lockID);
                return;
            }
            if (vessel != FlightGlobals.ActiveVessel)
            {
                vessel = FlightGlobals.ActiveVessel;
                masterMechJeb = null;
            }
            ControlLockerUtils.LockLevel lockLevel = ShouldLock();
            if (lockLevel != oldLockLevel)
            {
                if (oldLockLevel != ControlLockerUtils.LockLevel.UNLOCKED)
                {
                    InputLockManager.RemoveControlLock(lockID);
                    message.message = "Avionics: Unlocking Controls";
                }
                else
                {
                    InputLockManager.SetControlLock(lockmask, lockID);
                    if (!onRails) 
                        DisableAutopilot();
                    message.message = $"Insufficient Avionics, Locking Controls (supports {maxMass:N3}t, vessel {vesselMass:N3}t)";
                }
                ScreenMessages.PostScreenMessage(message);
                FlightLogger.fetch.LogEvent(message.message);
                oldLockLevel = lockLevel;
            }

            if (masterMechJeb == null && getMasterMechJeb != null)
            {
                masterMechJeb = getMasterMechJeb.Invoke(null, new object[] { FlightGlobals.ActiveVessel });
            }
            if (masterMechJeb != null && mjDeactivateControl != null)
            {
                // Update MJ every tick, to make sure it gets correct state after separation / docking.
                mjDeactivateControl.SetValue(masterMechJeb, lockLevel != ControlLockerUtils.LockLevel.UNLOCKED, index: null);
            }
        }

        public void OnDestroy()
        {
            InputLockManager.RemoveControlLock(lockID);
            GameEvents.onVesselWasModified.Remove(OnVesselModifiedHandler);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitchingHandler);
            GameEvents.onVesselGoOnRails.Remove(OnRailsHandler);
            GameEvents.onVesselGoOffRails.Remove(OffRailsHandler);
        }

        private void DisableAutopilot()
        {
            vessel.Autopilot.Disable();
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
        }
    }
}
