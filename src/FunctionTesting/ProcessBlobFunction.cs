using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionTesting;

public class ProcessBlobFunction(ILogger<ProcessBlobFunction> logger, IBlobProcessor blobProcessor)
{
    [Function(nameof(ProcessBlob))]
    public void ProcessBlob(
        [BlobTrigger("test-container/{name}")] string content,
        string name)
    {
        logger.LogInformation("Processing blob: {Name}", name);
        logger.LogInformation("Content: {Content}", content);
        blobProcessor.Process(name, content);
    }
}