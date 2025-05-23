using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;

namespace NuclearesController;
internal class MlPlantModel(int inputCount) {
    private const int replayBufferLen = 60 * 12;
    private double[] kPs = new double[inputCount + 1]; // first is bias
    public IReadOnlyList<double> KPs => this.kPs;
    public int ObservationCount => this.observations.Count;
    public int MaxObservationCount => replayBufferLen;

    private readonly List<Observation> observations = new(replayBufferLen);
    //private readonly double[] iPs = new double[inputCount];


    public double ReFit() {
        // fit [[inputs]] @ [kPs] = [outputNextTick] + [slack], minimizing slack
        if (this.observations.Count < inputCount) return -1;
        double[] y = [.. this.observations.Select(x => x.OutputNextTick)];
        double[][] X = [.. this.observations.Select(x => x.Inputs)];
        this.kPs = IntelligentLeastSquares.LstsqWithBias(X, y);
        //this.kPs = Fit.MultiDimWeighted(X, y, [.. Enumerable.Repeat(1.0, this.observations.Count)]);
        //MultipleRegression.Svd([.. this.observations.Select(x => x.Inputs)], y);

        var r2 = GoodnessOfFit.CoefficientOfDetermination(
            Matrix<double>.Build.DenseOfRows([.. this.observations.Select(x => x.Inputs)]).Multiply(Vector<double>.Build.DenseOfArray(this.kPs[1..])) + this.kPs[0],
            y);
        return r2;
    }

    public double ReverseSolveForX1(double desiredOutput, double[] remainingObservations) {
        return (desiredOutput - (Vector<double>.Build.DenseOfArray(remainingObservations).DotProduct(Vector<double>.Build.DenseOfArray(this.kPs[2..])) + this.kPs[0])) / this.KPs[1];
    }

    public double Evaluate(double[] x) => x.Dot(this.kPs[1..]) + this.kPs[0];
    public void AddObservation(double[] x, double y) {
        if (x.Length != inputCount) throw new ArgumentException("x has invalid length");
        if (this.observations.Count >= replayBufferLen) this.observations.RemoveAt(0);
        this.observations.Add(new(x, y));
    }

    private record struct Observation(double[] Inputs, double OutputNextTick);
}
