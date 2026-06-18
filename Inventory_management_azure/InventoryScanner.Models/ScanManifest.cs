using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InventoryScanner.Models;

public class ScanManifest
{
	[JsonPropertyName("run_id")]
	public string RunId { get; set; } = string.Empty;


	[JsonPropertyName("batch_index")]
	public int BatchIndex { get; set; }

	[JsonPropertyName("total_batches")]
	public int TotalBatches { get; set; }

	[JsonPropertyName("total_accounts_in_run")]
	public int TotalAccountsInRun { get; set; }

	[JsonPropertyName("accounts_in_batch")]
	public int AccountsInBatch { get; set; }

	[JsonPropertyName("accounts_succeeded")]
	public int AccountsSucceeded { get; set; }

	[JsonPropertyName("accounts_failed")]
	public int AccountsFailed { get; set; }

	[JsonPropertyName("accounts_succeeded_list")]
	public List<string> AccountsSucceededList { get; set; } = new List<string>();


	[JsonPropertyName("accounts_failed_list")]
	public List<string> AccountsFailedList { get; set; } = new List<string>();


	[JsonPropertyName("total_blobs_extracted")]
	public int TotalBlobsExtracted { get; set; }

	[JsonPropertyName("total_shares_extracted")]
	public int TotalSharesExtracted { get; set; }

	[JsonPropertyName("total_size_bytes")]
	public long TotalSizeBytes { get; set; }

	[JsonPropertyName("incremental_watermark")]
	public string IncrementalWatermark { get; set; } = string.Empty;


	[JsonPropertyName("scan_start_time")]
	public string ScanStartTime { get; set; } = string.Empty;


	[JsonPropertyName("scan_end_time")]
	public string ScanEndTime { get; set; } = string.Empty;


	[JsonPropertyName("scan_duration_seconds")]
	public double ScanDurationSeconds { get; set; }

	[JsonPropertyName("excluded_container_prefixes")]
	public List<string> ExcludedContainerPrefixes { get; set; } = new List<string>();


	[JsonPropertyName("warehouse_path")]
	public string WarehousePath { get; set; } = string.Empty;

}
