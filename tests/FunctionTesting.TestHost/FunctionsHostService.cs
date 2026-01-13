using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Xunit.Abstractions;

namespace FunctionTesting.TestHost;

public class FunctionsHostService : FunctionRpc.FunctionRpcBase
{
    private readonly ConcurrentDictionary<string, FunctionDefinition> _functions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FunctionInvocationResult>> _pendingInvocations = new();
    private readonly TaskCompletionSource _workerInitialized = new();

    private IServerStreamWriter<StreamingMessage>? _responseStream;
    private int _expectedFunctionCount;
    private int _functionsLoadedCount;

    private ITestOutputHelper? _output;

    public List<InvocationResponse> CompletedInvocations { get; } = [];

    public void SetOutput(ITestOutputHelper output) => _output = output;

    private void Log(string message)
    {
        _output?.WriteLine(message);
        Console.WriteLine(message);
    }

    public override async Task EventStream(
        IAsyncStreamReader<StreamingMessage> requestStream,
        IServerStreamWriter<StreamingMessage> responseStream,
        ServerCallContext context)
    {
        _responseStream = responseStream;

        try
        {
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await HandleWorkerMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
            Log("[Host] EventStream cancelled (shutdown)");
        }
        catch (IOException)
        {
            // Expected when worker process is killed
            Log("[Host] EventStream connection closed");
        }
    }

    private async Task HandleWorkerMessage(StreamingMessage message)
    {
        Log($"[Host] Received: {message.ContentCase}");

        switch (message.ContentCase)
        {
            case StreamingMessage.ContentOneofCase.StartStream:
                await SendWorkerInitRequest();
                break;

            case StreamingMessage.ContentOneofCase.WorkerInitResponse:
                await SendFunctionsMetadataRequest();
                break;

            case StreamingMessage.ContentOneofCase.FunctionMetadataResponse:
                await HandleFunctionMetadataResponse(message.FunctionMetadataResponse);
                break;

            case StreamingMessage.ContentOneofCase.FunctionLoadResponse:
                HandleFunctionLoadResponse(message.FunctionLoadResponse);
                break;

            case StreamingMessage.ContentOneofCase.InvocationResponse:
                HandleInvocationResponse(message.InvocationResponse);
                break;

            case StreamingMessage.ContentOneofCase.RpcLog:
                Log($"[Worker Log] [{message.RpcLog.Level}] {message.RpcLog.Message}");
                break;
        }
    }

    private async Task SendWorkerInitRequest()
    {
        var initRequest = new StreamingMessage
        {
            WorkerInitRequest = new WorkerInitRequest
            {
                HostVersion = "4.0.0",
                WorkerDirectory = Directory.GetCurrentDirectory(),
                FunctionAppDirectory = Directory.GetCurrentDirectory(),
            }
        };

        Log("[Host] Sending WorkerInitRequest");
        await _responseStream!.WriteAsync(initRequest);
    }

    private async Task SendFunctionsMetadataRequest()
    {
        var request = new StreamingMessage
        {
            FunctionsMetadataRequest = new FunctionsMetadataRequest
            {
                FunctionAppDirectory = Directory.GetCurrentDirectory()
            }
        };

        Log("[Host] Sending FunctionsMetadataRequest");
        await _responseStream!.WriteAsync(request);
    }

