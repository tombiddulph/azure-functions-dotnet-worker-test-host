namespace FunctionTesting.TestHost;

public class BlobTriggerData
{
    public required string Name { get; init; }
    public required string Content { get; init; }
    public string Container { get; init; } = "test-container";
}