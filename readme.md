# Azure Functions Isolated Worker Test Harness

A test harness for Azure Functions .NET Isolated Worker that enables integration testing of blob, Service Bus, and other triggered functions without requiring actual Azure infrastructure.

## Overview

This solution implements a fake Azure Functions host that communicates with the real .NET Isolated Worker via gRPC. It allows you to:

- Trigger functions directly from test code
- Verify function execution and side effects
- Test with dependency injection and service mocks
- Run fast, in-memory integration tests

## Architecture
```
┌─────────────────────────────────────────────────────────────────┐
│  Test Process                                                   │
│                                                                 │
│  ┌─────────────────┐    ┌─────────────────────────────────────┐│
│  │ Test Class      │    │ FunctionsTestHost                   ││
│  │                 │    │  ┌─────────────────────────────────┐││
│  │ TriggerBlob()───┼───▶│  │ gRPC Server (FunctionsHostSvc) │││
│  │                 │    │  │  - WorkerInit handshake         │││
│  │ Assert(...)◀────┼────│  │  - Function discovery           │││
│  │                 │    │  │  - Invocation dispatch          │││
│  └─────────────────┘    │  └───────────────┬─────────────────┘││
│                         │                  │ gRPC              ││
│  ┌─────────────────┐    │  ┌───────────────▼─────────────────┐││
│  │ CallbackServer  │◀───┼──│ Worker Process                  │││
│  │  - HTTP POST    │    │  │  - Your Functions               │││
│  │  - Captures     │    │  │  - DI Container                 │││
│  │    events       │    │  │  - FakeBlobProcessor            │││
│  └─────────────────┘    │  └─────────────────────────────────┘││
│                         └─────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

## Project Structure
```
FunctionTesting/
├── src/
│   └── FunctionTesting/                    # Azure Functions project
│       ├── Program.cs                      # Host configuration & DI setup
│       ├── ProcessBlobFunction.cs          # Blob-triggered function
│       ├── BlobProcessor.cs                # IBlobProcessor interface & implementation
│       ├── host.json                       # Functions host configuration
│       └── local.settings.json             # Local development settings
├── tests/
│   ├── FunctionTesting.TestHost/           # Test harness library
│   │   ├── protos/
│   │   │   ├── FunctionRpc.proto           # Azure Functions gRPC protocol
│   │   │   ├── NullableTypes.proto         # Nullable type definitions
│   │   │   └── ClaimsIdentityRpc.proto     # Claims identity definitions
│   │   ├── FunctionsTestHost.cs            # Main test host orchestrator
│   │   ├── FunctionsHostService.cs         # gRPC service implementation
│   │   ├── FunctionInvocationResult.cs     # Function execution result
│   │   └── BlobTriggerData.cs              # Blob trigger input data
│   └── FunctionTesting.Tests/              # Test project
│       ├── HostStartupTests.cs             # Integration tests
│       └── FakeBlobProcessor.cs            # Test double for blob processing
```

## How It Works

### 1. gRPC Host Simulation

The `FunctionsHostService` implements the `FunctionRpc.FunctionRpcBase` gRPC service that the Azure Functions host normally provides. It handles:

- **StartStream**: Worker connection handshake
- **WorkerInitRequest/Response**: Worker initialization
- **FunctionsMetadataRequest/Response**: Function discovery
- **FunctionLoadRequest/Response**: Function registration
- **InvocationRequest/Response**: Function execution

### 2. Worker Process Management

The `FunctionsTestHost` starts your Azure Functions worker as a child process with environment variables pointing to the test gRPC server:
```csharp
startInfo.Environment["Functions__Worker__HostEndpoint"] = $"http://127.0.0.1:{Port}";
startInfo.Environment["Functions__Worker__WorkerId"] = workerId;
```

### 3. Callback Server for Verification

Since the worker runs in a separate process, a `TestCallbackServer` provides an HTTP endpoint for capturing events from fake services:
```csharp
var processedEvent = await _host.CallbackServer.WaitForEventAsync();
Assert.Equal("test-file.json", processedEvent.Name);
```

## Usage

### Basic Test Setup
```csharp
public class BlobFunctionTests : IAsyncLifetime
{
    private FunctionsTestHost _host = null!;

    public async Task InitializeAsync()
    {
        var workerPath = GetWorkerPath();
        _host = await FunctionsTestHost.StartAsync(workerPath);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static string GetWorkerPath()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionDir, "src", "FunctionTesting", "bin", "Debug", "net10.0", "FunctionTesting.dll");
    }
}
```

### Triggering a Blob Function
```csharp
[Fact]
public async Task ProcessBlob_WhenTriggered_ProcessesSuccessfully()
{
    // Arrange
    var blobData = new BlobTriggerData
    {
        Name = "test-file.json",
        Content = """{"orderId": "12345", "amount": 99.99}"""
    };

    // Act
    var result = await _host.TriggerBlobAsync("ProcessBlob", blobData);

    // Assert
    Assert.True(result.Success, $"Function failed: {result.Exception}");
    
    var processedEvent = await _host.CallbackServer.WaitForEventAsync();
    Assert.Equal("test-file.json", processedEvent.Name);
}
```

### Function with Dependency Injection

In your Functions project, create a service interface and fake implementation:
```csharp
// IBlobProcessor.cs
public interface IBlobProcessor
{
    void Process(string name, string content);
}

