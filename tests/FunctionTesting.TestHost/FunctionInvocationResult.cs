using Microsoft.Azure.WebJobs.Script.Grpc.Messages;

namespace FunctionTesting.TestHost;

public class FunctionInvocationResult
{
    public bool Success { get; init; }
    public string? Exception { get; init; }
    public TypedData? ReturnValue { get; init; }
    public IReadOnlyList<ParameterBinding> OutputData { get; init; } = [];
}

public class FunctionDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string EntryPoint { get; init; }
    public required string ScriptFile { get; init; }
    public Dictionary<string, BindingInfo> Bindings { get; init; } = new();

    public string? TriggerParameterName => Bindings
        .FirstOrDefault(b => b.Value.Direction == BindingInfo.Types.Direction.In)
        .Key;
}