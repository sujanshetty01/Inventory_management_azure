namespace InventoryScanner.Helpers;

public static class Constants
{
	public static readonly string[] ExcludedContainerPrefixes = new string[7] { "azure-webjobs-", "app-package-", "scm-releases", "$logs", "$blobchangefeed", "$web", "$root" };

	public const string WatermarkTableName = "InventoryWatermarks";

	public const string WatermarkPartitionKey = "scanner";

	public const string WatermarkRowKey = "last_sync_date";

	public const int DefaultIncrementalDays = 30;

	public const int MaxRetries = 3;

	public const double InitialBackoffSeconds = 1.0;

	public const int BatchSize = 15;

	public const string DefaultWarehouseAccount = "stinvhost9922";

	public const string WarehouseContainer = "inventory-snapshots";

	public const string QueueName = "inventory-tasks";
}
