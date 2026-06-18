using System.ComponentModel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;

namespace InventoryScanner;

[EditorBrowsable(EditorBrowsableState.Never)]
public class FunctionMetadataProviderAutoStartup : IAutoConfigureStartup
{
	public void Configure(IHostBuilder hostBuilder)
	{
		hostBuilder.ConfigureGeneratedFunctionMetadataProvider();
	}
}
