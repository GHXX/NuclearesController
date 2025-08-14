namespace NuclearesController.Controllers;
internal abstract class BaseController {
    public abstract Task ReInitAsync();
    public async Task NotifyNewTimestampAsync() {
        this.infoTexts.Clear();
        await OnNewTimestampAsync();
    }
    protected abstract Task OnNewTimestampAsync();
    public async Task NotifyControlOpModeChangedAsync(ControlMode current, ControlMode old) {
        await OnControlOpModeChangedAsync(current, old);
    }
    protected virtual Task OnControlOpModeChangedAsync(ControlMode current, ControlMode old) => Task.CompletedTask;

    private readonly List<(string, ConsoleColor)> infoTexts = [];
    public (string, ConsoleColor)[] GetMessagesToPrint() => this.infoTexts.ToArray();


    protected static async Task<T> GetVariableAsync<T>(string varname) where T : IParsable<T> => await Program.GetVariableAsync<T>(varname);
    protected static async Task SetVariableAsync(string varname, object value) => await Program.SetVariableAsync(varname, value);


    protected void Print(string msg, ConsoleColor c = Program.defaultForegroundColor) => this.infoTexts.Add((msg, c));
    protected void PrintLine(string msg, ConsoleColor c = Program.defaultForegroundColor) => Print(msg + "\n", c);

    public readonly Dictionary<string, string> variablesToSet = [];
    protected void SetVariable(string name, object value) => this.variablesToSet[name] = value.ToString()!;

    public static int CurrentTimestamp => Program.currentTimestamp;
    public static ControlMode CurrOpMode => Program.currOpMode;
}
