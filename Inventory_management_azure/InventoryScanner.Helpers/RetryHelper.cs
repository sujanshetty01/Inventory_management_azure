using System;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;

namespace InventoryScanner.Helpers;

public static class RetryHelper
{
	public static async Task<T> RetryWithBackoffAsync<T>(Func<Task<T>> operation, string description, ILogger logger, int maxRetries = 3)
	{
		Exception lastException = null;
		for (int attempt = 0; attempt < maxRetries; attempt++)
		{
			try
			{
				return await operation();
			}
			catch (RequestFailedException ex)
			{
				lastException = ex;
				int status = ex.Status;
				bool flag = ((status == 429 || status == 500 || (uint)(status - 502) <= 2u) ? true : false);
				if (flag || (ex.ErrorCode?.Contains("Throttling", StringComparison.OrdinalIgnoreCase) ?? false))
				{
					TimeSpan delay = TimeSpan.FromSeconds(1.0 * Math.Pow(2.0, attempt));
					logger.LogWarning("Retry {Attempt}/{MaxRetries} for '{Description}' after {Backoff}s (status={Status}, error={ErrorCode})", attempt + 1, maxRetries, description, delay.TotalSeconds, ex.Status, ex.ErrorCode);
					await Task.Delay(delay);
					continue;
				}
				throw;
			}
		}
		throw lastException;
	}

	public static async Task<T> RetryWithBackoffAsync<T>(Func<T> operation, string description, ILogger logger, int maxRetries = 3)
	{
		Func<T> operation2 = operation;
		return await RetryWithBackoffAsync(() => Task.FromResult(operation2()), description, logger, maxRetries);
	}
}
