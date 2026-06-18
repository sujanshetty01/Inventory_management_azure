using System.Text.Json.Serialization;

namespace InventoryScanner.Models;

public class StorageAccountInfo
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;


	[JsonPropertyName("resourceGroup")]
	public string ResourceGroup { get; set; } = string.Empty;


	[JsonPropertyName("subscriptionId")]
	public string SubscriptionId { get; set; } = string.Empty;

}
