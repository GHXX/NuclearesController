namespace NuclearesController;
internal static class Util {
    public static string DictToString(Dictionary<string, float> d) => d.Select(x => $"{x.Key.PadRight(d.Max(x => x.Key.Length) + 1)} {x.Value,11:N5}").JoinByDelim("\n");
}
