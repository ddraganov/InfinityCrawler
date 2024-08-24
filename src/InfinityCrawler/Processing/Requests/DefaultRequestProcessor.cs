﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
				Headless = true // Set to false if you want to debug with the browser UI
			}).Result;

			_playwrightBrowserContext = browser.NewContextAsync().Result;
		}

		public void Add(Uri uri)
		{
			RequestQueue.Enqueue(uri);
			PendingRequests++;
		}

		public int PendingRequests { get; private set; }

		public async Task ProcessAsync(HttpClient httpClient, Func<RequestResult, Task> responseAction, RequestProcessorOptions options, CancellationToken cancellationToken = default)
		{
			if (options == null)
			{
				throw new ArgumentNullException(nameof(options));
			}

			var random = new Random();
			var activeRequests = new ConcurrentDictionary<Task<RequestResult>, RequestContext>(options.MaxNumberOfSimultaneousRequests, options.MaxNumberOfSimultaneousRequests);

			var currentBackoff = 0;
			var successesSinceLastThrottle = 0;
			var requestCount = 0;

			while (activeRequests.Count > 0 || !RequestQueue.IsEmpty)
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

						Logger?.LogDebug($"Request #{requestContext.RequestNumber} ({requestUri}) starting with a {requestStartDelay}ms delay.");

						var task = PerformRequestAsync(httpClient, requestContext);

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
						Logger?.LogInformation($"Increased backoff to {currentBackoff}ms.");
					}
					else if (currentBackoff > 0)
					{
						successesSinceLastThrottle += 1;
						if (successesSinceLastThrottle == options.MinSequentialSuccessesToMinimiseThrottling)
						{
							var newBackoff = currentBackoff - options.ThrottlingRequestBackoff.TotalMilliseconds;
							currentBackoff = Math.Max(0, (int)newBackoff);
							successesSinceLastThrottle = 0;
							Logger?.LogInformation($"Decreased backoff to {currentBackoff}ms.");
						}
					}
				}
			}

			Logger?.LogDebug($"Completed processing {requestCount} requests.");
		}

		private async Task<RequestResult> PerformRequestAsync(HttpClient httpClient, RequestContext context)
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
				string fileName = Guid.NewGuid().ToString();
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

				Logger?.LogDebug($"Request #{context.RequestNumber} completed successfully in {context.Timer.ElapsedMilliseconds}ms.");

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
				Logger?.LogDebug($"Request #{context.RequestNumber} cancelled.");
				return null;
			}
			catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
			{
				context.Timer.Stop();

				Logger?.LogDebug($"Request #{context.RequestNumber} completed with error in {context.Timer.ElapsedMilliseconds}ms.");
				Logger?.LogTrace(ex, $"Request #{context.RequestNumber} Exception: {ex.Message}");

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
