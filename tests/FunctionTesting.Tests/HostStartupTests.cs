using FunctionTesting.TestHost;
using Xunit.Abstractions;

namespace FunctionTesting.Tests;

public class HostStartupTests(ITestOutputHelper output) : IAsyncLifetime
{
    private FunctionsTestHost _host = null!;

    private static string GetWorkerPath()
    {
        // Navigate from test bin folder to the functions project bin folder
        var testDir = AppContext.BaseDirectory;
        var solutionDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var configuration = testDir.Contains("Release") ? "Release" : "Debug";
    
        var workerPath = Path.Combine(solutionDir, "src", "FunctionTesting", "bin", configuration, "net10.0", "FunctionTesting.dll");
        
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException($"Worker not found at: {workerPath}. Make sure to build the Functions project first.");
        }
        
        return workerPath;
    }

    public async Task InitializeAsync()
    {
        var workerPath = GetWorkerPath();
        output.WriteLine($"Worker path: {workerPath}");
        
        _host = await FunctionsTestHost.StartAsync(workerPath, output);
        output.WriteLine($"gRPC server started at: {_host.GrpcEndpoint}");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public void Host_StartsSuccessfully()
    {
        Assert.NotNull(_host);
        Assert.False(string.IsNullOrEmpty(_host.GrpcEndpoint));
    }
    
    [Fact]
    public async Task ProcessBlob_WhenTriggered_ProcessesSuccessfully()
    {
        // Arrange
        var blobData = new BlobTriggerData
        {
            Name = "test-file.json",
            Content = $$"""{"orderId": "12345", "amount": 99.99, "name": "Test Order", "timestamp": "{{DateTimeOffset.UtcNow:o}}"}"""
        };

        // Act
        var result = await _host.TriggerBlobAsync("ProcessBlob", blobData);

        // Assert
        Assert.True(result.Success, $"Function failed: {result.Exception}");
    }
}