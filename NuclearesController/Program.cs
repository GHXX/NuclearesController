using System.Globalization;
using System.Text;

namespace NuclearesController;

internal class Program
{
    private const int PORT = 8785;
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(2);
    private static HttpClient hc = new HttpClient() { BaseAddress = new($"http://localhost:{PORT}/") };
    private static readonly object logObj = new();
    public static void Log(string msg, LogLevel level)
    {
        lock (logObj)
        {
            var fg = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
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

    public static async Task<string> GetVariableRawAsync(string varname) => await hc.GetStringAsync($"?variable={varname}", new CancellationTokenSource(requestTimeout).Token);
    public static Dictionary<string, object> varCache = [];
    public static async Task<T> GetVariableAsync<T>(string varname) where T : IParsable<T>
    {
        if (!varname.Equals("TIME_STAMP", StringComparison.InvariantCultureIgnoreCase) && varCache.TryGetValue(varname, out var rv))
            return (T)rv;

        var rv2 = T.Parse((await GetVariableRawAsync(varname)).Replace(",", "."), null);
        varCache[varname] = rv2;
        return rv2;
    }

    public static async Task SetVariableAsync(string varname, object value) => await hc.PostAsync($"?variable={varname}&value={value}", null);

    private static int currentTimestamp = 0;
    internal static readonly string[] generatorVariables = [.. Enumerable.Range(0, 3).Select(x => $"GENERATOR_{x}_KW")];
    internal static readonly string[] secLevelVariables = [.. Enumerable.Range(0, 3).Select(x => $"COOLANT_SEC_{x}_VOLUME")];
    internal static readonly string[] primaryPumpSpeedVariables = [.. Enumerable.Range(0, 3).Select(x => $"COOLANT_CORE_CIRCULATION_PUMP_{x}_SPEED")];

    internal static readonly string[] observedVariables = ["CORE_TEMP","RODS_POS_ORDERED", "AUX_IODINE_GENERATION", "AUX_IODINE_CUMULATIVE", "AUX_XENON_GENERATION",
        "AUX_XENON_CUMULATIVE", "AUX_FACTOR", ..generatorVariables,..secLevelVariables];
    internal static readonly string[] deltaVariablesToObserve = ["CORE_TEMP", "RODS_POS_ORDERED", "AUX_IODINE_GENERATION", "AUX_FACTOR", .. generatorVariables, .. secLevelVariables];
    internal static readonly string[] variablesToPaste = ["AUX_FACTOR", .. generatorVariables, .. primaryPumpSpeedVariables];

    private static async Task WaitForNextTimeStepAsync()
    {
        while (true)
        {
            var nextTs = await GetVariableAsync<int>("TIME_STAMP"); // number representing minutes since start of game, ingame time
            if (nextTs != currentTimestamp) { currentTimestamp = nextTs; break; }
            await Task.Delay(500);
        }
    }

    private static async Task WaitForWebserverAvailableAsync()
    {
    retry: // retry marker for from inside catch block
        try { await hc.GetStringAsync("?variable=CORE_TEMP"); }
        catch { Console.WriteLine("Waiting for webserver to be online..."); goto retry; }
    }

    private static async Task Main(string[] args)
    {
        var c = new CultureInfo("en-US");
        c.NumberFormat.NumberGroupSeparator = " ";
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentCulture = c;
        var origConsoleColor = Console.ForegroundColor;
        Console.OutputEncoding = Encoding.Default;

    restart:
        try
        {
            Console.WriteLine("Starting controller...");
            Console.Title = "Nucleares Controller";

            const float absorptionCapacity = 10000;
            const float targetPowerOutput = (absorptionCapacity / 2) * (0.75f);

            await WaitForWebserverAvailableAsync();
            Console.Clear();



            var variablesToSet = new Dictionary<string, string>();
            void SetVariable(string name, object value) => variablesToSet[name] = value.ToString()!;

            string padright = new string(' ', 32);

            double targetCoreTemp = await GetVariableAsync<float>("CORE_TEMP");
            const float desiredCoreTemp = 360f;
            double rodStartPercentage = await GetVariableAsync<float>("RODS_POS_ACTUAL");
            var coreTempToRodsPid = new PID(0.5, 0.075, 0.5, rodStartPercentage, true, (0, 100));

            const float targetSecondaryLevel = 3500f;
            var secondaryLevelPids = Enumerable.Range(0, 3).Select(async i => new PID(0.0005, 0.005, 0, await GetVariableAsync<float>($"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED"), false, (0, 100))).Select(x => x.Result).ToArray();

            const float targetSteamGenTemp = 250f;
            var primaryLevelPids = Enumerable.Range(0, 3).Select(async i => new PID(0.0005, 0.001, 0.05, await GetVariableAsync<float>($"COOLANT_CORE_CIRCULATION_PUMP_{i}_ORDERED_SPEED"), false, (0, 100))).Select(x => x.Result).ToArray();

            const float desiredCondenserTemp = 65f;
            var condenserPumpSpeedPid = new PID(0.00005, 0.05, 0.01, await GetVariableAsync<float>("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"), true, (0, 100));

            const float desiredCondenserLevelMin = 26_000f;
            const float desiredCondenserLevelMax = 29_000f;

            async Task<Dictionary<string, float>> GetDeltaPrecursorDictAsync()
            {
                var rv = new Dictionary<string, float>();
                foreach (var dv in deltaVariablesToObserve)
                    rv[dv] = await GetVariableAsync<float>(dv);
                return rv;
            }

            var deltaHandler = new DeltaDictHelper<float>(await GetDeltaPrecursorDictAsync());

            //var energyToCoreTempPid = new PID(0.00005, 0.0001, 0.02, targetCoreTemp, false, (170, 450));
            //var coreTempToRodsPid = new PID(0.01, 0.01, 0, rodStartPercentage, true, (0, 100));
            OPMode currOpMode = OPMode.Shutdown;
            OPMode lastOpMode = currOpMode;
            while (true)
            {
                await WaitForNextTimeStepAsync();
                varCache.Clear();
                variablesToSet.Clear();
                var coreTempCurrent = await GetVariableAsync<float>("CORE_TEMP");
                var reactivityzerobased = await GetVariableAsync<float>("CORE_STATE_CRITICALITY");
                var opModeSelStr = await GetVariableAsync<string>("OPERATION_MODE");
                if (opModeSelStr == "SHUTDOWN")
                    currOpMode = OPMode.Shutdown;
                else
                    currOpMode = coreTempCurrent > 100 ? OPMode.Normal : OPMode.Startup;
                var opModeIsShutdown = currOpMode == OPMode.Shutdown;

                if (lastOpMode != currOpMode)
                {
                    lastOpMode = currOpMode;
                    coreTempToRodsPid.Reset(await GetVariableAsync<float>("RODS_POS_ACTUAL"));
                }

                float actualDesiredCoreTemp;
                bool actualDesiredCoreTempReactivityLimited;
                if (Math.Abs(reactivityzerobased) < 1)
                {
                    (actualDesiredCoreTemp, actualDesiredCoreTempReactivityLimited) = (desiredCoreTemp, false);
                }
                else
                {
                    var newTemp = desiredCoreTemp - 500 * Math.Sign(reactivityzerobased);
                    (actualDesiredCoreTemp, actualDesiredCoreTempReactivityLimited) = (newTemp, true);
                }
                SetVariable("RODS_POS_ORDERED", coreTempToRodsPid.Step(currentTimestamp, actualDesiredCoreTemp, coreTempCurrent, reactivityzerobased));
                for (int i = 0; i < 3; i++)
                {
                    var currSecCoolant = await GetVariableAsync<float>($"COOLANT_SEC_{i}_VOLUME");
                    SetVariable($"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED", secondaryLevelPids[i].Step(currentTimestamp, targetSecondaryLevel, currSecCoolant).ToString("N2"));

                    //    if (i == 1)
                    //    {
                    //        var actual = await GetVariableAsync<int>($"COOLANT_CORE_CIRCULATION_PUMP_{i}_ORDERED_SPEED");
                    //        var isOn = Math.Abs(await GetVariableAsync<int>($"COOLANT_CORE_CIRCULATION_PUMP_{i}_SPEED") - actual) < 5;
                    //        var steamGenTemp = await GetVariableAsync<float>($"COOLANT_SEC_{i}_TEMPERATURE");
                    //        if (isOn)
                    //            SetVariable($"COOLANT_PRIM_CIRCULATION_PUMP_{i}_ORDERED_SPEED", primaryLevelPids[i].Step(currentTimestamp, targetSteamGenTemp, steamGenTemp).ToString("N2"));
                    //        else
                    //            primaryLevelPids[i].Reset(actual);
                    //    }
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


                if (currOpMode is OPMode.Shutdown or OPMode.Startup)
                {
                    variablesToSet.Remove("RODS_POS_ORDERED");
                }

                var deltaDict = deltaHandler.Tick(await GetDeltaPrecursorDictAsync());

                foreach (var (k, v) in variablesToSet)
                {
                    await SetVariableAsync(k, v);
                }

                Console.SetCursorPosition(0, 0);
                Console.WriteLine("");
                Console.WriteLine("Cool reactor controller :)))))\n");
                Console.WriteLine($"OPERATION MODE: {opModeSelStr} --> {currOpMode.ToString().ToUpperInvariant()}          ");
                if (variablesToSet.ContainsKey("RODS_POS_ORDERED"))
                {
                    Console.WriteLine($"New rod level: {variablesToSet["RODS_POS_ORDERED"]}" + padright);
                    if (actualDesiredCoreTempReactivityLimited)
                    {
                        Warn("Large reactivity change detected. Slowing rod movement.");
                    }
                }
                Console.WriteLine($"Ordered secondary pumpspeeds A/B/C: {string.Join('/', Enumerable.Range(0, 3).Select(i => variablesToSet[$"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED"]))}" + "      ");
                Console.WriteLine($"Ordered condenser speed: {variablesToSet["CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"]}" + padright);
                Console.WriteLine("Additional variables:\n" + dictToString(observedVariables.ToDictionary(x => x, x => GetVariableAsync<float>(x).Result)));
                Console.WriteLine(padright + padright + padright);
                var ctReached = Math.Abs(coreTempCurrent - targetCoreTemp) < 1 && Math.Abs(reactivityzerobased) < 0.5;
                Console.ForegroundColor = ctReached ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.WriteLine($"CORE TEMP REACHED? {ctReached} ");
                Console.ForegroundColor = origConsoleColor;
                Console.WriteLine("Observed variable deltas:\n" + dictToString(deltaDict.ToDictionary(x => "\u0394" + x.Key, x => x.Value)));
                Console.WriteLine(padright + padright + padright);
                Console.WriteLine("Excel paste string:\n" + variablesToPaste.Select(x => GetVariableAsync<float>(x).Result.ToString().Replace(",", "").Replace('.', ',') + " ").JoinByDelim(" "));
                Console.WriteLine(padright + padright + padright);
                Console.WriteLine(padright + padright + padright);
                Console.WriteLine(padright + padright + padright);
                Console.SetCursorPosition(0, 0);

                string dictToString(Dictionary<string, float> d) => d.Select(x => $"{x.Key.PadRight(d.Max(x => x.Key.Length) + 1)} {x.Value,11:N5}").JoinByDelim("\n");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            await Task.Delay(10_000);
            goto restart;
        }
    }
}
