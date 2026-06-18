using System.Runtime.CompilerServices;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[CompilerGenerated]
internal class Program
{
	private static void _003CMain_003E_0024(string[] args)
	{
		new HostBuilder().ConfigureFunctionsWebApplication().ConfigureServices(delegate(IServiceCollection services)
		{
			services.AddApplicationInsightsTelemetryWorkerService();
			services.ConfigureFunctionsApplicationInsights();
		}).Build()
			.Run();
	}
}
