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

        // ---- Step 3: Build the ExecuteCreateFlex request ----
        var executionParams = FlexRequestBuilder.BuildExecutionParams();
        var payload = FlexRequestBuilder.BuildFlexPayload(config, subnetId);
        var request = FlexRequestBuilder.BuildRequest(payload, executionParams);

        // ---- Step 4: Execute and poll ----
        var scheduleClient = ArmClientFactory.CreateScheduleClient(credential, config.SubscriptionId, config.Location);
        var scheduleSubscriptionResource = HelperMethods.GetSubscriptionResource(scheduleClient, config.SubscriptionId);

        Dictionary<string, ResourceOperationDetails> completedOperations = [];
        Console.WriteLine("Calling ExecuteCreateFlex...");
        await ComputescheduleOperations.ExecuteCreateFlexOperation(
            completedOperations,
            executionParams,
            scheduleSubscriptionResource,
            s_blockedOperationErrors,
            request,
            config.Location);
    }
}