// FakeBlobProcessor.cs
public class FakeBlobProcessor : IBlobProcessor
{
    private readonly HttpClient _httpClient;
    private readonly string _callbackUrl;

    public FakeBlobProcessor(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _callbackUrl = Environment.GetEnvironmentVariable("TEST_CALLBACK_URL") ?? "";
    }

    public void Process(string name, string content)
    {
        if (!string.IsNullOrEmpty(_callbackUrl))
        {
            _ = _httpClient.PostAsJsonAsync(
                $"{_callbackUrl}/blob-processed", 
                new { Name = name, Content = content });
        }
    }
}
```

Register in Program.cs:
```csharp
builder.Services
    .AddHttpClient()
    .AddSingleton<IBlobProcessor, FakeBlobProcessor>();
```

## Adding New Trigger Types

### Service Bus Trigger

1. Add a trigger data class:
```csharp
// ServiceBusTriggerData.cs
public class ServiceBusTriggerData
{
    public required string Body { get; init; }
    public string MessageId { get; init; } = Guid.NewGuid().ToString();
    public string ContentType { get; init; } = "application/json";
    public IDictionary<string, string> ApplicationProperties { get; init; } = new Dictionary<string, string>();
}
```

2. Add a trigger method to `FunctionsTestHost`:
```csharp
public Task<FunctionInvocationResult> TriggerServiceBusAsync(
    string functionName,
    ServiceBusTriggerData messageData,
    CancellationToken ct = default)
{
    var bindings = new List<ParameterBinding>
    {
        new()
        {
            Name = "message",
            Data = new TypedData { String = messageData.Body }
        }
    };

    var triggerMetadata = new Dictionary<string, TypedData>
    {
        ["MessageId"] = new TypedData { String = messageData.MessageId },
        ["ContentType"] = new TypedData { String = messageData.ContentType }
    };

    return _hostService.InvokeAsync(functionName, bindings, triggerMetadata, ct);
}
```

## Package Dependencies

### TestHost Project
```xml
<ItemGroup>
    <PackageReference Include="Grpc.AspNetCore" Version="2.67.0" />
    <PackageReference Include="Grpc.Tools" Version="2.67.0" PrivateAssets="All" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Grpc" Version="1.17.2" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
    <PackageReference Include="xunit.abstractions" Version="2.0.3" />
</ItemGroup>

<ItemGroup>
    <Protobuf Include="Protos\*.proto" ProtoRoot="Protos" GrpcServices="Server" />
</ItemGroup>
```

### Test Project
```xml
<ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
</ItemGroup>

<ItemGroup>
    <ProjectReference Include="..\FunctionTesting.TestHost\FunctionTesting.TestHost.csproj" />
    <ProjectReference Include="..\..\src\FunctionTesting\FunctionTesting.csproj" />
</ItemGroup>
```

## Proto Files

Download from [azure-functions-language-worker-protobuf](https://github.com/Azure/azure-functions-language-worker-protobuf):
```powershell
mkdir Protos; cd Protos
iwr "https://raw.githubusercontent.com/Azure/azure-functions-language-worker-protobuf/dev/src/proto/FunctionRpc.proto" -OutFile "FunctionRpc.proto"
iwr "https://raw.githubusercontent.com/Azure/azure-functions-language-worker-protobuf/dev/src/proto/shared/NullableTypes.proto" -OutFile "NullableTypes.proto"
iwr "https://raw.githubusercontent.com/Azure/azure-functions-language-worker-protobuf/dev/src/proto/identity/ClaimsIdentityRpc.proto" -OutFile "ClaimsIdentityRpc.proto"
```

Edit `FunctionRpc.proto` to fix import paths:
```protobuf
// Change:
import "shared/NullableTypes.proto";
import "identity/ClaimsIdentityRpc.proto";

// To:
import "NullableTypes.proto";
import "ClaimsIdentityRpc.proto";
```

## Limitations

- **Out-of-process only**: This harness only works with the .NET Isolated Worker model, not the in-process model
- **No binding validation**: The harness doesn't validate binding configurations like the real host would
- **No retry policies**: Failed invocations aren't automatically retried
- **No concurrency control**: No function-level concurrency limits are enforced
- **Simplified trigger metadata**: Some trigger-specific metadata may be missing

## Troubleshooting

### Worker doesn't connect

Check the environment variables are being set correctly:
```csharp
startInfo.Environment["Functions__Worker__HostEndpoint"] = $"http://127.0.0.1:{Port}";
startInfo.Environment["Functions__Worker__WorkerId"] = workerId;
```

### gRPC message size errors

Increase the message size limits:
```csharp
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 100 * 1024 * 1024;
    options.MaxSendMessageSize = 100 * 1024 * 1024;
});
```

### Function not found

Ensure the function name matches exactly (case-insensitive). Check the logs for discovered functions:
```
[Host] Loading function: ProcessBlob
[Host]   EntryPoint: FunctionTesting.ProcessBlobFunction.ProcessBlob
```

## License

MIT