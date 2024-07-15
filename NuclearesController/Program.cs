namespace NuclearesController
{
    internal class Program
    {

        const int PORT = 8080;
        static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(2);
        static HttpClient hc = new HttpClient() { BaseAddress = new($"http://localhost:{PORT}/") };
        static async Task<T> GetVariableAsync<T>(string varname) where T : IParsable<T>
            => T.Parse(await hc.GetStringAsync($"?variable={varname}", new CancellationTokenSource(requestTimeout).Token), null);

        static async Task SetVariableAsync(string varname, float value) => await hc.PostAsync($"?variable={varname}&value={value}", null);

        static int currentTimestamp = 0;
        static async Task WaitForNextTimeStepAsync()
        {
            while (true)
            {
                var nextTs = await GetVariableAsync<int>("TIME_STAMP"); // number representing minutes since start of game, ingame time
                if (nextTs != currentTimestamp) { currentTimestamp = nextTs; break; }
                await Task.Delay(500);
            }
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting controller...");
            Console.Title = "Nucleares Controller";

            const float absorptionCapacity = 10000;
            const float targetPowerOutput = (absorptionCapacity / 2) * (0.75f);

        retry: // retry marker for from inside catch block
            try
            {
                await hc.GetStringAsync("?variable=CORE_TEMP");
            }
            catch { Console.WriteLine("Waiting for webserver to be online..."); goto retry; }
            Console.Clear();


            string padright = new string(' ', 32);

            double targetCoreTemp = await GetVariableAsync<float>("CORE_TEMP");
            double rodStartPercentage = await GetVariableAsync<float>("RODS_POS_ACTUAL");

            var energyToCoreTempPid = new PID(0.00005, 0.00001, 0.02, targetCoreTemp, false, (120, 450));
            var coreTempToRodsPid = new PID(0.01, 0.01, 0, rodStartPercentage, true, (0, 100));
            while (true)
            {
                await WaitForNextTimeStepAsync();
                // PID (DerivedEnergy --> CoreTemp)
                var currPowerOutput = await GetVariableAsync<float>("AUX_EFFECTIVELY_DERIVED_ENERGY_KW");
                targetCoreTemp = energyToCoreTempPid.Step(currentTimestamp, targetPowerOutput, currPowerOutput);
                targetCoreTemp = 200;

                // ------
                // PID (CoreTemp --> Rods)
                var coreTempActual = await GetVariableAsync<float>("CORE_TEMP");
                var newRodLevel = coreTempToRodsPid.Step(currentTimestamp, targetCoreTemp, coreTempActual);
                // ------


                Console.SetCursorPosition(0, 0);
                Console.WriteLine("Cool reactor controller :)))))");

                Console.WriteLine($"Curr/Targeted power output: {currPowerOutput}/{targetPowerOutput} E={targetPowerOutput - currPowerOutput}" + padright);
                Console.WriteLine($"Current PID output (new desired core temp): {targetCoreTemp}" + padright);
                Console.WriteLine($"Current PID output (new rod level): {newRodLevel}" + padright);

                int pidx = 0;
                foreach (var pid in new[] { energyToCoreTempPid, coreTempToRodsPid })
                {
                    void pfixLog(string m) => Console.WriteLine($"[PID {pidx}] " + m);
                    pfixLog(pid.StateString);
                    pfixLog($"Raw P/I/D components: {string.Join("/", pid.GetPIDComponents().Select(x => x.ToString()))}" + padright);

                    pidx++;
                }

                await SetVariableAsync("RODS_POS_ORDERED", (float)newRodLevel);
            }
        }
    }
}
