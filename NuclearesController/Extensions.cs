namespace NuclearesController;
internal static class Extensions {
    public static string JoinByDelim<T>(this IEnumerable<T> a, string delim) => string.Join(delim, a);

    public static double Dot(this double[] x, double[] y) {
        double rv = 0;
        if (x.Length != y.Length)
            throw new ArgumentException("arrays need to be of same length");

        for (var i = 0; i < x.Length; i++) {
            rv += x[i] * y[i];
        }
        return rv;
    }
}
