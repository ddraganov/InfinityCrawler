using System;
using System.Threading;
using System.Threading.Tasks;

namespace InfinityCrawler.Processing.Requests
{
	public interface IRequestProcessor
	{
		void Add(Uri requestUri);

		int PendingRequests { get; }

		Task ProcessAsync(
			Func<RequestResult, Task> responseAction,
			RequestProcessorOptions options, 
			CancellationToken cancellationToken = default
		);
	}
}
