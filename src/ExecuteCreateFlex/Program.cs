using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ComputeSchedule.Models;
using Azure.ResourceManager.Resources;
using DotNetEnv;
using UtilityMethods;

namespace ExecuteCreateFlex
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var blockedOperationsException = new HashSet<string> { "SchedulingOperationsBlockedException", "NonSchedulingOperationsBlockedException" };

            // Load .env file from the project directory
            Env.Load();

            // Location: The location of the virtual machines
            string location = Env.GetString("AZURE_LOCATION");

            // SubscriptionId: The subscription id under which the virtual machines are located
            string subscriptionId = Env.GetString("AZURE_SUBSCRIPTION_ID");

            // ResourceGroupName: The resource group name under which the virtual machines are located
            string resourceGroupName = Env.GetString("AZURE_RESOURCE_GROUP");

            // VNet and subnet names used to provision the network before VM creation
            string vnetName = Env.GetString("AZURE_VNET_NAME");
            string subnetName = Env.GetString("AZURE_SUBNET_NAME");

            // VM prefix used for resource naming, and admin credentials for created VMs
            string vmPrefix = Env.GetString("AZURE_VM_PREFIX");
            string vmAdminUsername = Env.GetString("AZURE_VM_ADMIN_USERNAME");
            string vmAdminPassword = Env.GetString("AZURE_VM_ADMIN_PASSWORD");

            Dictionary<string, ResourceOperationDetails> completedOperations = [];

            // Credential: The Azure credential used to authenticate the request
            TokenCredential cred = new DefaultAzureCredential();

            // Standard client for general ARM operations (resource group, VNet)
            ArmClient standardClient = new(cred);
            var standardSubscriptionResource = HelperMethods.GetSubscriptionResource(standardClient, subscriptionId);
            ResourceGroupResource resourceGroupResource = await standardSubscriptionResource.GetResourceGroupAsync(resourceGroupName);

            /*
             * Before creating a virtual machine, a virtual network and subnet must be created in the resource group.
             * A separate client with the network API version pinned is used for VNet creation.
             */
            var vnetClientOptions = new ArmClientOptions();
            vnetClientOptions.SetApiVersion(new ResourceType("Microsoft.Network/virtualNetworks"), "2025-03-01");
            ArmClient vnetClient = new(cred, subscriptionId, vnetClientOptions);
            var vnet = await HelperMethods.CreateVirtualNetwork(resourceGroupResource, subnetName, vnetName, location, vnetClient);
            var subnet = HelperMethods.GetSubnetId(vnet);

            // Regional client for ComputeSchedule operations
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
                ResourcePrefix = vmPrefix,
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
                                            subnet = new { id = subnet.ToString() },
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
                $"{vmPrefix}vm0",
                location,
                "Standard_D2ads_v5",
                vmAdminPassword,
                vmAdminUsername);
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
