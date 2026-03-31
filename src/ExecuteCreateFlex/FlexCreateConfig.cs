using DotNetEnv;

namespace ExecuteCreateFlex;

/// <summary>
/// Holds all configuration values needed to run the ExecuteCreateFlex operation,
/// loaded from the .env file at startup.
/// </summary>
internal record FlexCreateConfig(
    string Location,
    string SubscriptionId,
    string ResourceGroupName,
    string VnetName,
    string SubnetName,
    string VmPrefix,
    string VmAdminUsername,
    string VmAdminPassword)
{
    /// <summary>
    /// Loads configuration from the .env file and returns a populated <see cref="FlexCreateConfig"/>.
    /// </summary>
    public static FlexCreateConfig Load()
    {
        Env.Load();

        return new FlexCreateConfig(
            Location: Env.GetString("AZURE_LOCATION"),
            SubscriptionId: Env.GetString("AZURE_SUBSCRIPTION_ID"),
            ResourceGroupName: Env.GetString("AZURE_RESOURCE_GROUP"),
            VnetName: Env.GetString("AZURE_VNET_NAME"),
            SubnetName: Env.GetString("AZURE_SUBNET_NAME"),
            VmPrefix: Env.GetString("AZURE_VM_PREFIX"),
            VmAdminUsername: Env.GetString("AZURE_VM_ADMIN_USERNAME"),
            VmAdminPassword: Env.GetString("AZURE_VM_ADMIN_PASSWORD"));
    }
}
