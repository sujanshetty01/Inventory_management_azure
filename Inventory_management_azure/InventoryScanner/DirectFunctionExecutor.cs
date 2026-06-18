using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using InventoryScanner.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Context.Features;
using Microsoft.Azure.Functions.Worker.Invocation;

namespace InventoryScanner;

[EditorBrowsable(EditorBrowsableState.Never)]
[CompilerGenerated]
internal class DirectFunctionExecutor : IFunctionExecutor
{
	private readonly IFunctionActivator _functionActivator;

	private readonly Dictionary<string, Type> types = new Dictionary<string, Type>
	{
		{
			"InventoryScanner.Functions.StorageInventoryOrchestrator",
			Type.GetType("InventoryScanner.Functions.StorageInventoryOrchestrator, InventoryScanner, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")
		},
		{
			"InventoryScanner.Functions.StorageInventoryWorker",
			Type.GetType("InventoryScanner.Functions.StorageInventoryWorker, InventoryScanner, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")
		}
	};

	public DirectFunctionExecutor(IFunctionActivator functionActivator)
	{
		_functionActivator = functionActivator ?? throw new ArgumentNullException("functionActivator");
	}

	public async ValueTask ExecuteAsync(FunctionContext context)
	{
		object[] values = (await context.Features.Get<IFunctionInputBindingFeature>().BindFunctionInputAsync(context)).Values;
		if (string.Equals(context.FunctionDefinition.EntryPoint, "InventoryScanner.Functions.StorageInventoryOrchestrator.Run", StringComparison.Ordinal))
		{
			Type instanceType = types["InventoryScanner.Functions.StorageInventoryOrchestrator"];
			StorageInventoryOrchestrator obj = _functionActivator.CreateInstance(instanceType, context) as StorageInventoryOrchestrator;
			InvocationResult invocationResult = context.GetInvocationResult();
			invocationResult.Value = await obj.Run((HttpRequest)values[0]);
		}
		else if (string.Equals(context.FunctionDefinition.EntryPoint, "InventoryScanner.Functions.StorageInventoryWorker.Run", StringComparison.Ordinal))
		{
			Type instanceType2 = types["InventoryScanner.Functions.StorageInventoryWorker"];
			await (_functionActivator.CreateInstance(instanceType2, context) as StorageInventoryWorker).Run((string)values[0]);
		}
	}
}