    private async Task HandleFunctionMetadataResponse(FunctionMetadataResponse response)
    {
        Log($"[Host] Received metadata for {response.FunctionMetadataResults.Count} functions");

        if (response.FunctionMetadataResults.Count == 0)
        {
            _workerInitialized.TrySetResult();
            return;
        }

        _expectedFunctionCount = response.FunctionMetadataResults.Count;

        foreach (var metadata in response.FunctionMetadataResults)
        {
            Log($"[Host] Loading function: {metadata.Name}");
            Log($"[Host]   EntryPoint: {metadata.EntryPoint}");
            Log($"[Host]   Bindings: {string.Join(", ", metadata.Bindings.Select(b => $"{b.Key}:{b.Value.Type}:{b.Value.Direction}"))}");

            var functionId = Guid.NewGuid().ToString();

            var loadRequest = new StreamingMessage
            {
                FunctionLoadRequest = new FunctionLoadRequest
                {
                    FunctionId = functionId,
                    Metadata = metadata
                }
            };

            _functions[functionId] = new FunctionDefinition
            {
                Id = functionId,
                Name = metadata.Name,
                EntryPoint = metadata.EntryPoint,
                ScriptFile = metadata.ScriptFile
            };

            await _responseStream!.WriteAsync(loadRequest);
        }
    }

    private void HandleFunctionLoadResponse(FunctionLoadResponse response)
    {
        Log($"[Host] Function load response: {response.FunctionId} - {response.Result.Status}");

        if (response.Result.Status != StatusResult.Types.Status.Success)
        {
            Log($"[Host] Function load failed: {response.Result.Exception?.Message}");
        }

        _functionsLoadedCount++;
        if (_functionsLoadedCount >= _expectedFunctionCount)
        {
            Log("[Host] All functions loaded, worker ready");
            _workerInitialized.TrySetResult();
        }
    }

    public async Task<FunctionInvocationResult> InvokeAsync(
        string functionName,
        List<ParameterBinding> inputBindings,
        IDictionary<string, TypedData>? triggerMetadata = null,
        CancellationToken ct = default)
    {
        var function = _functions.Values.FirstOrDefault(f =>
                           f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"Function '{functionName}' not found. Available functions: {string.Join(", ", _functions.Values.Select(f => f.Name))}");

        var invocationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<FunctionInvocationResult>();
        _pendingInvocations[invocationId] = tcs;

        var invocationRequest = new InvocationRequest
        {
            InvocationId = invocationId,
            FunctionId = function.Id,
            TraceContext = new RpcTraceContext
            {
                TraceParent = $"00-{Guid.NewGuid():N}-{Guid.NewGuid().ToString("N").Substring(0, 16)}-01"
            }
        };

        invocationRequest.InputData.Add(inputBindings);

        if (triggerMetadata != null)
        {
            foreach (var kvp in triggerMetadata)
            {
                invocationRequest.TriggerMetadata.Add(kvp.Key, kvp.Value);
            }
        }

        var message = new StreamingMessage
        {
            InvocationRequest = invocationRequest
        };

        Log($"[Host] Invoking function: {functionName} ({function.Id})");
        foreach (var binding in inputBindings)
        {
            Log($"[Host]   Input: {binding.Name} = {binding.Data?.String ?? binding.Data?.Bytes?.ToStringUtf8() ?? "(null)"}");
        }
        if (triggerMetadata != null)
        {
            foreach (var kvp in triggerMetadata)
            {
                Log($"[Host]   Trigger: {kvp.Key} = {kvp.Value?.String ?? "(null)"}");
            }
        }

        await _responseStream!.WriteAsync(message, ct);

        await using var registration = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    private void HandleInvocationResponse(InvocationResponse response)
    {
        Log($"[Host] Invocation response: {response.InvocationId} - {response.Result.Status}");

        if (response.Result.Status != StatusResult.Types.Status.Success)
        {
            Log($"[Host] Invocation failed: {response.Result.Exception?.Message}");
            Log($"[Host] Stack trace: {response.Result.Exception?.StackTrace}");
        }

        CompletedInvocations.Add(response);

        if (_pendingInvocations.TryRemove(response.InvocationId, out var tcs))
        {
            var result = new FunctionInvocationResult
            {
                Success = response.Result.Status == StatusResult.Types.Status.Success,
                Exception = response.Result.Exception?.Message,
                ReturnValue = response.ReturnValue,
                OutputData = response.OutputData.ToList()
            };
            tcs.SetResult(result);
        }
    }

    public Task WaitForWorkerInitialized() => _workerInitialized.Task;
}