namespace FunctionTesting.Tests;

public class FakeBlobProcessor : IBlobProcessor
{
    private List<(string Name, string Content)> ProcessedBlobs { get; } = [];

    public void Process(string name, string content)
    {
        ProcessedBlobs.Add((name, content));
    }
}