﻿#region

using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

using KerbalEngineer.Flight;

using UnityEngine;

#endregion

namespace KerbalEngineer.Simulation
{
    public class SimManager : IUpdatable, IUpdateRequest
    {
        #region Instance

        private static readonly SimManager instance = new SimManager();

        public static SimManager Instance
        {
            get { return instance; }
        }

        #endregion

        public const double RESOURCE_MIN = 0.0001;

        private static bool bRequested;
        private static bool bRunning;
        private static readonly Stopwatch timer = new Stopwatch();
        private static long delayBetweenSims;

        public static long minSimTime = 150;

        // Support for RealFuels using reflection to check localCorrectThrust without dependency
        private static bool hasCheckedForRealFuels;
        private static bool hasInstalledRealFuels;

        private static Type RF_ModuleEngineConfigs_Type;
        private static Type RF_ModuleHybridEngine_Type;

        private static FieldInfo RF_ModuleEngineConfigs_locaCorrectThrust;
        private static FieldInfo RF_ModuleHybridEngine_locaCorrectThrust;
        public static Stage[] Stages { get; private set; }
        public static Stage LastStage { get; private set; }
        public static String failMessage { get; private set; }
        public static double Gravity { get; set; }
        public static double Atmosphere { get; set; }

        #region IUpdatable Members

        public void Update()
        {
            TryStartSimulation();
        }

        #endregion

        #region IUpdateRequest Members

        public bool UpdateRequested { get; set; }

        #endregion

        public static void RequestUpdate()
        {
            instance.UpdateRequested = true;
            RequestSimulation();
        }

        private static void GetRealFuelsTypes()
        {
            hasCheckedForRealFuels = true;

            foreach (AssemblyLoader.LoadedAssembly assembly in AssemblyLoader.loadedAssemblies)
            {
                MonoBehaviour.print("Assembly:" + assembly.assembly);

                if (assembly.assembly.ToString().Split(',')[0] == "RealFuels")
                {
                    MonoBehaviour.print("Found RealFuels mod");

                    RF_ModuleEngineConfigs_Type = assembly.assembly.GetType("RealFuels.ModuleEngineConfigs");
                    if (RF_ModuleEngineConfigs_Type == null)
                    {
                        MonoBehaviour.print("Failed to find ModuleEngineConfigs type");
                        break;
                    }

                    RF_ModuleEngineConfigs_locaCorrectThrust = RF_ModuleEngineConfigs_Type.GetField("localCorrectThrust");
                    if (RF_ModuleEngineConfigs_locaCorrectThrust == null)
                    {
                        MonoBehaviour.print("Failed to find ModuleEngineConfigs.localCorrectThrust field");
                        break;
                    }

                    RF_ModuleHybridEngine_Type = assembly.assembly.GetType("RealFuels.ModuleHybridEngine");
                    if (RF_ModuleHybridEngine_Type == null)
                    {
                        MonoBehaviour.print("Failed to find ModuleHybridEngine type");
                        break;
                    }

                    RF_ModuleHybridEngine_locaCorrectThrust = RF_ModuleHybridEngine_Type.GetField("localCorrectThrust");
                    if (RF_ModuleHybridEngine_locaCorrectThrust == null)
                    {
                        MonoBehaviour.print("Failed to find ModuleHybridEngine.localCorrectThrust field");
                        break;
                    }

                    hasInstalledRealFuels = true;
                    break;
                }
            }
        }

        public static bool DoesEngineUseCorrectedThrust(Part theEngine)
        {
            if (!hasInstalledRealFuels /*|| HighLogic.LoadedSceneIsFlight*/)
            {
                return false;
            }

            // Look for either of the Real Fuels engine modules and call the relevant method to find out
            PartModule modEngineConfigs = theEngine.Modules["ModuleEngineConfigs"];
            if (modEngineConfigs != null)
            {
                // Check the localCorrectThrust
                if ((bool)RF_ModuleEngineConfigs_locaCorrectThrust.GetValue(modEngineConfigs))
                {
                    return true;
                }
            }

            PartModule modHybridEngine = theEngine.Modules["ModuleHybridEngine"];
            if (modHybridEngine != null)
            {
                // Check the localCorrectThrust
                if ((bool)RF_ModuleHybridEngine_locaCorrectThrust.GetValue(modHybridEngine))
                {
                    return true;
                }
            }

            return false;
        }

        public static void RequestSimulation()
        {
            if (!hasCheckedForRealFuels)
            {
                GetRealFuelsTypes();
            }

            bRequested = true;
            if (!timer.IsRunning)
            {
                timer.Start();
            }
        }

        public static void TryStartSimulation()
        {
            if (bRequested && !bRunning && (HighLogic.LoadedSceneIsEditor || FlightGlobals.ActiveVessel != null) && timer.ElapsedMilliseconds > delayBetweenSims)
            {
                bRequested = false;
                timer.Reset();
                StartSimulation();
            }
        }

        public static bool ResultsReady()
        {
            return !bRunning;
        }

        private static void ClearResults()
        {
            failMessage = "";
            Stages = null;
            LastStage = null;
        }

        private static void StartSimulation()
        {
            try
            {
                bRunning = true;
                ClearResults();
                timer.Start();

                List<Part> parts = HighLogic.LoadedSceneIsEditor ? EditorLogic.SortedShipList : FlightGlobals.ActiveVessel.Parts;

                // Create the Simulation object in this thread
                Simulation sim = new Simulation();

                // This call doesn't ever fail at the moment but we'll check and return a sensible error for display
                if (sim.PrepareSimulation(parts, Gravity, Atmosphere))
                {
                    ThreadPool.QueueUserWorkItem(RunSimulation, sim);
                }
                else
                {
                    failMessage = "PrepareSimulation failed";
                    bRunning = false;
                }
            }
            catch (Exception e)
            {
                MonoBehaviour.print("Exception in StartSimulation: " + e);
                failMessage = e.ToString();
                bRunning = false;
            }
        }

        private static void RunSimulation(object simObject)
        {
            try
            {
                Stages = (simObject as Simulation).RunSimulation();
                if (Stages != null)
                {
#if LOG
                    foreach (Stage stage in Stages)
                        stage.Dump();
#endif
                    LastStage = Stages.Last();
                }
            }
            catch (Exception e)
            {
                MonoBehaviour.print("Exception in RunSimulation: " + e);
                Stages = null;
                LastStage = null;
                failMessage = e.ToString();
            }

            timer.Stop();
            MonoBehaviour.print("Total simulation time: " + timer.ElapsedMilliseconds + "ms");
            delayBetweenSims = minSimTime - timer.ElapsedMilliseconds;
            if (delayBetweenSims < 0)
            {
                delayBetweenSims = 0;
            }

            timer.Reset();
            timer.Start();

            bRunning = false;
        }
    }
}