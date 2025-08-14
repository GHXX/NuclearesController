namespace NuclearesController.Controllers;
internal class CoreController : BaseController {
    private const float desiredCoreTempNormalMode = 340f;
    private const float desiredCoreTempMaximumMode = 415f;
    private const float minRodDeltaForUpdate = 0.05f; // the minimum change in desired position required to trigger a set-rod-action
    private const int factorModelNeededObs = 10;
    private const double maxTargetReactivity = 1;
    private const double reactivitySlopeLengthDegrees = 25;
    private string[] coreReactivityRelevantVars = ["RODS_POS_ACTUAL", .. Program.primaryPumpSpeedVariables, "CORE_IODINE_CUMULATIVE", "CORE_XENON_CUMULATIVE", "CHEM_BORON_PPM"];


    private RodControlMode controlMode = RodControlMode.PID;
    private RodControlMode lastControlMode = RodControlMode.PID;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    private PID reactivityToRodsPid;
    private PID tempToThermalPid;
    private MlPlantModel coreFactorModel;
    private double[] reactivityModelX;
    private double lastSetRodposML;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

    
    protected override async Task OnControlOpModeChangedAsync(ControlMode current, ControlMode old) {
        await InnerInit();
    }
    protected override async Task OnNewTimestampAsync() {

        var coreTempCurrent = await GetVariableAsync<float>("CORE_TEMP");
        var reactivityzerobased = await GetVariableAsync<float>("CORE_STATE_CRITICALITY");
        var coreFactorOld = await GetVariableAsync<float>("CORE_FACTOR");
        var opModeSelStr = await GetVariableAsync<string>("CORE_OPERATION_MODE");
        var desiredCoreTemp = opModeSelStr == "MAXIMUM" ? desiredCoreTempMaximumMode : desiredCoreTempNormalMode;

        this.lastControlMode = this.controlMode;
        if (this.coreFactorModel.ObservationCount >= factorModelNeededObs) {
            this.controlMode = RodControlMode.ML;
        }

        if (coreTempCurrent < desiredCoreTemp - 50) {
            this.controlMode = RodControlMode.PID;
            this.coreFactorModel.Reset();
        }

        if (this.lastControlMode != this.controlMode) {
            switch (this.controlMode) {
                case RodControlMode.PID:
                    this.reactivityToRodsPid.Reset(await GetVariableAsync<float>("RODS_POS_ACTUAL"));
                    break;
                case RodControlMode.ML:
                    this.tempToThermalPid.Reset(coreFactorOld);
                    break;
            }
        }


        var estimatedCurrentCoreFactor = 0d;
        var r2_coreFactor = 0d;
        if (this.reactivityModelX.Length > 0) {
            estimatedCurrentCoreFactor = this.coreFactorModel.Evaluate(this.reactivityModelX);
        }
        this.reactivityModelX = [.. this.coreReactivityRelevantVars.Select(x => GetVariableAsync<float>(x).Result)]; // store for next time around
        this.coreFactorModel.AddObservation(this.reactivityModelX, coreFactorOld); // train on current X and current core factor
        r2_coreFactor = this.coreFactorModel.ReFit();

        var coreTempError = coreTempCurrent - desiredCoreTemp;
        var desiredReactivity = Math.Clamp(-coreTempError, -reactivitySlopeLengthDegrees, reactivitySlopeLengthDegrees) / reactivitySlopeLengthDegrees * maxTargetReactivity;
        double? mlEstimatedRodsPos = null;
        switch (this.controlMode) {
            case RodControlMode.PID:
                var newRodsPos = this.reactivityToRodsPid.Step(CurrentTimestamp, desiredReactivity, reactivityzerobased);
                SetVariable("RODS_ALL_POS_ORDERED", newRodsPos);
                break;

            case RodControlMode.ML:
                var newDesiredThermal = this.tempToThermalPid.Step(CurrentTimestamp, desiredCoreTemp, coreTempCurrent); // could add thermal surplus as delta potentially
                mlEstimatedRodsPos = this.coreFactorModel.ReverseSolveForX1(newDesiredThermal, this.reactivityModelX[1..]);
                if (Math.Abs(this.lastSetRodposML - mlEstimatedRodsPos.Value) > minRodDeltaForUpdate)
                    SetVariable("RODS_ALL_POS_ORDERED", mlEstimatedRodsPos);

                this.lastSetRodposML = mlEstimatedRodsPos.Value;
                break;
        }

        if (CurrOpMode is ControlMode.Shutdown or ControlMode.Startup) {
            this.variablesToSet.Remove("RODS_ALL_POS_ORDERED");
        }

        Print($"OPERATION MODE: {opModeSelStr} --> {CurrOpMode.ToString().ToUpperInvariant()} --> Temp target: {(CurrOpMode is ControlMode.Shutdown or ControlMode.Startup ? "Uncontrolled" : desiredCoreTemp)}");
        Print($"CONTROL MODE: {this.controlMode}", this.controlMode switch { RodControlMode.ML => ConsoleColor.Cyan, _ => ConsoleColor.Yellow });
        Print($"Desired/actual reactivity: {desiredReactivity:N3}/{reactivityzerobased:N3}");
        if (this.variablesToSet.TryGetValue("RODS_ALL_POS_ORDERED", out var val)) {
            Print($"New rod level: {val}");
        } else {
            Print("");
        }
        var ctReached = Math.Abs(coreTempCurrent - desiredCoreTemp) < 1 && Math.Abs(reactivityzerobased) < 0.5;
        Print($"\nCORE TEMP REACHED? {ctReached}", ctReached ? ConsoleColor.Green : ConsoleColor.Yellow);


        Print($"\nML Factor fit r²: {r2_coreFactor}; Observation count: {this.coreFactorModel.ObservationCount}/{this.coreFactorModel.MaxObservationCount}");
        Print($"ML Factor estimate: {estimatedCurrentCoreFactor}, actual: {coreFactorOld}; Params: {this.coreFactorModel.KPs.Select(x => x < 1e-10 ? "0" : x.ToString()).JoinByDelim(" ")}");
        Print($"ML Ideal rod pos estimate: {(mlEstimatedRodsPos == null ? ($"NONE - Warming Up: {this.coreFactorModel.ObservationCount}/{factorModelNeededObs}") : ($"{mlEstimatedRodsPos:N2}"))}");

    }

    public override async Task ReInitAsync() {
        await InnerInit();
    }

    private async Task InnerInit() {
        this.controlMode = RodControlMode.PID;
        this.lastControlMode = this.controlMode;


        double rodStartPercentage = await GetVariableAsync<float>("RODS_POS_ACTUAL");
        this.reactivityToRodsPid = new PID(1.5, 0.1 / 4, 0, rodStartPercentage, true, (0, 100));
        this.tempToThermalPid = new PID(0.075, 0.01, 0, 0, false, null);
        this.coreFactorModel = new MlPlantModel(this.coreReactivityRelevantVars.Length);

        this.reactivityModelX = [];
        this.lastSetRodposML = -1;
    }
}
