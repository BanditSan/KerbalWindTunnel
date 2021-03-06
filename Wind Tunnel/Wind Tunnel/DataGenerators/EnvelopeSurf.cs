﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using KerbalWindTunnel.Threading;
using Graphing;
using KerbalWindTunnel.Extensions;

namespace KerbalWindTunnel.DataGenerators
{
    public partial class EnvelopeSurf : DataSetGenerator
    {
        #region EnvelopeSurf
        protected static readonly ColorMap Jet_Dark_Positive = new ColorMap(ColorMap.Jet_Dark) { Filter = (v) => v >= 0 && !float.IsNaN(v) && !float.IsInfinity(v) };

        public EnvelopePoint[,] envelopePoints = new EnvelopePoint[0, 0];
        public Conditions currentConditions = Conditions.Blank;
        private Dictionary<Conditions, EnvelopePoint[,]> cache = new Dictionary<Conditions, EnvelopePoint[,]>();
        private Dictionary<Conditions, Conditions> cachedConditions = new Dictionary<Conditions, Conditions>();
        
        public EnvelopeSurf()
        {
            graphables.Clear();

            float bottom = 0, top = 0, left = 0, right = 0;
            float[,] blank = new float[0, 0];

            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Excess Thrust", ZUnit = "kN", StringFormat = "N0", Color = Jet_Dark_Positive });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Excess Acceleration", ZUnit = "g", StringFormat = "N2", Color = Jet_Dark_Positive });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Thrust Available", ZUnit = "kN", StringFormat = "N0", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Level AoA", ZUnit = "°", StringFormat = "F2", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Max Lift AoA", ZUnit = "°", StringFormat = "F2", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Max Lift", ZUnit = "kN", StringFormat = "N0", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Lift/Drag Ratio", ZUnit = "", StringFormat = "F2", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Drag", ZUnit = "kN", StringFormat = "N0", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Lift Slope", ZUnit = "m^2/°", StringFormat = "F3", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Pitch Input", ZUnit = "", StringFormat = "F2", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Fuel Burn Rate", ZUnit = "kg/s", StringFormat = "F3", Color = ColorMap.Jet_Dark });
            graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Fuel Economy", ZUnit = "kg/100 km", StringFormat = "F2", Color = ColorMap.Jet_Dark });
            //graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Stability Derivative", ZUnit = "kNm/deg", StringFormat = "F3", Color = ColorMap.Jet_Dark });
            //graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Stability Range", ZUnit = "deg", StringFormat = "F2", Color = ColorMap.Jet_Dark });
            //graphables.Add(new SurfGraph(blank, left, right, bottom, top) { Name = "Stability Score", ZUnit = "kNm-deg", StringFormat = "F1", Color = ColorMap.Jet_Dark });
            graphables.Add(new OutlineMask(blank, left, right, bottom, top) { Name = "Envelope Mask", ZUnit = "kN", StringFormat = "N0", Color = Color.grey, LineWidth = 2, LineOnly = true, MaskCriteria = (v) => !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0 });
            graphables.Add(new MetaLineGraph(new Vector2[0])              { Name = "Fuel-Optimal Path", StringFormat = "N0", Color = Color.black, LineWidth = 3, MetaFields = new string[] { "Climb Angle", "Climb Rate", "Fuel Used", "Time" }, MetaStringFormats = new string[] { "N1", "N0", "N3", "N1" }, MetaUnits = new string[] { "°", "m/s", "units", "s" } });
            graphables.Add(new MetaLineGraph(new Vector2[0])              { Name = "Time-Optimal Path", StringFormat = "N0", Color = Color.white, LineWidth = 3, MetaFields = new string[] { "Climb Angle", "Climb Rate", "Time" }, MetaStringFormats = new string[] { "N1", "N0", "N1" }, MetaUnits = new string[] { "°", "m/s", "s" } });

