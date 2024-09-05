using System.Numerics;

namespace NuclearesController;
internal class DeltaDictHelper<T>(Dictionary<string, T> initialVals) where T : INumber<T>
{
    private Dictionary<string, T> lastVals = initialVals;

    public Dictionary<string, T> Tick(Dictionary<string, T> newVals)
    {
        var deltaDict = new Dictionary<string, T>();
        foreach (var key in newVals.Keys)
        {
            deltaDict[key] = newVals[key] - this.lastVals[key];
        }

        this.lastVals = newVals;
        return deltaDict;
    }
}
