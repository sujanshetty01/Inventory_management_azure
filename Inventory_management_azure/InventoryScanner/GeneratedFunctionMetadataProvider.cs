using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker.Core.FunctionMetadata;

namespace InventoryScanner;

[EditorBrowsable(EditorBrowsableState.Never)]
[CompilerGenerated]
public class GeneratedFunctionMetadataProvider : IFunctionMetadataProvider
{
	public Task<ImmutableArray<IFunctionMetadata>> GetFunctionMetadataAsync(string directory)
	{
		List<IFunctionMetadata> list = new List<IFunctionMetadata>();
		List<string> rawBindings = new List<string> { "{\"name\":\"req\",\"type\":\"httpTrigger\",\"direction\":\"In\",\"authLevel\":\"Function\",\"methods\":[\"get\",\"post\"],\"route\":\"start-inventory\"}", "{\"name\":\"$return\",\"type\":\"http\",\"direction\":\"Out\"}" };
		DefaultFunctionMetadata item = new DefaultFunctionMetadata
		{
			Language = "dotnet-isolated",
			Name = "storage_inventory_orchestrator",
			EntryPoint = "InventoryScanner.Functions.StorageInventoryOrchestrator.Run",
			RawBindings = rawBindings,
			ScriptFile = "InventoryScanner.dll"
		};
		list.Add(item);
		List<string> rawBindings2 = new List<string> { "{\"name\":\"messageText\",\"type\":\"queueTrigger\",\"direction\":\"In\",\"queueName\":\"inventory-tasks\",\"connection\":\"AzureWebJobsStorage\",\"dataType\":\"String\"}" };
		DefaultFunctionMetadata item2 = new DefaultFunctionMetadata
		{
			Language = "dotnet-isolated",
			Name = "storage_inventory_worker",
			EntryPoint = "InventoryScanner.Functions.StorageInventoryWorker.Run",
			RawBindings = rawBindings2,
			ScriptFile = "InventoryScanner.dll"
		};
		list.Add(item2);
		return Task.FromResult(list.ToImmutableArray());
	}
}