            var e = graphables.GetEnumerator();
            while (e.MoveNext())
            {
                e.Current.XUnit = "m/s";
                e.Current.XName = "Speed";
                e.Current.YUnit = "m";
                e.Current.YName = "Altitude";
                e.Current.Visible = false;
            }
        }

        public override void Clear()
        {
            base.Clear();
            currentConditions = Conditions.Blank;
            cache.Clear();
            cachedConditions.Clear();
            envelopePoints = new EnvelopePoint[0, 0];

            ((LineGraph)graphables["Fuel-Optimal Path"]).SetValues(new Vector2[0]);
            ((LineGraph)graphables["Time-Optimal Path"]).SetValues(new Vector2[0]);
        }

        public override void Cancel()
        {
            if (calculationManager.Status != CalculationManager.RunStatus.PreStart)
            {
                if (calculationManager.PercentComplete < 0.75)
                {
                    calculationManager.Cancel();
                    calculationManager.Dispose();
                }
                else
                    calculationManager.OnCompleteCallback += () => { calculationManager.Dispose(); };
                calculationManager = new CalculationManager();
            }
            valuesSet = false;
        }

        private bool TryGetContaining(Conditions conditions, out Conditions containingConditions, out EnvelopePoint[,] points)
        {
            containingConditions = new Conditions();
            points = new EnvelopePoint[0, 0];
            Dictionary<Conditions, EnvelopePoint[,]>.Enumerator enumerator = cache.GetEnumerator();
            int bestNumPoints = 0;
            bool result = false;
            while (enumerator.MoveNext())
            {
                Conditions enumConditions = cachedConditions[enumerator.Current.Key];
                if (enumConditions.Contains(conditions))
                {
                    result = true;
                    int numPoints = Mathf.FloorToInt((conditions.upperBoundSpeed - conditions.lowerBoundSpeed) / enumConditions.stepSpeed + 1) *
                        Mathf.FloorToInt((conditions.upperBoundAltitude - conditions.lowerBoundAltitude) / enumConditions.stepAltitude + 1);
                    if (numPoints > bestNumPoints)
                    {
                        bestNumPoints = numPoints;
                        containingConditions = enumConditions;
                        points = cache[enumConditions];
                    }
                }
            }
            return result;
        }

        public void Calculate(AeroPredictor vessel, CelestialBody body, float lowerBoundSpeed = 0, float upperBoundSpeed = 2000, float stepSpeed = 50f, float lowerBoundAltitude = 0, float upperBoundAltitude = 60000, float stepAltitude = 500)
        {
            Conditions newConditions = new Conditions(body, lowerBoundSpeed, upperBoundSpeed, stepSpeed, lowerBoundAltitude, upperBoundAltitude, stepAltitude);
            if (currentConditions.Equals(newConditions) && calculationManager.Status != CalculationManager.RunStatus.PreStart)
                return;

            Cancel();

            bool loadedCache = false;
            Conditions loadedConditions;
            if (cache.TryGetValue(newConditions, out envelopePoints))
            {
                loadedCache = true;
                loadedConditions = cachedConditions[newConditions];
                currentConditions = loadedConditions;
            }
            else if (TryGetContaining(newConditions, out loadedConditions, out envelopePoints))
            {
                loadedCache = true;
                currentConditions = loadedConditions;
            }
            if (loadedCache)
            {
                calculationManager.Status = CalculationManager.RunStatus.Completed;
                UpdateGraphs();
                valuesSet = true;

                if (loadedConditions.stepSpeed > stepSpeed || loadedConditions.stepAltitude > stepAltitude)
                {
                    WindTunnel.Instance.StartCoroutine(RefinementProcessing(calculationManager, newConditions, vessel, envelopePoints, loadedConditions,
                        new Queue<Conditions>(new Conditions[] { newConditions.Modify(stepSpeed: stepSpeed / 2, stepAltitude: stepAltitude / 2) }), true));
                }
                else if (loadedConditions.stepSpeed > stepSpeed / 2 || loadedConditions.stepAltitude > stepAltitude / 2)
                {
                    WindTunnel.Instance.StartCoroutine(RefinementProcessing(calculationManager, newConditions, vessel, envelopePoints, loadedConditions, forcePushToGraph: true));
                }

                return;
            }
            
            WindTunnel.Instance.StartCoroutine(Processing(calculationManager, newConditions, vessel));
        }

        private void UpdateGraphs()
        {
            float bottom = currentConditions.lowerBoundAltitude;
            float top = currentConditions.upperBoundAltitude;
            float left = currentConditions.lowerBoundSpeed;
            float right = currentConditions.upperBoundSpeed;
            Func<EnvelopePoint, float> scale = (pt) => 1;
            if (WindTunnelSettings.UseCoefficients)
                scale = (pt) => 1 / pt.dynamicPressure;

            ((SurfGraph)graphables["Excess Thrust"]).SetValues(envelopePoints.SelectToArray(pt => pt.Thrust_excess), left, right, bottom, top);
            ((SurfGraph)graphables["Excess Acceleration"]).SetValues(envelopePoints.SelectToArray(pt => pt.Accel_excess), left, right, bottom, top);
            ((SurfGraph)graphables["Thrust Available"]).SetValues(envelopePoints.SelectToArray(pt => pt.Thrust_available), left, right, bottom, top);
            ((SurfGraph)graphables["Level AoA"]).SetValues(envelopePoints.SelectToArray(pt => pt.AoA_level * Mathf.Rad2Deg), left, right, bottom, top);
            ((SurfGraph)graphables["Max Lift AoA"]).SetValues(envelopePoints.SelectToArray(pt => pt.AoA_max * Mathf.Rad2Deg), left, right, bottom, top);
            ((SurfGraph)graphables["Max Lift"]).SetValues(envelopePoints.SelectToArray(pt => pt.Lift_max), left, right, bottom, top);
            ((SurfGraph)graphables["Lift/Drag Ratio"]).SetValues(envelopePoints.SelectToArray(pt => pt.LDRatio), left, right, bottom, top);
            ((SurfGraph)graphables["Drag"]).SetValues(envelopePoints.SelectToArray(pt => pt.drag * scale(pt)), left, right, bottom, top);
            ((SurfGraph)graphables["Lift Slope"]).SetValues(envelopePoints.SelectToArray(pt => pt.dLift / pt.dynamicPressure), left, right, bottom, top);
            ((SurfGraph)graphables["Pitch Input"]).SetValues(envelopePoints.SelectToArray(pt => pt.pitchInput), left, right, bottom, top);
            ((SurfGraph)graphables["Fuel Burn Rate"]).SetValues(envelopePoints.SelectToArray(pt => pt.fuelBurnRate), left, right, bottom, top);
            //((SurfGraph)graphables["Stability Derivative"]).SetValues(envelopePoints.SelectToArray(pt => pt.stabilityDerivative), left, right, bottom, top);
            //((SurfGraph)graphables["Stability Range"]).SetValues(envelopePoints.SelectToArray(pt => pt.stabilityRange), left, right, bottom, top);
            //((SurfGraph)graphables["Stability Score"]).SetValues(envelopePoints.SelectToArray(pt => pt.stabilityScore), left, right, bottom, top);

            float[,] economy = envelopePoints.SelectToArray(pt => pt.fuelBurnRate / pt.speed * 1000 * 100);
            int stallpt = CoordLocator.GenerateCoordLocators(envelopePoints.SelectToArray(pt=>pt.Thrust_excess)).First(0, 0, c => c.value>=0);
            SurfGraph toModify = (SurfGraph)graphables["Fuel Economy"];
            toModify.SetValues(economy, left, right, bottom, top);
            float minEconomy = economy[stallpt, 0] / 3;
            toModify.ZMax = minEconomy;
            ((OutlineMask)graphables["Envelope Mask"]).SetValues(envelopePoints.SelectToArray(pt => pt.Thrust_excess), left, right, bottom, top);
        }

        private IEnumerator Processing(CalculationManager manager, Conditions conditions, AeroPredictor vessel)
        {
            int numPtsX = (int)Math.Ceiling((conditions.upperBoundSpeed - conditions.lowerBoundSpeed) / conditions.stepSpeed);
            int numPtsY = (int)Math.Ceiling((conditions.upperBoundAltitude - conditions.lowerBoundAltitude) / conditions.stepAltitude);
            EnvelopePoint[,] newEnvelopePoints = new EnvelopePoint[numPtsX + 1, numPtsY + 1];
            
            GenData rootData = new GenData(vessel, conditions, 0, 0, manager);
            
            //System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
            //timer.Start();
            ThreadPool.QueueUserWorkItem(SetupInBackground, rootData, true);

            while (!manager.Completed)
            {
                //Debug.Log(manager.PercentComplete + "% done calculating...");
                if (manager.Status == CalculationManager.RunStatus.Cancelled)
                    yield break;
                yield return 0;
            }
            //timer.Stop();
            //Debug.Log("Time taken: " + timer.ElapsedMilliseconds / 1000f);
            
            newEnvelopePoints = ((CalculationManager.State[,])rootData.storeState.Result)
                .SelectToArray(pt => (EnvelopePoint)pt.Result);

            AddToCache(conditions, newEnvelopePoints);
            if (!manager.Cancelled)
            {
                envelopePoints = newEnvelopePoints;
                currentConditions = conditions;
                UpdateGraphs();
                CalculateOptimalLines(vessel, conditions, WindTunnelWindow.Instance.TargetSpeed, WindTunnelWindow.Instance.TargetAltitude, 0, 0);
                
                valuesSet = true;
            }
            yield return 0;

            if (!manager.Cancelled)
            {
                Conditions nextConditions = conditions.Modify(stepSpeed: conditions.stepSpeed / 2, stepAltitude: conditions.stepAltitude / 2);
                WindTunnel.Instance.StartCoroutine(RefinementProcessing(calculationManager, nextConditions, vessel, newEnvelopePoints, conditions));
            }
        }

        private IEnumerator RefinementProcessing(CalculationManager manager, Conditions conditions, AeroPredictor vessel, EnvelopePoint[,] basisData, Conditions basisConditions = new Conditions(), Queue<Conditions> followOnConditions = null, bool forcePushToGraph = false)
        {
            int numPtsX = (int)Math.Ceiling((conditions.upperBoundSpeed - conditions.lowerBoundSpeed) / conditions.stepSpeed);
            int numPtsY = (int)Math.Ceiling((conditions.upperBoundAltitude - conditions.lowerBoundAltitude) / conditions.stepAltitude);
            EnvelopePoint[,] newEnvelopePoints = new EnvelopePoint[numPtsX + 1, numPtsY + 1];
            
            CalculationManager backgroundManager = new CalculationManager();
            manager.OnCancelCallback += backgroundManager.Cancel;
            CalculationManager.State[,] results = new CalculationManager.State[numPtsX + 1, numPtsY + 1];
            GenData rootData = new GenData(vessel, conditions, 0, 0, backgroundManager);
            ThreadPool.QueueUserWorkItem(ContinueInBackground, new object[] { rootData, results, basisData, basisConditions });
            while (!backgroundManager.Completed)
            {
                if (manager.Status == CalculationManager.RunStatus.Cancelled)
                {
                    backgroundManager.Cancel();
                    yield break;
                }
                yield return 0;
            }
            manager.OnCancelCallback -= backgroundManager.Cancel;

            newEnvelopePoints = ((CalculationManager.State[,])rootData.storeState.Result)
                .SelectToArray(pt => (EnvelopePoint)pt.Result);

            AddToCache(conditions, newEnvelopePoints);
            if (currentConditions.Equals(conditions) || (forcePushToGraph && !backgroundManager.Cancelled))
            {
                envelopePoints = newEnvelopePoints;
                currentConditions = conditions;
                UpdateGraphs();
                valuesSet = true;
            }
            backgroundManager.Dispose();
            if (!manager.Cancelled && followOnConditions != null && followOnConditions.Count > 0)
            {
                yield return 0;
                Conditions nextConditions = followOnConditions.Dequeue();
                WindTunnel.Instance.StartCoroutine(RefinementProcessing(manager, nextConditions, vessel, newEnvelopePoints, conditions, followOnConditions, forcePushToGraph));
            }
        }

        private bool AddToCache(Conditions conditions, EnvelopePoint[,] data)
        {
            if (cache.ContainsKey(conditions))
            {
                if (cache[conditions].Length > data.Length)
                    return false;
                else
                    cache.Remove(conditions);
            }
            cache[conditions] = data;
            cachedConditions[conditions] = conditions;
            return true;
        }

        private static void SetupInBackground(object obj)
        {
            GenData rootData = (GenData)obj;
            Conditions conditions = rootData.conditions;
            CalculationManager manager = rootData.storeState.manager;
            CalculationManager seedManager = new CalculationManager();
            CalculationManager.State[,] results = null;
            
            Conditions seedConditions = new Conditions(conditions.body, conditions.lowerBoundSpeed, conditions.upperBoundSpeed, 11, conditions.lowerBoundAltitude, conditions.upperBoundAltitude, 11);
            GenerateLevel(seedConditions, seedManager, ref results, rootData.vessel);

            if (!seedManager.WaitForCompletion(30000))
            {
                Debug.LogError("KerbalWindTunnel: Seed data timed out!");
                return;
            }
            seedManager.Dispose();

            GenerateLevel(conditions, manager, ref results, rootData.vessel);

            if (rootData.storeState.manager.Cancelled)
                return;
            rootData.storeState.StoreResult(results);
        }

        private static void ContinueInBackground(object obj)
        {
            object[] inObj = (object[])obj;
            GenData rootData = (GenData)inObj[0];
            CalculationManager.State[,] results = (CalculationManager.State[,])inObj[1];
            EnvelopePoint[,] basisData = inObj.Length > 2 ? (EnvelopePoint[,])inObj[2] : null;
            Conditions basisConditions = inObj.Length > 3 ? (Conditions)inObj[3] : new Conditions();
            CalculationManager manager = rootData.storeState.manager;
            GenerateLevel(rootData.conditions, manager, ref results, rootData.vessel, basisConditions, basisData);
            if (rootData.storeState.manager.Cancelled)
                return;
            rootData.storeState.StoreResult(results);
        }

        private static void GenerateLevel(Conditions conditions, CalculationManager manager, ref CalculationManager.State[,] results, AeroPredictor vessel, Conditions basisConditions = new Conditions(), EnvelopePoint[,] resultPoints = null)
        {
            float[,] AoAs_guess = null, maxAs_guess = null, pitchIs_guess = null;
            if (resultPoints != null)
            {
                AoAs_guess = resultPoints.SelectToArray(pt => pt.AoA_level);
                maxAs_guess = resultPoints.SelectToArray(pt => pt.AoA_max);
                pitchIs_guess = resultPoints.SelectToArray(pt => pt.pitchInput);
            }
            else if (results != null)
            {
                AoAs_guess = results.SelectToArray(pt => ((EnvelopePoint)pt.Result).AoA_level);
                maxAs_guess = results.SelectToArray(pt => ((EnvelopePoint)pt.Result).AoA_max);
                pitchIs_guess = results.SelectToArray(pt => ((EnvelopePoint)pt.Result).pitchInput);
                resultPoints = results.SelectToArray(pt => (EnvelopePoint)pt.Result);
            }
            int numPtsX = (int)Math.Ceiling((conditions.upperBoundSpeed - conditions.lowerBoundSpeed) / conditions.stepSpeed);
            int numPtsY = (int)Math.Ceiling((conditions.upperBoundAltitude - conditions.lowerBoundAltitude) / conditions.stepAltitude);
            float trueStepX = (conditions.upperBoundSpeed - conditions.lowerBoundSpeed) / numPtsX;
            float trueStepY = (conditions.upperBoundAltitude - conditions.lowerBoundAltitude) / numPtsY;
            results = new CalculationManager.State[numPtsX + 1, numPtsY + 1];
            
            for (int j = 0; j <= numPtsY; j++)
            {
                for (int i = 0; i <= numPtsX; i++)
                {
                    if (manager.Cancelled)
                        return;
                    float x = (float)i / numPtsX;
                    float y = (float)j / numPtsY;
                    float aoa_guess = AoAs_guess != null ? AoAs_guess.Lerp2(x, y) : float.NaN;
                    float maxA_guess = maxAs_guess != null ? maxAs_guess.Lerp2(x, y) : float.NaN;
                    float pi_guess = pitchIs_guess != null ? pitchIs_guess.Lerp2(x, y) : float.NaN;
                    float speed = conditions.lowerBoundSpeed + trueStepX * i;
                    float altitude = conditions.lowerBoundAltitude + trueStepY * j;
                    GenData genData = new GenData(vessel, conditions, speed, altitude, manager, aoa_guess, maxA_guess, pi_guess);
                    results[i, j] = genData.storeState;
                    if (!basisConditions.Equals(Conditions.Blank) && !basisConditions.Equals(new Conditions()) &&
                        basisConditions.Contains(speed, altitude, out int ii, out int jj))
                    {
                        results[i, j].StoreResult(resultPoints[ii, jj]);
                    }
                    else
                        ThreadPool.QueueUserWorkItem(GenerateSurfPoint, genData);
                }
            }
        }

        private static void GenerateSurfPoint(object obj)
        {
            GenData data = (GenData)obj;
            if (data.storeState.manager.Cancelled)
                return;
            //Debug.Log("Starting point: " + data.altitude + "/" + data.speed);
            EnvelopePoint result = new EnvelopePoint(data.vessel, data.conditions.body, data.altitude, data.speed, data.AoA_guess, data.maxA_guess, data.pitchI_guess);
            //Debug.Log("Point solved: " + data.altitude + "/" + data.speed);

            data.storeState.StoreResult(result);
        }

        public override void OnAxesChanged(AeroPredictor vessel, float xMin, float xMax, float yMin, float yMax, float zMin, float zMax)
        {
            const int numPtsX = 125, numPtsY = 125;
            float stepX = (xMax - xMin) / numPtsX, stepY = (yMax - yMin) / numPtsY;
            Calculate(vessel, currentConditions.body, xMin, xMax, stepX, yMin, yMax, stepY);
        }

        #endregion EnvelopeSurf

        private struct GenData
        {
            public readonly Conditions conditions;
            public readonly AeroPredictor vessel;
            public readonly CalculationManager.State storeState;
            public readonly float speed;
            public readonly float altitude;
            public readonly float AoA_guess;
            public readonly float maxA_guess;
            public readonly float pitchI_guess;

            public GenData(AeroPredictor vessel, Conditions conditions, float speed, float altitude, CalculationManager manager, float AoA_guess = float.NaN, float maxA_guess = float.NaN, float pitchI_guess = float.NaN)
            {
                this.vessel = vessel;
                this.conditions = conditions;
                this.speed = speed;
                this.altitude = altitude;
                this.storeState = manager.GetStateToken();
                this.AoA_guess = AoA_guess;
                this.maxA_guess = maxA_guess;
                this.pitchI_guess = pitchI_guess;
            }
        }
        public struct EnvelopePoint
        {
            public readonly float AoA_level;
            public readonly float Thrust_excess;
            public readonly float Accel_excess;
            public readonly float Lift_max;
            public readonly float AoA_max;
            public readonly float Thrust_available;
            public readonly float altitude;
            public readonly float speed;
            public readonly float LDRatio;
            public readonly Vector3 force;
            public readonly Vector3 aeroforce;
            public readonly float mach;
            public readonly float dynamicPressure;
            public readonly float dLift;
            public readonly float drag;
            public readonly float pitchInput;
            public readonly float fuelBurnRate;
            //public readonly float stabilityRange;
            //public readonly float stabilityScore;
            //public readonly float stabilityDerivative;

            public EnvelopePoint(AeroPredictor vessel, CelestialBody body, float altitude, float speed, float AoA_guess = float.NaN, float maxA_guess = float.NaN, float pitchI_guess = float.NaN)
            {
                this.altitude = altitude;
                this.speed = speed;
                AeroPredictor.Conditions conditions = new AeroPredictor.Conditions(body, speed, altitude);
                float gravParameter, radius;
                lock (body)
                {
                    gravParameter = (float)body.gravParameter;
                    radius = (float)body.Radius;
                }
                this.mach = conditions.mach;
                this.dynamicPressure = 0.0005f * conditions.atmDensity * speed * speed;
                float weight = (vessel.Mass * gravParameter / ((radius + altitude) * (radius + altitude))) - (vessel.Mass * speed * speed / (radius + altitude));
                Vector3 thrustForce = vessel.GetThrustForce(conditions);
                fuelBurnRate = vessel.GetFuelBurnRate(conditions);
                //AoA_max = vessel.GetMaxAoA(conditions, out Lift_max, maxA_guess);
                if (float.IsNaN(maxA_guess))
                    AoA_max = vessel.GetMaxAoA(conditions, out Lift_max, maxA_guess);
                else
                {
                    AoA_max = maxA_guess;
                    Lift_max = AeroPredictor.GetLiftForceMagnitude(vessel.GetAeroForce(conditions, AoA_max, 1) + thrustForce, AoA_max);
                }

                AoA_level = vessel.GetAoA(conditions, weight, guess: AoA_guess, pitchInputGuess: 0, lockPitchInput: true);
                if (AoA_level < AoA_max)
                    pitchInput = vessel.GetPitchInput(conditions, AoA_level, guess: pitchI_guess);
                else
                    pitchInput = 1;

                Thrust_available = thrustForce.magnitude;

                //vessel.GetAeroCombined(conditions, AoA_level, pitchInput, out force, out Vector3 torque);
                force = vessel.GetAeroForce(conditions, AoA_level, pitchInput);
                aeroforce = AeroPredictor.ToFlightFrame(force, AoA_level); //vessel.GetLiftForce(body, speed, altitude, AoA_level, mach, atmDensity);
                drag = -aeroforce.z;
                float lift = aeroforce.y;
                Thrust_excess = -drag - AeroPredictor.GetDragForceMagnitude(thrustForce, AoA_level);
                if (weight > Lift_max)// AoA_level >= AoA_max)
                {
                    Thrust_excess = Lift_max - weight;
                    AoA_level = AoA_max;
                }
                Accel_excess = (Thrust_excess / vessel.Mass / WindTunnelWindow.gAccel);
                LDRatio = Mathf.Abs(lift / drag);
                dLift = (vessel.GetLiftForceMagnitude(conditions, AoA_level + WindTunnelWindow.AoAdelta, pitchInput) - lift)
                    / (WindTunnelWindow.AoAdelta * Mathf.Rad2Deg);
                //stabilityDerivative = (vessel.GetAeroTorque(conditions, AoA_level + WindTunnelWindow.AoAdelta, pitchInput).x - torque.x)
                //    / (WindTunnelWindow.AoAdelta * Mathf.Rad2Deg);
                //GetStabilityValues(vessel, conditions, AoA_level, out stabilityRange, out stabilityScore);
            }

            private static void GetStabilityValues(AeroPredictor vessel, AeroPredictor.Conditions conditions, float AoA_centre, out float stabilityRange, out float stabilityScore)
            {
                const int step = 5;
                const int range = 90;
                const int alphaSteps = range / step;
                float[] torques = new float[2 * alphaSteps + 1];
                float[] aoas = new float[2 * alphaSteps + 1];
                int start, end;
                for (int i = 0; i <= 2 * alphaSteps; i++)
                {
                    aoas[i] = (i - alphaSteps) * step * Mathf.Deg2Rad;
                    torques[i] = vessel.GetAeroTorque(conditions, aoas[i], 0).x;
                }
                int eq = 0 + alphaSteps;
                int dir = (int)Mathf.Sign(torques[eq]);
                if (dir == 0)
                {
                    start = eq - 1;
                    end = eq + 1;
                }
                else
                {
                    while (eq > 0 && eq < 2 * alphaSteps)
                    {
                        eq += dir;
                        if (Mathf.Sign(torques[eq]) != dir)
                            break;
                    }
                    if (eq == 0 || eq == 2 * alphaSteps)
                    {
                        stabilityRange = 0;
                        stabilityScore = 0;
                        return;
                    }
                    if (dir < 0)
                    {
                        start = eq;
                        end = eq + 1;
                    }
                    else
                    {
                        start = eq - 1;
                        end = eq;
                    }
                }
                while (torques[start] > 0 && start > 0)
                    start -= 1;
                while (torques[end] < 0 && end < 2 * alphaSteps - 1)
                    end += 1;
                float min = (Mathf.InverseLerp(torques[start], torques[start + 1], 0) + start) * step;
                float max = (-Mathf.InverseLerp(torques[end], torques[end - 1], 0) + end) * step;
                stabilityRange = max - min;
                stabilityScore = 0;
                for (int i = start; i < end; i++)
                {
                    stabilityScore += (torques[i] + torques[i + 1]) / 2 * step;
                }
            }

            public override string ToString()
            {
                return String.Format("Altitude:\t{0:N0}m\n" + "Speed:\t{1:N0}m/s\n" + "Mach:\t{9:N2}\n" + "Level Flight AoA:\t{2:N2}°\n" +
                        "Excess Thrust:\t{3:N0}kN\n" + "Excess Acceleration:\t{4:N2}g\n" + "Max Lift Force:\t{5:N0}kN\n" +
                        "Max Lift AoA:\t{6:N2}°\n" + "Lift/Drag Ratio:\t{8:N2}\n" + "Available Thrust:\t{7:N0}kN",
                        altitude, speed, AoA_level * Mathf.Rad2Deg,
                        Thrust_excess, Accel_excess, Lift_max,
                        AoA_max * Mathf.Rad2Deg, Thrust_available, LDRatio,
                        mach);
            }
        }

        public struct Conditions : IEquatable<Conditions>
        {
            public readonly CelestialBody body;
            public readonly float lowerBoundSpeed;
            public readonly float upperBoundSpeed;
            public readonly float stepSpeed;
            public readonly float lowerBoundAltitude;
            public readonly float upperBoundAltitude;
            public readonly float stepAltitude;

            public static readonly Conditions Blank = new Conditions(null, 0, 0, 0f, 0, 0, 0f);

            public Conditions(CelestialBody body, float lowerBoundSpeed, float upperBoundSpeed, float stepSpeed, float lowerBoundAltitude, float upperBoundAltitude, float stepAltitude)
            {
                this.body = body;
                if (body != null && lowerBoundAltitude > body.atmosphereDepth)
                    lowerBoundAltitude = upperBoundAltitude = (float)body.atmosphereDepth;
                if (body != null && upperBoundAltitude > body.atmosphereDepth)
                    upperBoundAltitude = (float)body.atmosphereDepth;
                this.lowerBoundSpeed = lowerBoundSpeed;
                this.upperBoundSpeed = upperBoundSpeed;
                this.stepSpeed = stepSpeed;
                this.lowerBoundAltitude = lowerBoundAltitude;
                this.upperBoundAltitude = upperBoundAltitude;
                this.stepAltitude = stepAltitude;
            }
            public Conditions(CelestialBody body, float lowerBoundSpeed, float upperBoundSpeed, int speedPts, float lowerBoundAltitude, float upperBoundAltitude, int altitudePts) :
                this(body, lowerBoundSpeed, upperBoundSpeed, (upperBoundSpeed - lowerBoundSpeed) / (speedPts - 1), lowerBoundAltitude, upperBoundAltitude, (upperBoundAltitude - lowerBoundAltitude) / (altitudePts - 1))
            { }

            public Conditions Modify(CelestialBody body = null, float lowerBoundSpeed = float.NaN, float upperBoundSpeed = float.NaN, float stepSpeed = float.NaN, float lowerBoundAltitude = float.NaN, float upperBoundAltitude = float.NaN, float stepAltitude = float.NaN)
                => Conditions.Modify(this, body, lowerBoundSpeed, upperBoundSpeed, stepSpeed, lowerBoundAltitude, upperBoundAltitude, stepAltitude);
            public static Conditions Modify(Conditions conditions, CelestialBody body = null, float lowerBoundSpeed = float.NaN, float upperBoundSpeed = float.NaN, float stepSpeed = float.NaN, float lowerBoundAltitude = float.NaN, float upperBoundAltitude = float.NaN, float stepAltitude = float.NaN)
            {
                if (body == null) body = conditions.body;
                if (float.IsNaN(lowerBoundSpeed)) lowerBoundSpeed = conditions.lowerBoundSpeed;
                if (float.IsNaN(upperBoundSpeed)) upperBoundSpeed = conditions.upperBoundSpeed;
                if (float.IsNaN(stepSpeed)) stepSpeed = conditions.stepSpeed;
                if (float.IsNaN(lowerBoundAltitude)) lowerBoundAltitude = conditions.lowerBoundAltitude;
                if (float.IsNaN(upperBoundAltitude)) upperBoundAltitude = conditions.upperBoundAltitude;
                if (float.IsNaN(stepAltitude)) stepAltitude = conditions.stepAltitude;
                return new Conditions(body, lowerBoundSpeed, upperBoundSpeed, stepSpeed, lowerBoundAltitude, upperBoundAltitude, stepAltitude);
            }

            public bool Contains(Conditions conditions)
            {
                return this.lowerBoundSpeed <= conditions.lowerBoundSpeed &&
                    this.upperBoundSpeed >= conditions.upperBoundSpeed &&
                    this.lowerBoundAltitude <= conditions.lowerBoundAltitude &&
                    this.upperBoundAltitude >= conditions.upperBoundAltitude;
            }
            public bool Contains(float speed, float altitude)
            {
                return lowerBoundSpeed <= speed && speed <= upperBoundSpeed &&
                    lowerBoundAltitude <= altitude && altitude <= upperBoundAltitude &&
                    (speed - lowerBoundSpeed) % stepSpeed == 0 && (altitude - lowerBoundAltitude) % stepAltitude == 0;
            }
            public bool Contains(float speed, float altitude, out int x, out int y)
            {
                bool result = Contains(speed, altitude);
                if (result)
                {
                    x = (int)((speed - lowerBoundSpeed) / stepSpeed);
                    y = (int)((altitude - lowerBoundAltitude) / stepAltitude);
                }
                else
                {
                    x = -1; y = -1;
                }
                return result;
            }

            public override bool Equals(object obj)
            {
                if (obj == null)
                    return false;
                if (obj.GetType() != typeof(Conditions))
                    return false;
                Conditions conditions = (Conditions)obj;
                return this.Equals(conditions);
            }

            public bool Equals(Conditions conditions)
            {
                return this.body == conditions.body &&
                    this.lowerBoundSpeed == conditions.lowerBoundSpeed &&
                    this.upperBoundSpeed == conditions.upperBoundSpeed &&
                    this.lowerBoundAltitude == conditions.lowerBoundAltitude &&
                    this.upperBoundAltitude == conditions.upperBoundAltitude;
            }

            public override int GetHashCode()
            {
                return Extensions.HashCode.Of(this.body).And(this.lowerBoundSpeed).And(this.upperBoundSpeed).And(this.lowerBoundAltitude).And(this.upperBoundAltitude);
            }
        }
    }
}
