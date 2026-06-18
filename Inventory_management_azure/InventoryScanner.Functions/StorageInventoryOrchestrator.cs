using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Azure.ResourceManager.Resources;
using Azure.Storage.Queues;
using InventoryScanner.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace InventoryScanner.Functions;

public class StorageInventoryOrchestrator
{
	private readonly ILogger<StorageInventoryOrchestrator> _logger;

	public StorageInventoryOrchestrator(ILogger<StorageInventoryOrchestrator> logger)
	{
		_logger = logger;
	}

	[Function("storage_inventory_orchestrator")]
	public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, new string[] { "get", "post" }, Route = "start-inventory")] HttpRequest req)
	{
		_logger.LogInformation("Starting Enterprise Storage Inventory Discovery via HTTP Trigger...");
		try
		{
			DefaultAzureCredential credential = new DefaultAzureCredential();
			TenantResource tenantResource = new ArmClient(credential).GetTenants().First();
			string text = Environment.GetEnvironmentVariable("INVENTORY_SUBSCRIPTION_IDS") ?? "";
			List<string> list = (string.IsNullOrEmpty(text) ? new List<string>() : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
			_logger.LogInformation("Executing ARG Query to discover storage accounts...");
			ResourceQueryContent resourceQueryContent = new ResourceQueryContent("\n                Resources\n                | where type =~ 'microsoft.storage/storageaccounts'\n                | project name, resourceGroup, subscriptionId\n            ");
			if (list.Count > 0)
			{
				foreach (string item in list)
				{
					resourceQueryContent.Subscriptions.Add(item);
				}
			}
			List<StorageAccountInfo> list2 = (await tenantResource.GetResourcesAsync(resourceQueryContent)).Value.Data.ToObjectFromJson<List<StorageAccountInfo>>() ?? new List<StorageAccountInfo>();
			int totalAccounts = list2.Count;
			_logger.LogInformation("Discovery Complete: Found {TotalAccounts} storage accounts.", totalAccounts);
			if (totalAccounts == 0)
			{
				_logger.LogInformation("No storage accounts found. Exiting.");
				return new OkObjectResult("No storage accounts found.");
			}
			List<List<StorageAccountInfo>> batches = (from x in list2.Select((StorageAccountInfo account, int index) => new { account, index })
				group x by x.index / 15 into g
				select g.Select(x => x.account).ToList()).ToList();
			_logger.LogInformation("Splitting workload into {BatchCount} parallel batches.", batches.Count);
			string text2 = Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName") ?? throw new InvalidOperationException("AzureWebJobsStorage__accountName is missing.");
			QueueServiceClient queueServiceClient = new QueueServiceClient(new Uri("https://" + text2 + ".queue.core.windows.net"), (TokenCredential)credential, (QueueClientOptions)null);
			QueueClient queueClient = queueServiceClient.GetQueueClient("inventory-tasks");
			await queueClient.CreateIfNotExistsAsync();
			string runId = $"{DateTime.UtcNow:yyyyMMddTHHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
			for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
			{
				string messageText = JsonSerializer.Serialize(new BatchMessage
				{
					RunId = runId,
					BatchIndex = batchIndex,
					TotalBatches = batches.Count,
					TotalAccountsInRun = totalAccounts,
					Accounts = batches[batchIndex]
				});
				await queueClient.SendMessageAsync(messageText);
				_logger.LogInformation("Dispatched batch {BatchNum}/{TotalBatches} with {AccountCount} accounts.", batchIndex + 1, batches.Count, batches[batchIndex].Count);
			}
			_logger.LogInformation("Successfully dispatched {BatchCount} batches to the Worker Queue (run_id={RunId}).", batches.Count, runId);
			return new OkObjectResult(new
			{
				status = "dispatched",
				run_id = runId,
				total_accounts = totalAccounts,
				total_batches = batches.Count
			});
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Critical error in Orchestrator");
			return new ObjectResult(new
			{
				status = "error",
				message = "Internal server error. Check logs for details."
			})
			{
				StatusCode = 500
			};
		}
	}
}
