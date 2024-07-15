namespace NuclearesController
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting controller...");
            Console.Title = "Nucleares Controller";

            const int PORT = 8181;
            var requestTimeout = TimeSpan.FromSeconds(2);
            var hc = new HttpClient() { BaseAddress = new($"http://localhost:{PORT}/") };
            async Task<T> GetVariableAsync<T>(string varname) where T : IParsable<T>
                => T.Parse(await hc.GetStringAsync($"?variable={varname}", new CancellationTokenSource(requestTimeout).Token), null);

            async Task SetVariableAsync(string varname, float value) => await hc.PostAsync($"?variable={varname}&value={value}", null);

            var rodStartPercentage = await GetVariableAsync<float>("RODS_POS_ACTUAL");
            var mainPid = new PID(0.00005, 0.00001, 0.02, rodStartPercentage, true, (0, 100), TimeSpan.FromSeconds(15));
            string padright = new string(' ', 32);
            const float absorptionCapacity = 10000;
            const float targetPowerOutput = (absorptionCapacity / 2) * (0.75f);
            int currentTimestamp = 0;
            while (true)
            {
                while (true)
                {
                    var nextTs = await GetVariableAsync<int>("TIME_STAMP"); // number representing minutes since start of game, ingame time
                    if (nextTs != currentTimestamp) { currentTimestamp = nextTs; break; }
                    await Task.Delay(500);
                }

                var currPowerOutput = await GetVariableAsync<float>("AUX_EFFECTIVELY_DERIVED_ENERGY_KW");
                Console.SetCursorPosition(0, 0);
                Console.WriteLine("Cool reactor controller :)))))");

                var newRodLevel = Math.Clamp(mainPid.Step(currentTimestamp, targetPowerOutput, currPowerOutput), 0, 100);
                Console.WriteLine($"Curr/Targeted power output: {currPowerOutput}/{targetPowerOutput} E={targetPowerOutput - currPowerOutput}" + padright);
                Console.WriteLine($"Current PID output (new rod level): {newRodLevel}" + padright);
                Console.WriteLine($"Raw P/I/D components: {mainPid.GetPIDComponents().Select(x => x.ToString()).Aggregate((a, b) => $"{a}/{b}")}" + padright);
                await SetVariableAsync("RODS_POS_ORDERED", (float)newRodLevel);
            }
        }
    }
}
