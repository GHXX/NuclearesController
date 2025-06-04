using MathNet.Numerics.LinearAlgebra;

namespace NuclearesController;
internal class IntelligentLeastSquares {
    public static double[] LstsqWithBias(double[][] X, double[] y, double rcond = 1e-8) {
        X = [.. X.Select(x => (double[])[1, .. x])];
        var svd = Matrix<double>.Build.DenseOfRowArrays(X).Svd();

        var sMax = svd.S.Max(Math.Abs);
        var goodVals = svd.S.Select((x, i) => (x, i)).Where(x => Math.Abs(x.x) / sMax > rcond).Select(x => x.i).ToArray();
        var U = SelectColumns(svd.U, goodVals);
        var s = svd.S.Where((x, i) => goodVals.Contains(i)).Select(x => 1 / x).ToArray();
        var V = SelectColumns(svd.VT.Transpose(), goodVals);
        var coeffs = V * Matrix<double>.Build.Diagonal(s) * U.Transpose() * Vector<double>.Build.DenseOfArray(y);

        if (coeffs.Any(x => double.IsNaN(x) || double.IsInfinity(x) || double.IsNegativeInfinity(x)))
            throw new Exception("lstsq failed");

        return [.. coeffs];
    }

    public static Matrix<T> SelectColumns<T>(Matrix<T> X, int[] cols) where T : struct, IEquatable<T>, IFormattable {
        return Matrix<T>.Build.DenseOfColumnVectors(X.EnumerateColumns().Where((_, i) => cols.Contains(i)));
    }
}
