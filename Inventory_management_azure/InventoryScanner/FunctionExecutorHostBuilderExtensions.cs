using Microsoft.Azure.Functions.Worker.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InventoryScanner;

public static class FunctionExecutorHostBuilderExtensions
{
	public static IHostBuilder ConfigureGeneratedFunctionExecutor(this IHostBuilder builder)
	{
		return builder.ConfigureServices(delegate(IServiceCollection s)
		{
			s.AddSingleton<IFunctionExecutor, DirectFunctionExecutor>();
		});
	}
}
