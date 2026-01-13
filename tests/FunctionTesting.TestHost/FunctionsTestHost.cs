using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace FunctionTesting.TestHost;

public class FunctionsTestHost : IAsyncDisposable
{
    private readonly WebApplication _grpcApp;
    private readonly FunctionsHostService _hostService;
    private readonly ITestOutputHelper? _output;
    private Process? _workerProcess;
    private bool _disposed;

    public FunctionsHostService HostService => _hostService;
    public string GrpcEndpoint { get; private set; } = string.Empty;
    public int Port { get; private set; }

    private FunctionsTestHost(
        WebApplication grpcApp, 
        FunctionsHostService hostService, 
        ITestOutputHelper? output)
    {
        _grpcApp = grpcApp;
        _hostService = hostService;
        _output = output;
    }

    public static Task<FunctionsTestHost> StartAsync(string workerPath) 
        => StartAsync(workerPath, null);

    public static async Task<FunctionsTestHost> StartAsync(string workerPath, ITestOutputHelper? output)
    {

        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = 100 * 1024 * 1024;
            options.MaxSendMessageSize = 100 * 1024 * 1024;
        });
        builder.Services.AddSingleton<FunctionsHostService>();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        var hostService = app.Services.GetRequiredService<FunctionsHostService>();
        
        if (output != null)
        {
            hostService.SetOutput(output);
        }

        app.MapGrpcService<FunctionsHostService>();

        await app.StartAsync();

        var host = new FunctionsTestHost(app, hostService, output);
        
        var address = app.Urls.First();
        host.GrpcEndpoint = address;
        host.Port = new Uri(address).Port;

        output?.WriteLine($"gRPC server listening on port {host.Port}");

        try
        {
            await host.StartWorkerAsync(workerPath);
        }
        catch
        {
            // Ensure cleanup if worker startup fails
            await host.DisposeAsync();
            throw;
        }

        return host;
    }

    private async Task StartWorkerAsync(string workerPath)
    {
        var workerId = Guid.NewGuid().ToString();

        var arguments = $"\"{workerPath}\"";
        
        _output?.WriteLine($"Starting worker with: dotnet {arguments}");
        _output?.WriteLine($"Host endpoint: http://127.0.0.1:{Port}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        startInfo.Environment["FUNCTIONS_WORKER_DIRECTORY"] = Path.GetDirectoryName(workerPath)!;
        startInfo.Environment["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development";
        startInfo.Environment["Functions__Worker__HostEndpoint"] = $"http://127.0.0.1:{Port}";
        startInfo.Environment["Functions__Worker__WorkerId"] = workerId;
        startInfo.Environment["Functions__Worker__GrpcMaxMessageLength"] = "104857600";

        _workerProcess = new Process { StartInfo = startInfo };
        
        _workerProcess.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _output?.WriteLine($"[Worker] {e.Data}");
        };
        
        _workerProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _output?.WriteLine($"[Worker Error] {e.Data}");
        };

        _workerProcess.Start();
        _workerProcess.BeginOutputReadLine();
        _workerProcess.BeginErrorReadLine();

        var timeout = TimeSpan.FromSeconds(30);
        var cts = new CancellationTokenSource(timeout);
        
        try
        {
            await _hostService.WaitForWorkerInitialized().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Worker did not initialize within {timeout.TotalSeconds} seconds");
        }
    }

    public Task<FunctionInvocationResult> TriggerBlobAsync(
        string functionName,
        BlobTriggerData blobData,
        CancellationToken ct = default)
    {
        var bindings = new List<ParameterBinding>
        {
            new()
            {
                Name = "content",
                Data = new TypedData { String = blobData.Content }
            },
            new()
            {
                Name = "name",
                Data = new TypedData { String = blobData.Name }
            }
        };

        var triggerMetadata = new Dictionary<string, TypedData>
        {
            ["BlobTrigger"] = new TypedData { String = $"{blobData.Container}/{blobData.Name}" },
            ["Uri"] = new TypedData { String = $"https://fakestorage.blob.core.windows.net/{blobData.Container}/{blobData.Name}" },
            ["Name"] = new TypedData { String = blobData.Name }
        };

        return _hostService.InvokeAsync(functionName, bindings, triggerMetadata, ct);
    }

    private void KillWorkerProcess()
    {
        if (_workerProcess == null) return;

        try
        {
            if (!_workerProcess.HasExited)
            {
                _output?.WriteLine($"[Host] Killing worker process {_workerProcess.Id}");
                _workerProcess.Kill(entireProcessTree: true);
                _workerProcess.WaitForExit(5000); // Wait up to 5 seconds
            }
        }
        catch (Exception ex)
        {
            _output?.WriteLine($"[Host] Error killing worker: {ex.Message}");
        }
        finally
        {
            _workerProcess.Dispose();
            _workerProcess = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _output?.WriteLine("[Host] Disposing test host...");

        // Kill worker first
        KillWorkerProcess();

        // Then stop servers
        try
        {
            await _grpcApp.StopAsync();
            await _grpcApp.DisposeAsync();
        }
        catch (Exception ex)
        {
            _output?.WriteLine($"[Host] Error stopping gRPC server: {ex.Message}");
        }

        _output?.WriteLine("[Host] Disposed");
    }

    ~FunctionsTestHost()
    {
        // Fallback cleanup if DisposeAsync wasn't called
        KillWorkerProcess();
    }
}