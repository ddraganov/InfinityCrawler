﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InfinityCrawler.Processing.Requests;
using Microsoft.Extensions.Logging;
using TurnerSoftware.RobotsExclusionTools;

namespace InfinityCrawler.Internal
{
	internal class CrawlRunner
	{
		public Uri BaseUri { get; }
		public CrawlSettings Settings { get; }

		private RobotsFile RobotsFile { get; }

		private ILogger Logger { get; }

		private RobotsPageParser RobotsPageParser { get; }

		private ConcurrentDictionary<Uri, UriCrawlState> UriCrawlStates { get; } = new ConcurrentDictionary<Uri, UriCrawlState>();
		private ConcurrentDictionary<Uri, byte> SeenUris { get; } = new ConcurrentDictionary<Uri, byte>();
		private ConcurrentBag<CrawledUri> CrawledUris { get; } = [];

		public CrawlRunner(Uri baseUri, RobotsFile robotsFile,  CrawlSettings crawlSettings, ILogger logger = null)
		{
			BaseUri = baseUri;
			RobotsFile = robotsFile;
			Settings = crawlSettings;

			Logger = logger;
			RobotsPageParser = new RobotsPageParser();

			AddRequest(baseUri);
		}

		private static Uri StripFragment(Uri uri)
		{
			return new UriBuilder(uri)
			{
				Fragment = null
			}.Uri;
		}

		private void AddLink(CrawlLink crawlLink)
		{
			if (crawlLink.Relationship != null && crawlLink.Relationship.Equals("nofollow", StringComparison.InvariantCultureIgnoreCase))
			{
				return;
			}

			var uriWithoutFragment = StripFragment(crawlLink.Location);
			if (SeenUris.ContainsKey(uriWithoutFragment))
			{
				return;
			}

			AddRequest(uriWithoutFragment, false);
		}

		private void AddRedirect(Uri requestUri, Uri redirectUri)
		{
			if (UriCrawlStates.TryRemove(requestUri, out var crawlState))
			{
				var absoluteRedirectUri = new Uri(requestUri, redirectUri);
				absoluteRedirectUri = StripFragment(absoluteRedirectUri);

				var redirectCrawlState = new UriCrawlState
				{
					Location = absoluteRedirectUri,
					Redirects = crawlState.Redirects ?? []
				};
				redirectCrawlState.Redirects.Add(new CrawledUriRedirect
				{
					Location = crawlState.Location,
					Requests = crawlState.Requests
				});

				UriCrawlStates.TryAdd(redirectCrawlState.Location, redirectCrawlState);
				AddRequest(redirectCrawlState.Location, true);
			}
		}

		public void AddResult(Uri requestUri, CrawledContent content)
		{
			if (UriCrawlStates.TryGetValue(requestUri, out var crawlState))
			{
				var robotsPageDefinition = RobotsPageParser.FromRules(content.PageRobotRules);
				if (!robotsPageDefinition.CanIndex(Settings.UserAgent))
				{
					Logger?.LogDebug("Result content for {} has been blocked by an in-page Robots rule.", requestUri);
					AddResult(new CrawledUri
					{
						Location = crawlState.Location,
						Status = CrawlStatus.RobotsBlocked,
						Requests = crawlState.Requests,
						RedirectChain = crawlState.Redirects
					});
				}
				else
				{
					Logger?.LogDebug("Result for {} has completed successfully with content.", requestUri);

					AddResult(new CrawledUri
					{
						Location = crawlState.Location,
						Status = CrawlStatus.Crawled,
						RedirectChain = crawlState.Redirects,
						Requests = crawlState.Requests,
						Content = content
					});

					if (robotsPageDefinition.CanFollowLinks(Settings.UserAgent))
					{
						foreach (var crawlLink in content.Links)
						{
							AddLink(crawlLink);
						}
					}
				}
			}
		}

		public void AddRequest(Uri requestUri)
		{
			var uriWithoutFragment = StripFragment(requestUri);
			AddRequest(uriWithoutFragment, false);
		}

