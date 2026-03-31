using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ComputeSchedule.Models;
using UtilityMethods;

namespace ExecuteCreateFlex
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var blockedOperationsException = new HashSet<string> { "SchedulingOperationsBlockedException", "NonSchedulingOperationsBlockedException" };

            // Location: The location of the virtual machines
            const string location = "eastus2euap";

            // SubscriptionId: The subscription id under which the virtual machines are located
            const string subscriptionId = "1d04e8f1-ee04-4056-b0b2-718f5bb45b04";

            // ResourceGroupName: The resource group name under which the virtual machines are located
            const string resourceGroupName = "computeschedule-azcliext-resources";

            // SubnetId: The resource ID of the subnet to use for the virtual machines
            // Update this value to match a subnet that exists in your resource group
            const string subnetId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Network/virtualNetworks/default-vnet/subnets/default-subnet";

            Dictionary<string, ResourceOperationDetails> completedOperations = [];

            // Credential: The Azure credential used to authenticate the request
            TokenCredential cred = new DefaultAzureCredential();

            // Client: The Azure Resource Manager client configured with a regional endpoint
            // ComputeSchedule requires a location-specific ARM endpoint
            var options = new ArmClientOptions
            {
                Environment = new ArmEnvironment(new Uri($"https://{location}.management.azure.com"), "https://management.core.windows.net/")
            };

            // Adding custom headers to the ARM client (optional)
            // options.AddPolicy(new SetHeaderPolicy(), HttpPipelinePosition.PerCall);

            ArmClient client = new(cred, subscriptionId, options);
            var subscriptionResource = HelperMethods.GetSubscriptionResource(client, subscriptionId);

            // Execution parameters for the request including the retry policy used by ScheduledActions to retry the operation in case of failures
            var executionParams = new ScheduledActionExecutionParameterDetail()
            {
                RetryPolicy = new UserRequestRetryPolicy()
                {
                    // Number of times ScheduledActions should retry the operation in case of failures: Range 0-7
                    RetryCount = 1,
                    // Time window in minutes within which ScheduledActions should retry the operation in case of failures: Range in minutes 5-120
                    RetryWindowInMinutes = 45
                }
            };

            // Flex properties: define prioritized VM size profiles and allocation strategy
            // ScheduledActions will try each size in priority order if the preferred size is unavailable
            var flexProperties = new FlexProperties(
                new[]
                {
                    new VmSizeProfile("Standard_D2ads_v5", 0),
                    new VmSizeProfile("Standard_E2ads_v5", 1),
                    new VmSizeProfile("Standard_D2ds_v5", 2),
                },
                OsType.Windows,
                new PriorityProfile
                {
                    Type = PriorityType.Regular,
                    AllocationStrategy = AllocationStrategy.Prioritized,
                });

            // Build the resource provisioning payload with flex properties
            var resourceConfig = new ResourceProvisionFlexPayload(1, flexProperties)
            {
                ResourcePrefix = "sampleflex",
            };

            resourceConfig.BaseProfile["resourcegroupName"] = BinaryData.FromString($"\"{resourceGroupName}\"");
            resourceConfig.BaseProfile["computeApiVersion"] = BinaryData.FromString("\"2023-09-01\"");
            resourceConfig.BaseProfile["properties"] = BinaryData.FromObjectAsJson(new
            {
                hardwareProfile = new { vmSize = "Standard_D2ads_v5" },
                storageProfile = new
                {
                    imageReference = new
                    {
                        publisher = "MicrosoftWindowsServer",
                        offer = "WindowsServer",
                        sku = "2022-datacenter-azure-edition",
                        version = "latest"
                    },
                    osDisk = new
                    {
                        osType = "Windows",
                        createOption = "FromImage",
                        caching = "ReadWrite",
                        managedDisk = new { storageAccountType = "Standard_LRS" },
                        deleteOption = "Detach",
                        diskSizeGB = 127
                    },
                    diskControllerType = "SCSI"
                },
                networkProfile = new
                {
                    networkInterfaceConfigurations = new[]
                    {
                        new
                        {
                            name = "samplenic",
                            properties = new
                            {
                                primary = true,
                                enableIPForwarding = true,
                                ipConfigurations = new[]
                                {
                                    new
                                    {
                                        name = "samplenic",
                                        properties = new
                                        {
                                            subnet = new { id = subnetId },
                                            primary = true,
                                            applicationGatewayBackendAddressPools = Array.Empty<object>(),
                                            loadBalancerBackendAddressPools = Array.Empty<object>()
                                        }
                                    }
                                }
                            }
                        }
                    },
                    networkApiVersion = "2022-07-01"
                }
            });

            // Resource overrides: override certain properties of the base profile for each VM created
            var vmOverride = HelperMethods.GenerateResourceOverrideItem(
                "sampleflexvm0",
                location,
                "Standard_D2ads_v5",
                "YourStr0ngP@ssword123!",
                "testUserName");
            resourceConfig.ResourceOverrides.Add(vmOverride);

            // Build the ExecuteCreateFlex request
            var correlationId = Guid.NewGuid().ToString();
            var request = new ExecuteCreateFlexContent(resourceConfig, executionParams)
            {
                CorrelationId = correlationId,
            };

            // Execute the create flex operation with polling and error handling
            Console.WriteLine("Calling ExecuteCreateFlex...");
            await ComputescheduleOperations.ExecuteCreateFlexOperation(
                completedOperations,
                executionParams,
                subscriptionResource,
                blockedOperationsException,
                request,
                location);
        }
    }
}
