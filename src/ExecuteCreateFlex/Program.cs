using Azure;
using Azure.Identity;
using Azure.ResourceManager.ComputeSchedule.Models;
using UtilityMethods;

namespace ExecuteCreateFlex;

public static class Program
{
    private static readonly HashSet<string> s_blockedOperationErrors =
        ["SchedulingOperationsBlockedException", "NonSchedulingOperationsBlockedException"];

    public static async Task Main(string[] args)
    {
        // ---- Step 1: Load configuration from .env ----
        var config = FlexCreateConfig.Load();

        // ---- Step 2: Provision network ----
        var credential = new DefaultAzureCredential();
        var standardClient = ArmClientFactory.CreateStandardClient(credential);
        var subscriptionResource = HelperMethods.GetSubscriptionResource(standardClient, config.SubscriptionId);
        var resourceGroup = await subscriptionResource.GetResourceGroupAsync(config.ResourceGroupName);

        var vnetClient = ArmClientFactory.CreateVNetClient(credential, config.SubscriptionId);
        var vnet = await HelperMethods.CreateVirtualNetwork(resourceGroup, config.SubnetName, config.VnetName, config.Location, vnetClient);
        var subnetId = HelperMethods.GetSubnetId(vnet).ToString();

        // ---- Step 3: Execute batched ExecuteCreateFlex requests ----
        var batchSizes = BuildBatchSizes(FlexRequestBuilder.TotalRequestedVmCount, FlexRequestBuilder.MaxResourceCountPerRequest);
        var scheduleClient = ArmClientFactory.CreateScheduleClient(credential, config.SubscriptionId, config.Location);
        var scheduleSubscriptionResource = HelperMethods.GetSubscriptionResource(scheduleClient, config.SubscriptionId);

        var failedOperations = new List<HelperMethods.FailedVmOperation>();
        int totalValid = 0;
        int totalCompleted = 0;
        int totalSucceeded = 0;
        int totalFailed = 0;
        int totalCancelled = 0;
        int batchRequestFailures = 0;

        Console.WriteLine($"Submitting {FlexRequestBuilder.TotalRequestedVmCount} VMs as {batchSizes.Count} batch request(s) with max {FlexRequestBuilder.MaxParallelBatches} parallel batches.");

        using var concurrencyGate = new SemaphoreSlim(FlexRequestBuilder.MaxParallelBatches);
        var batchTasks = batchSizes.Select((batchSize, batchIndex) => Task.Run(async () =>
        {
            await concurrencyGate.WaitAsync();
            try
            {
                Console.WriteLine($"Starting batch {batchIndex + 1}/{batchSizes.Count} (resourceCount={batchSize}).");

                var executionParams = FlexRequestBuilder.BuildExecutionParams();
                var payload = FlexRequestBuilder.BuildFlexPayload(config, subnetId, batchSize, batchIndex);
                var request = FlexRequestBuilder.BuildRequest(payload, executionParams);

                Dictionary<string, ResourceOperationDetails> completedOperations = [];
                var (_, summary) = await ComputescheduleOperations.ExecuteCreateFlexOperation(
                    completedOperations,
                    executionParams,
                    scheduleSubscriptionResource,
                    s_blockedOperationErrors,
                    request,
                    config.Location);

                lock (failedOperations)
                {
                    failedOperations.AddRange(summary.FailedOperations);
                }

                Interlocked.Add(ref totalValid, summary.ValidCount);
                Interlocked.Add(ref totalCompleted, summary.CompletedCount);
                Interlocked.Add(ref totalSucceeded, summary.SucceededCount);
                Interlocked.Add(ref totalFailed, summary.FailedCount);
                Interlocked.Add(ref totalCancelled, summary.CancelledCount);
            }
            catch (RequestFailedException ex)
            {
                Interlocked.Increment(ref batchRequestFailures);
                lock (failedOperations)
                {
                    failedOperations.Add(new HelperMethods.FailedVmOperation(
                        OperationId: $"batch-{batchIndex}",
                        ResourceId: "batch-request",
                        State: "RequestFailed",
                        ErrorCode: ex.ErrorCode ?? "Unknown",
                        ErrorDetails: ex.Message));
                }

                Console.WriteLine($"Batch {batchIndex + 1}/{batchSizes.Count} request failed with ErrorCode:{ex.ErrorCode} and ErrorMessage:{ex.Message}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref batchRequestFailures);
                lock (failedOperations)
                {
                    failedOperations.Add(new HelperMethods.FailedVmOperation(
                        OperationId: $"batch-{batchIndex}",
                        ResourceId: "batch-request",
                        State: "Exception",
                        ErrorCode: "UnhandledException",
                        ErrorDetails: ex.Message));
                }

                Console.WriteLine($"Batch {batchIndex + 1}/{batchSizes.Count} failed with Exception:{ex.Message}");
            }
            finally
            {
                concurrencyGate.Release();
            }
        })).ToList();

        await Task.WhenAll(batchTasks);

        Console.WriteLine(
            $"Combined final status: requested={FlexRequestBuilder.TotalRequestedVmCount}, valid={totalValid}, completed={totalCompleted}, succeeded={totalSucceeded}, failed={totalFailed}, cancelled={totalCancelled}, batchRequestFailures={batchRequestFailures}.");

        if (failedOperations.Count > 0)
        {
            Console.WriteLine("Failed VM operations across all batches:");
            foreach (var failedOperation in failedOperations)
            {
                Console.WriteLine(
                    $"- resourceId={failedOperation.ResourceId}, state={failedOperation.State}, errorCode={failedOperation.ErrorCode}, errorDetails={failedOperation.ErrorDetails}");
            }
        }
        else
        {
            Console.WriteLine("All batch requests completed without VM operation failures.");
        }
    }

    private static List<int> BuildBatchSizes(int totalVmCount, int maxPerRequest)
    {
        var batchSizes = new List<int>();
        var remaining = totalVmCount;

        while (remaining > 0)
        {
            var currentBatchSize = Math.Min(remaining, maxPerRequest);
            batchSizes.Add(currentBatchSize);
            remaining -= currentBatchSize;
        }

        return batchSizes;
    }
}

