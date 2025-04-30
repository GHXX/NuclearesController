using MathNet.Numerics.LinearRegression;

namespace NuclearesController;
internal class IntelligentLeastSquares {
    public static double[] LstsqWithBias(double[][] X, double[] y, double aTol = 1e-3) {
        var goodColumns = new List<int>();
        for (int col = 0; col < X[0].Length; col++) {
            var otherVar = X[0][col];
            for (int i = 1; i < X.Length; i++) {
                if (Math.Abs(otherVar - X[i][col]) > aTol) {
                    goodColumns.Add(col); break;
                }
            }
        }
        double[][] denseX = [.. X.Select(row => row.Where((_, colIdx) => goodColumns.Contains(colIdx)).ToArray())];
        var coeffs = MultipleRegression.DirectMethod(denseX, y, true); // first is bias
        int coeffSrcIdx = 1;
        double[] rv = new double[X[0].Length];
        for (int i = 0; i < rv.Length; i++) {
            if (goodColumns.Contains(i))
                rv[i] = coeffs[coeffSrcIdx++];
        }
        if (coeffSrcIdx != coeffs.Length)
            throw new Exception("not all coeffs were read?");

        if (coeffs.Any(x => double.IsNaN(x) || double.IsInfinity(x) || double.IsNegativeInfinity(x)))
            throw new Exception("lstsq failed");

        return [.. rv.Prepend(coeffs[0])];
    }
}
