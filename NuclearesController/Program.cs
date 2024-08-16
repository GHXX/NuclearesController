namespace NuclearesController;

internal class Program
{
    private const int PORT = 8785;
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(2);
    private static HttpClient hc = new HttpClient() { BaseAddress = new($"http://localhost:{PORT}/") };

    public static async Task<string> GetVariableRawAsync(string varname) => await hc.GetStringAsync($"?variable={varname}", new CancellationTokenSource(requestTimeout).Token);
    public static async Task<T> GetVariableAsync<T>(string varname) where T : IParsable<T> => T.Parse(await GetVariableRawAsync(varname), null);

    public static async Task SetVariableAsync(string varname, object value) => await hc.PostAsync($"?variable={varname}&value={value}", null);

    private static int currentTimestamp = 0;

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
            var coreTempToRodsPid = new PID(0.01, 0.01, 0, rodStartPercentage, true, (0, 100));

            const float targetSecondaryLevel = 2800f;
            var secondaryLevelPids = Enumerable.Range(0, 3).Select(async i => new PID(0.0005, 0.01, 0, await GetVariableAsync<float>($"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED"), false, (0, 100))).Select(x => x.Result).ToArray();

            const float desiredCondenserTemp = 65f;
            var condenserPumpSpeedPid = new PID(0.00005, 0.05, 0.01, await GetVariableAsync<float>("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"), true, (0, 100));

            const float desiredCondenserLevelMin = 27_000f;
            const float desiredCondenserLevelMax = 30_000f;

            //var energyToCoreTempPid = new PID(0.00005, 0.0001, 0.02, targetCoreTemp, false, (170, 450));
            //var coreTempToRodsPid = new PID(0.01, 0.01, 0, rodStartPercentage, true, (0, 100));
            while (true)
            {
                await WaitForNextTimeStepAsync();
                variablesToSet.Clear();
                var coreTempCurrent = await GetVariableAsync<float>("CORE_TEMP");
                var reactivityzerobased = await GetVariableAsync<float>("CORE_STATE_CRITICALITY");

                SetVariable("RODS_POS_ORDERED", coreTempToRodsPid.Step(currentTimestamp, desiredCoreTemp, coreTempCurrent, reactivityzerobased));
                for (int i = 0; i < 3; i++)
                {
                    var currSecCoolant = await GetVariableAsync<float>($"COOLANT_SEC_{i}_VOLUME");
                    SetVariable($"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED", secondaryLevelPids[i].Step(currentTimestamp, targetSecondaryLevel, currSecCoolant).ToString("N2"));
                }

                var condenserTempCurrent = await GetVariableAsync<float>("CONDENSER_TEMPERATURE");
                SetVariable("CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED", condenserPumpSpeedPid.Step(currentTimestamp, desiredCondenserTemp, condenserTempCurrent));

                var condenserLevelCurrent = await GetVariableAsync<float>("CONDENSER_VOLUME");
                if (condenserLevelCurrent < desiredCondenserLevelMin)
                    SetVariable("FREIGHT_PUMP_CONDENSER_ACTIVE", true);
                else if (condenserLevelCurrent > desiredCondenserLevelMax)
                    SetVariable("FREIGHT_PUMP_CONDENSER_ACTIVE", false);

                foreach (var (k, v) in variablesToSet)
                {
                    await SetVariableAsync(k, v);
                }


                Console.SetCursorPosition(0, 0);
                Console.WriteLine("Cool reactor controller :)))))");
                Console.WriteLine($"New rod level: {variablesToSet["RODS_POS_ORDERED"]}" + padright);
                Console.WriteLine($"Ordered secondary pumpspeeds A/B/C: {string.Join('/', Enumerable.Range(0, 3).Select(i => variablesToSet[$"COOLANT_SEC_CIRCULATION_PUMP_{i}_ORDERED_SPEED"]))}");
                Console.WriteLine($"Ordered condenser speed: {variablesToSet["CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED"]}" + padright);
                Console.WriteLine(padright + padright + padright);
                Console.SetCursorPosition(0, 0);
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
