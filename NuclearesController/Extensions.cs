namespace NuclearesController;
internal static class Extensions
{
    public static string JoinByDelim<T>(this IEnumerable<T> a, string delim) => string.Join(delim, a);
}
