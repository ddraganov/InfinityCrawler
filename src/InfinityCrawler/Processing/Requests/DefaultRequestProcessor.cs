using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace InfinityCrawler.Processing.Requests
{
	public class DefaultRequestProcessor : IRequestProcessor
	{
		private ILogger Logger { get; }
		private ConcurrentQueue<Uri> RequestQueue { get; } = new ConcurrentQueue<Uri>();
		private readonly IPlaywright _playwright;
		private readonly IBrowserContext _playwrightBrowserContext;

		public DefaultRequestProcessor(ILogger logger = null)
		{
			Logger = logger;
			_playwright = Playwright.CreateAsync().Result;
			IBrowser browser = _playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
			{
				Headless = false // Set to false if you want to debug with the browser UI
			}).Result;

			_playwrightBrowserContext = browser.NewContextAsync().Result;
		}

		public void Add(Uri uri)
		{
			RequestQueue.Enqueue(uri);
			PendingRequests++;
		}

		public int PendingRequests { get; private set; }

		public async Task ProcessAsync(Func<RequestResult, Task> responseAction, RequestProcessorOptions options, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(options);

			var random = new Random();
			var activeRequests = new ConcurrentDictionary<Task<RequestResult>, RequestContext>(options.MaxNumberOfSimultaneousRequests, options.MaxNumberOfSimultaneousRequests);

			var currentBackoff = 0;
			var successesSinceLastThrottle = 0;
			var requestCount = 0;

			while (!activeRequests.IsEmpty || !RequestQueue.IsEmpty)
			{
				cancellationToken.ThrowIfCancellationRequested();

				while (!RequestQueue.IsEmpty)
				{
					cancellationToken.ThrowIfCancellationRequested();

					if (RequestQueue.TryDequeue(out var requestUri))
					{
						var requestStartDelay = 0d;
						//Request delaying and backoff
						if (options.DelayBetweenRequestStart.TotalMilliseconds > 0)
						{
							requestStartDelay = options.DelayBetweenRequestStart.TotalMilliseconds;
							requestStartDelay += random.NextDouble() * options.DelayJitter.TotalMilliseconds;
						}

						requestStartDelay += currentBackoff;

						var requestContext = new RequestContext
						{
							RequestNumber = requestCount + 1,
							RequestUri = requestUri,
							Timer = new Stopwatch(),
							RequestStartDelay = requestStartDelay,
							RequestTimeout = options.RequestTimeout,
							CancellationToken = cancellationToken
						};

						Logger?.LogDebug("Request #{} ({}) starting with a {}ms delay.", requestContext.RequestNumber, requestUri, requestStartDelay);

						var task = PerformRequestAsync(requestContext);

						activeRequests.TryAdd(task, requestContext);
						requestCount++;

						if (activeRequests.Count == options.MaxNumberOfSimultaneousRequests)
						{
							break;
						}
					}
				}

				await Task.WhenAny(activeRequests.Keys).ConfigureAwait(false);

				cancellationToken.ThrowIfCancellationRequested();

				var completedRequests = activeRequests.Keys.Where(t => t.IsCompleted);
				foreach (var completedRequest in completedRequests)
				{
					activeRequests.TryRemove(completedRequest, out var requestContext);
					PendingRequests--;

					if (completedRequest.IsFaulted)
					{
						var aggregateException = completedRequest.Exception;
						
						//Keep the existing stack trace when re-throwing
						ExceptionDispatchInfo.Capture(aggregateException.InnerException).Throw();
					}

					await responseAction(completedRequest.Result);

					//Manage the throttling based on timeouts and successes
					var throttlePoint = options.TimeoutBeforeThrottle;
					if (throttlePoint.TotalMilliseconds > 0 && requestContext.Timer.Elapsed > throttlePoint)
					{
						successesSinceLastThrottle = 0;
						currentBackoff += (int)options.ThrottlingRequestBackoff.TotalMilliseconds;
						Logger?.LogInformation("Increased backoff to {}ms.", currentBackoff);
					}
					else if (currentBackoff > 0)
					{
						successesSinceLastThrottle += 1;
						if (successesSinceLastThrottle == options.MinSequentialSuccessesToMinimiseThrottling)
						{
							var newBackoff = currentBackoff - options.ThrottlingRequestBackoff.TotalMilliseconds;
							currentBackoff = Math.Max(0, (int)newBackoff);
							successesSinceLastThrottle = 0;
							Logger?.LogInformation("Decreased backoff to {}ms.", currentBackoff);
						}
					}
				}
			}

			Logger?.LogDebug("Completed processing {} requests.", requestCount);
		}

		private async Task<RequestResult> PerformRequestAsync(RequestContext context)
		{
			if (context.RequestStartDelay > 0)
			{
				await Task.Delay((int)context.RequestStartDelay);
			}

			var requestStart = DateTime.UtcNow;
			context.Timer.Start();

			try
			{
				var page = await _playwrightBrowserContext.NewPageAsync();

				// Navigate to the web page and wait for JavaScript to finish rendering
				IResponse response = await page.GotoAsync(
					context.RequestUri.ToString(), 
					new PageGotoOptions 
					{
						WaitUntil = WaitUntilState.DOMContentLoaded,
						Timeout = Convert.ToSingle(context.RequestTimeout.TotalMilliseconds)
					});

				// Scrape the rendered text from the page
				//var content = await page.EvaluateAsync<string>("document.body.innerText");
				var fileName = Guid.NewGuid().ToString();
				var content = await page.ContentAsync();
				File.WriteAllText("D:\\crawled-pages\\" + fileName + "_pl", content);
				//var timeoutToken = new CancellationTokenSource(context.RequestTimeout).Token;
				//var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeoutToken).Token;
				//using var response = await httpClient.GetAsync(context.RequestUri, combinedToken);
				//var contentStream = new MemoryStream();
				//await response.Content.CopyToAsync(contentStream);
				//File.WriteAllText("D:\\dfsgds\\" + fileName + "_nat", await response.Content.ReadAsStringAsync());
				//contentStream.Seek(0, SeekOrigin.Begin);

				//We only want to time the request, not the handling of the response
				context.Timer.Stop();

				context.CancellationToken.ThrowIfCancellationRequested();

				Logger?.LogDebug("Request #{} completed successfully in {}ms.", context.RequestNumber, context.Timer.ElapsedMilliseconds);

				return new RequestResult
				{
					RequestUri = context.RequestUri,
					RequestStart = requestStart,
					RequestStartDelay = context.RequestStartDelay,
					StatusCode = (HttpStatusCode)response.Status,
					Headers = response.Headers,
					Content = content,
					ElapsedTime = context.Timer.Elapsed
				};
			}
			catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
			{
				Logger?.LogDebug("Request #{} cancelled.", context.RequestNumber);
				return null;
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
			{
				context.Timer.Stop();

				Logger?.LogDebug("Request #{} completed with error in {}ms.", context.RequestNumber, context.Timer.ElapsedMilliseconds);
				Logger?.LogTrace(ex, "Request #{} Exception: {}", context.RequestNumber, ex.Message);

				return new RequestResult
				{
					RequestUri = context.RequestUri,
					RequestStart = requestStart,
					RequestStartDelay = context.RequestStartDelay,
					ElapsedTime = context.Timer.Elapsed,
					Exception = ex
				};
			}
		}
	}
}
