﻿namespace NuclearesController;

internal class PID
{
    public double KP { get; }
    public double KI { get; }
    public double KD { get; }

    private double integratorValue = 0;
    private readonly (double, double) integratorMinMax;
    private readonly double inverseEffectFactor;
    private (int, double)? lastDatapoint = null;
    private (int, double)? currentDifferentDDatapoint = null;
    private (int, double)? lastDifferentDDatapoint = null;

    private (double, double, double) lastVals = (0, 0, 0);

    public PID(double kP, double kI, double kD, double initialOutput, bool inverseEffect, (double, double)? integratorMinMax)
    {
        this.KP = kP;
        this.KI = kI;
        this.KD = kD;
        this.integratorValue = initialOutput;
        this.integratorMinMax = integratorMinMax ?? (double.MinValue, double.MaxValue);
        this.inverseEffectFactor = inverseEffect ? -1 : 1;
    }

    public string StateString { get; private set; } = "unknown";
    public double Step(int timeSecondsNow, double target, double actual, double? directDelta = null)
    {
        double error = target - actual;

        var currDatapoint = (timeSecondsNow, error);
        double cP = error * this.KP * this.inverseEffectFactor;
        double cI = 0;
        double cD = 0;
        double deriv = 0;
        if (directDelta == null)
        {
            if (this.currentDifferentDDatapoint == null || double.Abs(currDatapoint.Item2 - this.currentDifferentDDatapoint.Value.Item2) > 1e-8) // new changed datapoint
            {
                this.lastDifferentDDatapoint = this.currentDifferentDDatapoint;
                this.currentDifferentDDatapoint = currDatapoint;
            }

            if (this.lastDifferentDDatapoint != null && this.currentDifferentDDatapoint != null)
            {
                var deltaT = this.currentDifferentDDatapoint.Value.Item1 - this.lastDifferentDDatapoint.Value.Item1;
                var deltaE = this.currentDifferentDDatapoint.Value.Item2 - this.lastDifferentDDatapoint.Value.Item2;
                deriv = (deltaE / deltaT);
            }
        }
        else
            deriv = directDelta.Value;

        cD = this.KD * deriv * this.inverseEffectFactor;
        bool ciLimited = false;
        if (this.lastDatapoint != null)
        {
            var deltaT = currDatapoint.Item1 - this.lastDatapoint.Value.Item1;
            var unclamped = this.integratorValue + this.KI * error * deltaT * this.inverseEffectFactor;
            ciLimited = (unclamped > this.integratorMinMax.Item2) || (unclamped < this.integratorMinMax.Item1);
            this.integratorValue = Math.Clamp(unclamped, this.integratorMinMax.Item1, this.integratorMinMax.Item2);
        }

        cI = this.integratorValue;
        this.lastDatapoint = currDatapoint;
        this.lastVals = (cP, cI, cD);

        this.StateString = $"Curr/Target {actual}/{target} E:{error}";

        return ciLimited ? cI : cP + cI + cD;
    }

    public double[] GetPIDComponents() => [this.lastVals.Item1, this.lastVals.Item2, this.lastVals.Item3];

    internal void Reset(float targetI)
    {
        this.integratorValue = targetI;
    }
}
