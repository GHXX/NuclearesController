using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NuclearesController;

internal class Program {
    private const int PORT = 8785;
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient hc = new HttpClient() { BaseAddress = new($"http://localhost:{PORT}"), Timeout = requestTimeout };
    private static readonly object logObj = new();

    private const int factorModelNeededObs = 10;
    private const float desiredCoreTemp = 360.5f;
    private const double maxTargetReactivity = 1;
    private const double reactivitySlopeLengthDegrees = 25;
    public static void Log(string msg, LogLevel level) {
        lock (logObj) {
            var fg = Console.ForegroundColor;
            Console.ForegroundColor = level switch {
                LogLevel.Info => ConsoleColor.White,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => throw new NotImplementedException(),
            };
            Console.WriteLine(msg);
            Console.ForegroundColor = fg;
        }
    }
    public static void Info(string msg) => Log(msg, LogLevel.Info);
    public static void Warn(string msg) => Log(msg, LogLevel.Warning);
    public static void Error(string msg) => Log(msg, LogLevel.Error);

    private static Dictionary<string, string> rawVarCache = [];
    public static async Task<string> GetVariableRawAsync(string varname) {
    retry:
        try {
            if (!varname.Equals("TIME_STAMP", StringComparison.InvariantCultureIgnoreCase) && rawVarCache.TryGetValue(varname, out var rv)) return rv;
            var res = await hc.GetStringAsync($"?variable={varname}", new CancellationTokenSource(requestTimeout).Token);
            rawVarCache[varname] = res;
            return res;
        } catch (TaskCanceledException) {
            goto retry;
        }
    }

    public static ConcurrentBag<string> prefetchCache = [];
    public static Dictionary<string, object> varCache = [];
    public static async Task<T> GetVariableAsync<T>(string varname) where T : IParsable<T> {
        if (!varname.Equals("TIME_STAMP", StringComparison.InvariantCultureIgnoreCase) && varCache.TryGetValue(varname, out var rv))
            return (T)rv;

        var rv2 = T.Parse((await GetVariableRawAsync(varname)).Replace(",", "."), null);
        varCache[varname] = rv2;
        return rv2;
    }

    public static async Task HttpPrefetchAsync() {
        await Parallel.ForEachAsync(prefetchCache, async (x, ctok) => {
            try {
                await GetVariableAsync<float>(x);
            } catch (Exception) {
            }
        });
    }

    public static async Task SetVariableAsync(string varname, object value) {
        string strVal = (value?.ToString() ?? "null").Replace('.', ',');
    retry:
        try {
            var resp = await hc.PostAsync($"?variable={varname}&value={strVal}", null);
            if (!resp.IsSuccessStatusCode) {
                throw new Exception($"Non success status code for setting variable {varname} to {strVal}");
            }
        } catch (TaskCanceledException) {
            goto retry;
        }
    }

    private static int currentTimestamp = 0;
    internal static readonly string[] generatorVariables = [.. Enumerable.Range(0, 3).Select(x => $"GENERATOR_{x}_KW")];
    internal static readonly string[] secLevelVariables = [.. Enumerable.Range(0, 3).Select(x => $"COOLANT_SEC_{x}_VOLUME")];
    internal static readonly string[] primaryPumpSpeedVariables = [.. Enumerable.Range(0, 3).Select(x => $"COOLANT_CORE_CIRCULATION_PUMP_{x}_SPEED")];

    internal static readonly string[] observedVariables = ["CORE_TEMP", "CORE_IODINE_GENERATION", "CORE_IODINE_CUMULATIVE", "CORE_XENON_GENERATION",
        "CORE_XENON_CUMULATIVE", "CORE_FACTOR", ..generatorVariables,..secLevelVariables];
    internal static readonly string[] deltaVariablesToObserve = ["CORE_TEMP", "CORE_IODINE_GENERATION", "CORE_FACTOR", .. generatorVariables, .. secLevelVariables];
    //internal static readonly string[] variablesToPaste = ["CORE_FACTOR", .. generatorVariables, .. primaryPumpSpeedVariables, "CORE_TEMP"];

