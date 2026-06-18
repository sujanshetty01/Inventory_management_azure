using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using InventoryScanner.Helpers;
using InventoryScanner.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace InventoryScanner.Functions;

public class StorageInventoryWorker
{
	private readonly ILogger<StorageInventoryWorker> _logger;

	public StorageInventoryWorker(ILogger<StorageInventoryWorker> logger)
	{
		_logger = logger;
	}

	[Function("storage_inventory_worker")]
	public async Task Run([QueueTrigger("inventory-tasks", Connection = "AzureWebJobsStorage")] string messageText)
	{
		_logger.LogInformation("Worker Instance Started. Processing a batch of storage accounts...");
		DateTimeOffset scanStartTime = DateTimeOffset.UtcNow;
		try
		{
			BatchMessage batchMessage = JsonSerializer.Deserialize<BatchMessage>(messageText) ?? throw new InvalidOperationException("Failed to deserialize queue message.");
			string runId = (string.IsNullOrEmpty(batchMessage.RunId) ? Guid.NewGuid().ToString("N").Substring(0, 8) : batchMessage.RunId);
			int batchIndex = batchMessage.BatchIndex;
			int totalBatches = batchMessage.TotalBatches;
			int totalAccountsInRun = batchMessage.TotalAccountsInRun;
			List<StorageAccountInfo> batch = batchMessage.Accounts;
			DefaultAzureCredential credential = new DefaultAzureCredential();
			List<Dictionary<string, object?>> inventoryBlobs = new List<Dictionary<string, object>>();
			List<Dictionary<string, object?>> inventoryShares = new List<Dictionary<string, object>>();
			List<Dictionary<string, object?>> scanErrors = new List<Dictionary<string, object>>();
			List<string> accountsSucceeded = new List<string>();
			string warehouseAccount = Environment.GetEnvironmentVariable("WAREHOUSE_ACCOUNT") ?? "stinvhost9922";
			string uriString = "https://" + warehouseAccount + ".table.core.windows.net";
			TableServiceClient tableService = new TableServiceClient(new Uri(uriString), credential);
			DateTimeOffset lastSyncDate = WatermarkHelper.GetWatermark(tableService, _logger);
			_logger.LogInformation("Incremental Scan Mode: Only scanning blobs modified AFTER {LastSyncDate}", lastSyncDate);
			foreach (StorageAccountInfo item2 in batch)
			{
				string accountName2 = item2.Name;
				string accountSub2 = (string.IsNullOrEmpty(item2.SubscriptionId) ? "unknown" : item2.SubscriptionId);
				string accountRg2 = (string.IsNullOrEmpty(item2.ResourceGroup) ? "unknown" : item2.ResourceGroup);
				_logger.LogInformation("Scanning Storage Account: {AccountName}", accountName2);
				try
				{
					string uriString2 = "https://" + accountName2 + ".blob.core.windows.net";
					BlobServiceClient blobServiceClient = new BlobServiceClient(new Uri(uriString2), (TokenCredential)credential, (BlobClientOptions)null);
					foreach (BlobContainerItem container in await RetryHelper.RetryWithBackoffAsync(delegate
					{
						List<BlobContainerItem> list3 = new List<BlobContainerItem>();
						foreach (BlobContainerItem blobContainer in blobServiceClient.GetBlobContainers())
						{
							list3.Add(blobContainer);
						}
						return list3;
					}, "list_containers(" + accountName2 + ")", _logger))
					{
						if (Constants.ExcludedContainerPrefixes.Any((string prefix) => container.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
						{
							_logger.LogInformation("  Skipping infrastructure container: {ContainerName}", container.Name);
							continue;
						}
						BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(container.Name);
						foreach (BlobItem item3 in await RetryHelper.RetryWithBackoffAsync(delegate
						{
							List<BlobItem> list2 = new List<BlobItem>();
							foreach (BlobItem blob in containerClient.GetBlobs(BlobTraits.Tags, BlobStates.Snapshots | BlobStates.Deleted | BlobStates.Version))
							{
								list2.Add(blob);
							}
							return list2;
						}, $"list_blobs({accountName2}/{container.Name})", _logger))
						{
							if (!item3.Properties.LastModified.HasValue || !(item3.Properties.LastModified.Value < lastSyncDate))
							{
								string value = ((item3.Properties.ContentHash != null) ? Convert.ToHexString(item3.Properties.ContentHash).ToLowerInvariant() : null);
								inventoryBlobs.Add(new Dictionary<string, object>
								{
									["SubscriptionId"] = accountSub2,
									["ResourceGroup"] = accountRg2,
									["StorageAccount"] = accountName2,
									["ContainerName"] = container.Name,
									["BlobName"] = item3.Name,
									["BlobType"] = item3.Properties.BlobType?.ToString(),
									["ContentType"] = item3.Properties.ContentType,
									["ContentMD5"] = value,
									["IsDeleted"] = item3.Deleted,
									["Size_Bytes"] = item3.Properties.ContentLength,
									["CreationTime"] = item3.Properties.CreatedOn?.ToString("yyyy-MM-dd HH:mm:ss"),
									["LastModified"] = item3.Properties.LastModified?.ToString("yyyy-MM-dd HH:mm:ss"),
									["BlobTier"] = item3.Properties.AccessTier?.ToString(),
									["VersionId"] = item3.VersionId,
									["IsCurrentVersion"] = item3.IsLatestVersion,
									["Snapshot"] = item3.Snapshot,
									["TagCount"] = item3.Tags?.Count
								});
							}
						}
					}
					try
					{
						string uriString3 = "https://" + accountName2 + ".file.core.windows.net";
						ShareServiceClient shareServiceClient = new ShareServiceClient(new Uri(uriString3), (TokenCredential)credential, new ShareClientOptions
						{
							ShareTokenIntent = ShareTokenIntent.Backup
						});
						foreach (ShareItem item4 in await RetryHelper.RetryWithBackoffAsync(delegate
						{
							List<ShareItem> list = new List<ShareItem>();
							foreach (ShareItem share in shareServiceClient.GetShares())
							{
								list.Add(share);
							}
							return list;
						}, "list_shares(" + accountName2 + ")", _logger))
						{
							inventoryShares.Add(new Dictionary<string, object>
							{
								["SubscriptionId"] = accountSub2,
								["ResourceGroup"] = accountRg2,
								["StorageAccount"] = accountName2,
								["FileShareName"] = item4.Name,
								["Quota_GB"] = item4.Properties?.QuotaInGB,
								["AccessTier"] = item4.Properties?.AccessTier
							});
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning("Could not scan file shares for {AccountName}: {Error}", accountName2, ex.Message);
					}
					accountsSucceeded.Add(accountName2);
				}
				catch (Exception ex2)
				{
					Dictionary<string, object> item = new Dictionary<string, object>
					{
						["StorageAccount"] = accountName2,
						["SubscriptionId"] = accountSub2,
						["ResourceGroup"] = accountRg2,
						["ErrorType"] = ex2.GetType().Name,
						["ErrorMessage"] = ((ex2.Message.Length > 500) ? ex2.Message.Substring(0, 500) : ex2.Message),
						["Timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
					};
					scanErrors.Add(item);
					_logger.LogWarning("Failed to scan account {AccountName}: {Error}", accountName2, ex2.Message);
				}
			}
			_logger.LogInformation("Batch {BatchNum}/{TotalBatches} complete. Extracted {BlobCount} newly modified blobs, {ShareCount} file shares, {SuccessCount} accounts succeeded, {ErrorCount} accounts failed.", batchIndex + 1, totalBatches, inventoryBlobs.Count, inventoryShares.Count, accountsSucceeded.Count, scanErrors.Count);
			List<Dictionary<string, object?>> stats = new List<Dictionary<string, object>>();
			if (inventoryBlobs.Count > 0)
			{
				stats = (from b in inventoryBlobs
					group b by new
					{
						StorageAccount = (b["StorageAccount"]?.ToString() ?? ""),
						ContainerName = (b["ContainerName"]?.ToString() ?? "")
					} into g
					select new Dictionary<string, object>
					{
						["StorageAccount"] = g.Key.StorageAccount,
						["ContainerName"] = g.Key.ContainerName,
						["TotalBlobs"] = g.Count(),
						["TotalSizeBytes"] = g.Sum(delegate(Dictionary<string, object> b)
						{
							object obj2 = b["Size_Bytes"];
							if (obj2 is long result2)
							{
								return result2;
							}
							return (obj2 is int num2) ? num2 : 0;
						})
					}).ToList();
				_logger.LogInformation("Storage Utilization Statistics: {Stats}", JsonSerializer.Serialize(stats, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
			}
			try
			{
				BlobServiceClient blobServiceClient2 = new BlobServiceClient(new Uri("https://" + warehouseAccount + ".blob.core.windows.net"), (TokenCredential)credential, (BlobClientOptions)null);
				BlobContainerClient warehouseContainerClient = blobServiceClient2.GetBlobContainerClient("inventory-snapshots");
				await warehouseContainerClient.CreateIfNotExistsAsync();
				string accountRg2 = DateTimeOffset.UtcNow.ToString("yyyy/MM/dd");
				string accountSub2 = $"{runId}_batch{batchIndex}";
				await UploadJsonAsync(warehouseContainerClient, inventoryBlobs, accountRg2 + "/blobs_snapshot_" + accountSub2 + ".json", _logger);
				await UploadJsonAsync(warehouseContainerClient, inventoryShares, accountRg2 + "/shares_snapshot_" + accountSub2 + ".json", _logger);
				if (scanErrors.Count > 0)
				{
					await UploadJsonAsync(warehouseContainerClient, scanErrors, accountRg2 + "/scan_errors_" + accountSub2 + ".json", _logger);
				}
				if (stats.Count > 0)
				{
					await UploadExcelAsync(warehouseContainerClient, stats, accountRg2 + "/utilization_stats_" + accountSub2 + ".xlsx", _logger);
					await UploadExcelAsync(warehouseContainerClient, inventoryBlobs, accountRg2 + "/blobs_report_" + accountSub2 + ".xlsx", _logger);
				}
				DateTimeOffset utcNow = DateTimeOffset.UtcNow;
				ScanManifest value2 = new ScanManifest
				{
					RunId = runId,
					BatchIndex = batchIndex,
					TotalBatches = totalBatches,
					TotalAccountsInRun = totalAccountsInRun,
					AccountsInBatch = batch.Count,
					AccountsSucceeded = accountsSucceeded.Count,
					AccountsFailed = scanErrors.Count,
					AccountsSucceededList = accountsSucceeded,
					AccountsFailedList = scanErrors.Select((Dictionary<string, object> e) => e["StorageAccount"]?.ToString() ?? "unknown").ToList(),
					TotalBlobsExtracted = inventoryBlobs.Count,
					TotalSharesExtracted = inventoryShares.Count,
					TotalSizeBytes = inventoryBlobs.Sum(delegate(Dictionary<string, object> b)
					{
						object obj = b["Size_Bytes"];
						if (obj is long result)
						{
							return result;
						}
						return (obj is int num) ? num : 0;
					}),
					IncrementalWatermark = lastSyncDate.ToString("o"),
					ScanStartTime = scanStartTime.ToString("o"),
					ScanEndTime = utcNow.ToString("o"),
					ScanDurationSeconds = Math.Round((utcNow - scanStartTime).TotalSeconds, 2),
					ExcludedContainerPrefixes = Constants.ExcludedContainerPrefixes.ToList(),
					WarehousePath = $"{warehouseAccount}/{"inventory-snapshots"}/{accountRg2}/"
				};
				string accountName2 = accountRg2 + "/scan_manifest_" + accountSub2 + ".json";
				BlobClient blobClient = warehouseContainerClient.GetBlobClient(accountName2);
				string manifestJson = JsonSerializer.Serialize(value2, new JsonSerializerOptions
				{
					WriteIndented = true
				});
				await blobClient.UploadAsync(new BinaryData(manifestJson), overwrite: true);
				_logger.LogInformation("Uploaded Scan Manifest: {ManifestBlobName}", accountName2);
				_logger.LogInformation("Worker successfully pushed all partitioned data to Warehouse ({WarehouseAccount}/{Container}). Manifest: {Manifest}", warehouseAccount, "inventory-snapshots", manifestJson);
			}
			catch (Exception ex3)
			{
				_logger.LogError("Failed to push to Warehouse: {Error}", ex3.Message);
			}
			if (accountsSucceeded.Count > 0)
			{
				WatermarkHelper.UpdateWatermark(tableService, scanStartTime, _logger);
			}
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Critical error in Worker");
			throw;
		}
	}

	private static async Task UploadJsonAsync(BlobContainerClient containerClient, List<Dictionary<string, object?>> data, string blobName, ILogger logger)
	{
		BlobClient blobClient = containerClient.GetBlobClient(blobName);
		string data2 = ((data.Count > 0) ? JsonSerializer.Serialize(data) : "[]");
		await blobClient.UploadAsync(new BinaryData(data2), overwrite: true);
		logger.LogInformation("Uploaded JSON Snapshot: {BlobName} ({RecordCount} records)", blobName, data.Count);
	}

	private static async Task UploadExcelAsync(BlobContainerClient containerClient, List<Dictionary<string, object?>> data, string blobName, ILogger logger)
	{
		if (data.Count == 0)
		{
			return;
		}
		using MemoryStream excelStream = ExcelHelper.ToExcelStream(data);
		await containerClient.GetBlobClient(blobName).UploadAsync(excelStream, overwrite: true);
		logger.LogInformation("Uploaded Excel Report: {BlobName} ({RecordCount} records)", blobName, data.Count);
	}
}
