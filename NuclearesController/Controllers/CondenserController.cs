namespace NuclearesController.Controllers;
internal class CondenserController : BaseController {
    private const float condenserRetentionTankDesiredFilllevel = 40_000 * 0.5f;
    private const float desiredCondenserLevelMin = 160_000f;
    private const float desiredCondenserLevelMax = 200_000f;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    private PID condenserRetentionTankPid;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

    public override async Task ReInitAsync() {
        this.condenserRetentionTankPid = new PID(0.005, 0.00005, 0, await GetVariableAsync<float>("STEAM_EJECTOR_OPERATIONAL_MOTIVE_VALVE_ORDERED"), false, (0, 100));
    }

    protected override async Task OnNewTimestampAsync() {
        if (CurrOpMode == ControlMode.Normal || CurrOpMode == ControlMode.Startup) {
            var condenserRetentionTankFillLevelCurrent = await GetVariableAsync<float>("VACUUM_RETENTION_TANK_VOLUME");
            var newOpValveOrdered = this.condenserRetentionTankPid.Step(CurrentTimestamp, condenserRetentionTankDesiredFilllevel, condenserRetentionTankFillLevelCurrent);
            SetVariable("STEAM_EJECTOR_OPERATIONAL_MOTIVE_VALVE", Math.Clamp(newOpValveOrdered, 0, 100).ToString("N2"));
        }

        var condenserLevelCurrent = await GetVariableAsync<float>("CONDENSER_VOLUME");
        if (condenserLevelCurrent < desiredCondenserLevelMin)
            SetVariable("FREIGHT_PUMP_CONDENSER_SWITCH", true);
        else if (condenserLevelCurrent > desiredCondenserLevelMax)
            SetVariable("FREIGHT_PUMP_CONDENSER_SWITCH", false);
    }
}