		private void AddRequest(Uri requestUri, bool skipMaxPageCheck)
		{
			if (Settings.HostAliases != null)
			{
				if (!(requestUri.Host == BaseUri.Host || Settings.HostAliases.Contains(requestUri.Host)))
				{
					Logger?.LogDebug("Request containing host {} is not in the list of allowed hosts. This request will be ignored.", requestUri.Host);
					return;
				}
			}
			else if (requestUri.Host != BaseUri.Host)
			{
				Logger?.LogDebug("Request containing host {} doesn't match the base host. This request will be ignored.", requestUri.Host);
				return;
			}

			if (!skipMaxPageCheck && Settings.MaxNumberOfPagesToCrawl > 0)
			{
				var expectedCrawlCount = CrawledUris.Count + Settings.RequestProcessor.PendingRequests;
				if (expectedCrawlCount == Settings.MaxNumberOfPagesToCrawl)
				{
					Logger?.LogDebug("Page crawl limit blocks adding request for {}. This request will be ignored.", requestUri);
					return;
				}
			}

			SeenUris.TryAdd(requestUri, 0);

			if (UriCrawlStates.TryGetValue(requestUri, out var crawlState))
			{
				var lastRequest = crawlState.Requests.LastOrDefault();
				if (lastRequest != null && lastRequest.IsSuccessfulStatus)
				{
					return;
				}

				if (crawlState.Requests.Count == Settings.NumberOfRetries)
				{
					Logger?.LogDebug("Request for {} has hit the maximum retry limit ({}).", requestUri, Settings.NumberOfRetries);
					AddResult(new CrawledUri
					{
						Location = crawlState.Location,
						Status = CrawlStatus.MaxRetries,
						Requests = crawlState.Requests,
						RedirectChain = crawlState.Redirects
					});
					return;
				}

				if (crawlState.Redirects != null && crawlState.Redirects.Count == Settings.MaxNumberOfRedirects)
				{
					Logger?.LogDebug("Request for {} has hit the maximum redirect limit ({}).", requestUri, Settings.MaxNumberOfRedirects);
					AddResult(new CrawledUri
					{
						Location = crawlState.Location,
						RedirectChain = crawlState.Redirects,
						Status = CrawlStatus.MaxRedirects
					});
					return;
				}
			}

			if (RobotsFile.IsAllowedAccess(requestUri, Settings.UserAgent))
			{
				Logger?.LogDebug("Added {} to request queue.", requestUri);
				Settings.RequestProcessor.Add(requestUri);
			}
			else
			{
				Logger?.LogDebug("Request for {} has been blocked by the Robots.txt file.", requestUri);
				AddResult(new CrawledUri
				{
					Location = requestUri,
					Status = CrawlStatus.RobotsBlocked
				});
			}
		}

		private void AddResult(CrawledUri result)
		{
			CrawledUris.Add(result);
		}

		public async Task<IEnumerable<CrawledUri>> ProcessAsync(
			Func<RequestResult, UriCrawlState, Task> responseSuccessAction,
			CancellationToken cancellationToken = default
		)
		{
			await Settings.RequestProcessor.ProcessAsync(
				async (requestResult) =>
				{
					var crawlState = UriCrawlStates.GetOrAdd(requestResult.RequestUri, new UriCrawlState
					{
						Location = requestResult.RequestUri
					});

					if (requestResult.Exception != null)
					{
						//Retry failed requests
						Logger?.LogDebug("An exception occurred while requesting {}. This URL will be added to the request queue to be attempted again later.", crawlState.Location);
						crawlState.Requests.Add(new CrawlRequest
						{
							RequestStart = requestResult.RequestStart,
							ElapsedTime = requestResult.ElapsedTime
						});
						AddRequest(requestResult.RequestUri);
					}
					else
					{
						var crawlRequest = new CrawlRequest
						{
							RequestStart = requestResult.RequestStart,
							ElapsedTime = requestResult.ElapsedTime,
							StatusCode = requestResult.StatusCode,
							IsSuccessfulStatus = (int)requestResult.StatusCode is >= 200 and <= 299
						};
						crawlState.Requests.Add(crawlRequest);

						var redirectStatusCodes = new[]
						{
							HttpStatusCode.MovedPermanently,
							HttpStatusCode.Redirect,
							HttpStatusCode.TemporaryRedirect
						};
						if (redirectStatusCodes.Contains(crawlRequest.StatusCode.Value))
						{
							string locationHeaderValue = requestResult.Headers.GetValueOrDefault("Location");
							Logger?.LogDebug("Result for {} was a redirect ({}). This URL will be added to the request queue.", crawlState.Location, locationHeaderValue);
							AddRedirect(crawlState.Location, new Uri(locationHeaderValue));
						}
						else if (crawlRequest.IsSuccessfulStatus)
						{
							await responseSuccessAction(requestResult, crawlState);
						}
						else if ((int)crawlRequest.StatusCode >= 500 && (int)crawlRequest.StatusCode <= 599)
						{
							//On server errors, try to crawl the page again later
							Logger?.LogDebug("Result for {} was unexpected ({}). This URL will be added to the request queue to be attempted again later.", crawlState.Location, crawlRequest.StatusCode);
							AddRequest(crawlState.Location);
						}
						else
						{
							//On any other error, just save what we have seen and move on
							//Consider the content of the request irrelevant
							Logger?.LogDebug("Result for {} was unexpected ({}). No further requests will be attempted.", crawlState.Location, crawlRequest.StatusCode);
							AddResult(new CrawledUri
							{
								Location = crawlState.Location,
								Status = CrawlStatus.Crawled,
								RedirectChain = crawlState.Redirects,
								Requests = crawlState.Requests
							});
						}
					}
				},
				Settings.RequestProcessorOptions,
				cancellationToken
			);

			Logger?.LogDebug("Completed crawling {} pages.", CrawledUris.Count);

			return [.. CrawledUris];
		}
	}
}
