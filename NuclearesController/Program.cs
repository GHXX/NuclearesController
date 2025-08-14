using NuclearesController.Controllers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NuclearesController;

internal class Program {
    private const int PORT = 8785;
    private static readonly Uri requestUrl = new($"http://localhost:{PORT}");
    public const ConsoleColor defaultForegroundColor = ConsoleColor.Gray;
    public static ControlMode currOpMode;
    public static ControlMode lastOpMode;


    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(2);

    private static readonly HttpClient hc = new HttpClient() { BaseAddress = requestUrl, Timeout = requestTimeout };
    private static readonly object logObj = new();

    public static void Print(string msg, ConsoleColor? color = null) {
        lock (logObj) {
            if (color != null)
                Console.ForegroundColor = color.Value;
            var lines = msg.Split('\n', StringSplitOptions.None);
            foreach (var line in lines) {
                Console.Write(line);
                var (left, top) = Console.GetCursorPosition();
                Console.Write(new string(' ', Console.BufferWidth - left));
                var (left2, top2) = Console.GetCursorPosition();
                if (left2 != Console.BufferWidth - 1) {
                    Console.Write("\n");
                } else {
                    //Console.WriteLine("test");
                }
            }
            Console.ForegroundColor = defaultForegroundColor;
        }
    }

    private static readonly ConcurrentDictionary<string, string> rawVarCache = [];
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
    public static ConcurrentDictionary<string, object> varCache = [];
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

    public static int currentTimestamp = 0;
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
        try { await hc.GetStringAsync("?variable=CORE_TEMP"); } catch { Console.WriteLine("Waiting for webserver to be online..."); await Task.Delay(5000); goto retry; }
    }

    private static async Task Main() {
        var c = new CultureInfo("en-US");
        c.NumberFormat.NumberGroupSeparator = " ";
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentCulture = c;
        Console.OutputEncoding = Encoding.Default;

        BaseController[] modules = typeof(Program).Assembly.GetTypes()
            .Where(x => x.IsAssignableTo(typeof(BaseController)) && x != typeof(BaseController))
            .Select(x => (BaseController)Activator.CreateInstance(x)!).Where(x => x != null)
            .ToArray();
    restart:
        try {

            Print("Starting controller...");
            Console.Title = "Nucleares Controller";
            await WaitForWebserverAvailableAsync();
            Console.Clear();

            var variablesToSet = new Dictionary<string, string>();


            async Task<Dictionary<string, float>> GetDeltaPrecursorDictAsync() {
                var rv = new ConcurrentDictionary<string, float>();
                await Parallel.ForEachAsync(deltaVariablesToObserve, async (x, ctok) => rv[x] = await GetVariableAsync<float>(x));
                return rv.ToDictionary();
            }

            await Task.WhenAll(modules.Select(x => x.ReInitAsync()));
            var deltaHandler = new DeltaDictHelper<float>(await GetDeltaPrecursorDictAsync());

            //var energyToCoreTempPid = new PID(0.00005, 0.0001, 0.02, targetCoreTemp, false, (170, 450));
            //var coreTempToRodsPid = new PID(0.01, 0.01, 0, rodStartPercentage, true, (0, 100));
            currOpMode = ControlMode.Shutdown;
            lastOpMode = currOpMode;


            while (true) {
                await WaitForNextTimeStepAsync();
                prefetchCache = [.. varCache.Keys];
                rawVarCache.Clear();
                varCache.Clear();
                variablesToSet.Clear();
                await HttpPrefetchAsync();
                var coreTempCurrent = await GetVariableAsync<float>("CORE_TEMP");
                var opModeSelStr = await GetVariableAsync<string>("CORE_OPERATION_MODE");

                lastOpMode = currOpMode;
                if (opModeSelStr == "SHUTDOWN")
                    currOpMode = ControlMode.Shutdown;
                else
                    currOpMode = coreTempCurrent > 100 ? ControlMode.Normal : ControlMode.Startup;

                if (lastOpMode != currOpMode) {
                    await Task.WhenAll(modules.Select(x => x.NotifyControlOpModeChangedAsync(currOpMode, lastOpMode)));
                }

                await Task.WhenAll(modules.Select(x => x.NotifyNewTimestampAsync()));

                var deltaDict = deltaHandler.Tick(await GetDeltaPrecursorDictAsync());

                Console.SetCursorPosition(0, 0);
                Print("\nCool reactor controller :)))))\n");
                foreach (var m in modules) {
                    Print($"========== {m.GetType().Name} is active ==========");
                    foreach (var msg in m.GetMessagesToPrint()) {
                        Print(msg.Item1, msg.Item2);
                    }
                    Print("\n");
                    foreach (var kv in m.variablesToSet) {
                        variablesToSet.Add(kv.Key, kv.Value);
                    }
                }

                Print($"========== Extra Info ==========");
                Print($"Additional variables:\n" + Util.DictToString(observedVariables.ToDictionary(x => x, x => GetVariableAsync<float>(x).Result)));
                Print("Observed variable deltas:\n" + Util.DictToString(deltaDict.ToDictionary(x => "\u0394" + x.Key, x => x.Value)));
                /*Console.WriteLine($"Ordered secondary pumpspeeds A/B/C: {string.Join('/', Enumerable.Range(0, 3).Select(i => variablesToSet[$"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED"]))}" + "      ");
                Console.WriteLine($"Ordered condenser speed: {variablesToSet["CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"]}" + padright);*/
                //Console.WriteLine("Excel paste string:\n" + variablesToPaste.Select(x => GetVariableAsync<float>(x).Result.ToString().Replace(",", "").Replace('.', ',') + " ").JoinByDelim(" ") + padright);

                var (cursorPosLeft, cursorPosTop) = Console.GetCursorPosition();
                var cursorPosIdx = cursorPosTop * Console.BufferWidth + cursorPosLeft;
                int padLen = Console.BufferWidth * Console.WindowHeight - cursorPosIdx;
                if (padLen > 0)
                    Console.Write(new string(' ', padLen));
                Console.SetCursorPosition(0, 0);

                foreach (var (k, v) in variablesToSet) {
                    await SetVariableAsync(k, v);
                }
            }
        } catch (Exception ex) {
            Console.WriteLine(ex);
            await Task.Delay(10_000);
            goto restart;
        }
    }
}
