using YamlDotNet.Serialization;

namespace KartOrderService.ContractTests;

internal static class ContractLoader
{
    public static Dictionary<object, object> Load()
    {
        var yamlPath = Path.Combine(AppContext.BaseDirectory, "contracts", "api-contract.yaml");
        var yaml = File.ReadAllText(yamlPath);
        return new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(yaml);
    }
}
