using Microsoft.Azure.Functions.Worker.Core.FunctionMetadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InventoryScanner;

public static class WorkerHostBuilderFunctionMetadataProviderExtension
{
	public static IHostBuilder ConfigureGeneratedFunctionMetadataProvider(this IHostBuilder builder)
	{
		builder.ConfigureServices(delegate(IServiceCollection s)
		{
			s.AddSingleton<IFunctionMetadataProvider, GeneratedFunctionMetadataProvider>();
		});
		return builder;
	}
}
