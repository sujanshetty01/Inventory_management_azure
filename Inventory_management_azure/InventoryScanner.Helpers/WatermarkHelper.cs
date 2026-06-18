using System;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace InventoryScanner.Helpers;

public static class WatermarkHelper
{
	public static DateTimeOffset GetWatermark(TableServiceClient tableServiceClient, ILogger logger)
	{
		try
		{
			string @string = tableServiceClient.GetTableClient("InventoryWatermarks").GetEntity<TableEntity>("scanner", "last_sync_date").Value.GetString("LastSyncDate");
			if (!string.IsNullOrEmpty(@string))
			{
				return DateTimeOffset.Parse(@string);
			}
		}
		catch (RequestFailedException ex) when (ex.Status == 404)
		{
			logger.LogInformation("No existing watermark found. Using default incremental window.");
		}
		catch (Exception ex2)
		{
			logger.LogWarning("Failed to fetch watermark from Table Storage: {Error}", ex2.Message);
		}
		return DateTimeOffset.UtcNow.AddDays(-30.0);
	}

	public static void UpdateWatermark(TableServiceClient tableServiceClient, DateTimeOffset syncDate, ILogger logger)
	{
		try
		{
			tableServiceClient.CreateTableIfNotExists("InventoryWatermarks");
			TableClient tableClient = tableServiceClient.GetTableClient("InventoryWatermarks");
			TableEntity entity = new TableEntity("scanner", "last_sync_date") { 
			{
				"LastSyncDate",
				syncDate.ToString("o")
			} };
			tableClient.UpsertEntity(entity);
			logger.LogInformation("Watermark updated to {SyncDate}", syncDate.ToString("o"));
		}
		catch (Exception ex)
		{
			logger.LogWarning("Failed to update watermark: {Error}", ex.Message);
		}
	}
}
