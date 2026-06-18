using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InventoryScanner.Models;

public class BatchMessage
{
	[JsonPropertyName("run_id")]
	public string RunId { get; set; } = string.Empty;


	[JsonPropertyName("batch_index")]
	public int BatchIndex { get; set; }

	[JsonPropertyName("total_batches")]
	public int TotalBatches { get; set; } = 1;


	[JsonPropertyName("total_accounts_in_run")]
	public int TotalAccountsInRun { get; set; }

	[JsonPropertyName("accounts")]
	public List<StorageAccountInfo> Accounts { get; set; } = new List<StorageAccountInfo>();

}