    private static async Task WaitForNextTimeStepAsync() {
        while (true) {
            var nextTs = await GetVariableAsync<int>("TIME_STAMP"); // number representing minutes since start of game, ingame time
            if (nextTs != currentTimestamp) { currentTimestamp = nextTs; break; }
            await Task.Delay(500);
        }
    }

    private static async Task WaitForWebserverAvailableAsync() {
    retry: // retry marker for from inside catch block
        try { await hc.GetStringAsync("?variable=CORE_TEMP"); } catch { Console.WriteLine("Waiting for webserver to be online..."); await Task.Delay(1000); goto retry; }
    }

    private static async Task Main(string[] args) {
        var c = new CultureInfo("en-US");
        c.NumberFormat.NumberGroupSeparator = " ";
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentCulture = c;
        var origConsoleColor = Console.ForegroundColor;
        Console.OutputEncoding = Encoding.Default;

    restart:
        try {
            Console.WriteLine("Starting controller...");
            Console.Title = "Nucleares Controller";

            const float absorptionCapacity = 10000;
            const float targetPowerOutput = (absorptionCapacity / 2) * (0.75f);
            var coshCorrectionFactor = maxTargetReactivity / Math.Log(Math.Cosh(maxTargetReactivity));

            await WaitForWebserverAvailableAsync();
            Console.Clear();



            var variablesToSet = new Dictionary<string, string>();
            void SetVariable(string name, object value) => variablesToSet[name] = value.ToString()!;

            string padright = new string(' ', 32);

            double targetCoreTemp = await GetVariableAsync<float>("CORE_TEMP");
            double rodStartPercentage = await GetVariableAsync<float>("RODS_POS_ACTUAL");
            var reactivityToRodsPid = new PID(1.5, 0.1 / 4, 0, rodStartPercentage, true, (0, 100));
            var tempToThermalPid = new PID(0.075, 0.01, 0, 0, false, null);

            string[] coreReactivityRelevantVars = ["RODS_POS_ACTUAL",/*"CORE_TEMP",*/ .. primaryPumpSpeedVariables, "CORE_IODINE_CUMULATIVE", "CORE_XENON_CUMULATIVE"];
            var coreFactorModel = new MlPlantModel(coreReactivityRelevantVars.Length);

            const float targetSecondaryLevel = 35000f;
            var secondaryLevelPids = Enumerable.Range(0, 3).Select(async i => new PID(0.005, 0.00005, 0, await GetVariableAsync<float>($"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED"), false, (0, 100))).Select(x => x.Result).ToArray();

            //const float targetSteamGenTemp = 250f;
            //var primaryLevelPids = Enumerable.Range(0, 3).Select(async i => new PID(0.0005, 0.001, 0.05, await GetVariableAsync<float>($"COOLANT_CORE_CIRCULATION_PUMP_{i}_ORDERED_SPEED"), false, (0, 100))).Select(x => x.Result).ToArray();

            const float desiredCondenserTemp = 28f;
            var condenserPumpSpeedPid = new PID(0.00005, 0.05, 0.01, await GetVariableAsync<float>("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"), true, (0, 100));

            const float desiredCondenserLevelMin = 200_000f;
            const float desiredCondenserLevelMax = 250_000f;

            async Task<Dictionary<string, float>> GetDeltaPrecursorDictAsync() {
                var rv = new ConcurrentDictionary<string, float>();
                await Parallel.ForEachAsync(deltaVariablesToObserve, async (x, ctok) => rv[x] = await GetVariableAsync<float>(x));
                //foreach (var dv in deltaVariablesToObserve)
                //    rv[dv] = await GetVariableAsync<float>(dv);
                return rv.ToDictionary();
            }

            var deltaHandler = new DeltaDictHelper<float>(await GetDeltaPrecursorDictAsync());

            //var energyToCoreTempPid = new PID(0.00005, 0.0001, 0.02, targetCoreTemp, false, (170, 450));
            //var coreTempToRodsPid = new PID(0.01, 0.01, 0, rodStartPercentage, true, (0, 100));
            OPMode currOpMode = OPMode.Shutdown;
            OPMode lastOpMode = currOpMode;

            ControlMode controlMode = ControlMode.PID;
            ControlMode lastControlMode = controlMode;

            double[] reactivityModelX = Array.Empty<double>();
            while (true) {
                await WaitForNextTimeStepAsync();
                prefetchCache = [.. varCache.Keys];
                rawVarCache.Clear();
                varCache.Clear();
                variablesToSet.Clear();
                await HttpPrefetchAsync();
                var coreTempCurrent = await GetVariableAsync<float>("CORE_TEMP");
                var reactivityzerobased = await GetVariableAsync<float>("CORE_STATE_CRITICALITY");
                var coreFactorOld = await GetVariableAsync<float>("CORE_FACTOR");
                var opModeSelStr = await GetVariableAsync<string>("CORE_OPERATION_MODE");
                if (opModeSelStr == "SHUTDOWN")
                    currOpMode = OPMode.Shutdown;
                else
                    currOpMode = coreTempCurrent > 100 ? OPMode.Normal : OPMode.Startup;
                var opModeIsShutdown = currOpMode == OPMode.Shutdown;

                if (coreFactorModel.ObservationCount >= factorModelNeededObs) {
                    controlMode = ControlMode.ML;
                }

                if (lastOpMode != currOpMode) {
                    reactivityToRodsPid.Reset(await GetVariableAsync<float>("RODS_POS_ACTUAL"));
                }
                if (lastControlMode != controlMode) {
                    switch (controlMode) {
                        case ControlMode.PID:
                            reactivityToRodsPid.Reset(await GetVariableAsync<float>("RODS_POS_ACTUAL"));
                            break;
                        case ControlMode.ML:
                            tempToThermalPid.Reset(coreFactorOld);
                            break;
                    }
                }

                lastOpMode = currOpMode;
                lastControlMode = controlMode;

                var estimatedCurrentCoreFactor = 0d;
                var r2_coreFactor = 0d;
                if (reactivityModelX.Length > 0) {
                    estimatedCurrentCoreFactor = coreFactorModel.Evaluate(reactivityModelX);
                }
                reactivityModelX = [.. coreReactivityRelevantVars.Select(x => GetVariableAsync<float>(x).Result)]; // store for next time around
                coreFactorModel.AddObservation(reactivityModelX, coreFactorOld); // train on current X and current core factor
                r2_coreFactor = coreFactorModel.ReFit();

                var coreTempError = coreTempCurrent - desiredCoreTemp;
                var desiredReactivity = Math.Clamp(-coreTempError, -reactivitySlopeLengthDegrees, reactivitySlopeLengthDegrees) / reactivitySlopeLengthDegrees * maxTargetReactivity;
                double? mlEstimatedRodsPos = null;
                switch (controlMode) {
                    case ControlMode.PID:
                        var newRodsPos = reactivityToRodsPid.Step(currentTimestamp, desiredReactivity, reactivityzerobased);
                        SetVariable("RODS_ALL_POS_ORDERED", newRodsPos);
                        break;

                    case ControlMode.ML:
                        var newDesiredThermal = tempToThermalPid.Step(currentTimestamp, desiredCoreTemp, coreTempCurrent); // could add thermal surplus as delta potentially
                        mlEstimatedRodsPos = coreFactorModel.ReverseSolveForX1(newDesiredThermal, reactivityModelX[1..]);
                        SetVariable("RODS_ALL_POS_ORDERED", mlEstimatedRodsPos);
                        break;
                }



                for (int i = 0; i < 0; i++) {
                    var currSecCoolant = await GetVariableAsync<float>($"COOLANT_SEC_{i}_VOLUME");
                    SetVariable($"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED", secondaryLevelPids[i].Step(currentTimestamp, targetSecondaryLevel, currSecCoolant).ToString("N2"));
                }

                var condenserTempCurrent = await GetVariableAsync<float>("CONDENSER_TEMPERATURE");
                var newCondenserSpeed = condenserPumpSpeedPid.Step(currentTimestamp, desiredCondenserTemp, condenserTempCurrent);
                if (currOpMode == OPMode.Normal)
                    newCondenserSpeed = Math.Max(1, newCondenserSpeed);
                SetVariable("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED", newCondenserSpeed.ToString("N2"));

                var condenserLevelCurrent = await GetVariableAsync<float>("CONDENSER_VOLUME");
                if (condenserLevelCurrent < desiredCondenserLevelMin)
                    SetVariable("FREIGHT_PUMP_CONDENSER_ACTIVE", true);
                else if (condenserLevelCurrent > desiredCondenserLevelMax)
                    SetVariable("FREIGHT_PUMP_CONDENSER_ACTIVE", false);


                if (currOpMode is OPMode.Shutdown or OPMode.Startup) {
                    variablesToSet.Remove("RODS_ALL_POS_ORDERED");
                }

                var deltaDict = deltaHandler.Tick(await GetDeltaPrecursorDictAsync());

                foreach (var (k, v) in variablesToSet) {
                    await SetVariableAsync(k, v);
                }

                Console.SetCursorPosition(0, 0);
                Console.WriteLine("");
                Console.WriteLine("Cool reactor controller :)))))\n");
                Console.WriteLine($"OPERATION MODE: {opModeSelStr} --> {currOpMode.ToString().ToUpperInvariant()}          ");
                Console.ForegroundColor = controlMode == ControlMode.ML ? ConsoleColor.Cyan : ConsoleColor.Yellow;
                Console.WriteLine($"CONTROL MODE: {controlMode}            ");
                Console.ForegroundColor = origConsoleColor;
                Console.WriteLine($"Desired/actual reactivity: {desiredReactivity:N3}/{reactivityzerobased:N3}");
                if (variablesToSet.ContainsKey("RODS_ALL_POS_ORDERED")) {
                    Console.WriteLine($"New rod level: {variablesToSet["RODS_ALL_POS_ORDERED"]}" + padright);
                    //if (actualDesiredCoreTempReactivityLimited) {
                    //    Warn("Large reactivity change detected. Slowing rod movement.");
                    //}
                } else {
                    Console.WriteLine();
                }
                    /*Console.WriteLine($"Ordered secondary pumpspeeds A/B/C: {string.Join('/', Enumerable.Range(0, 3).Select(i => variablesToSet[$"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED"]))}" + "      ");
                    Console.WriteLine($"Ordered condenser speed: {variablesToSet["CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"]}" + padright);*/
                    Console.WriteLine($"Additional variables:{padright}\n" + dictToString(observedVariables.ToDictionary(x => x, x => GetVariableAsync<float>(x).Result)));
                Console.WriteLine(padright + padright + padright);
                var ctReached = Math.Abs(coreTempCurrent - desiredCoreTemp) < 1 && Math.Abs(reactivityzerobased) < 0.5;
                Console.ForegroundColor = ctReached ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.WriteLine($"CORE TEMP REACHED? {ctReached} ");
                Console.ForegroundColor = origConsoleColor;
                Console.WriteLine("Observed variable deltas:\n" + dictToString(deltaDict.ToDictionary(x => "\u0394" + x.Key, x => x.Value)));
                Console.WriteLine(padright + padright + padright);
                Console.WriteLine($"ML Factor fit r²: {r2_coreFactor}; Observation count: {coreFactorModel.ObservationCount}/{coreFactorModel.MaxObservationCount}"+padright);
                Console.WriteLine($"ML Factor estimate: {estimatedCurrentCoreFactor}, actual: {coreFactorOld}; Params: {coreFactorModel.KPs.Select(x => x.ToString()).JoinByDelim(" ")}" + padright);
                Console.WriteLine($"ML Ideal rod pos estimate: {(mlEstimatedRodsPos == null ? ($"NONE - Warming Up: {coreFactorModel.ObservationCount}/{factorModelNeededObs}") : ($"{mlEstimatedRodsPos:N2}"))}" + padright);
                Console.WriteLine(padright + padright + padright);
                //Console.WriteLine("Excel paste string:\n" + variablesToPaste.Select(x => GetVariableAsync<float>(x).Result.ToString().Replace(",", "").Replace('.', ',') + " ").JoinByDelim(" ") + padright);
                Console.WriteLine(padright + padright + padright);
                Console.WriteLine(padright + padright + padright);
                Console.WriteLine(padright + padright + padright);
                Console.SetCursorPosition(0, 0);

                string dictToString(Dictionary<string, float> d) => d.Select(x => $"{x.Key.PadRight(d.Max(x => x.Key.Length) + 1)} {x.Value,11:N5}").JoinByDelim("\n");
            }
        } catch (Exception ex) {
            Console.WriteLine(ex);
            await Task.Delay(10_000);
            goto restart;
        }
    }
}
