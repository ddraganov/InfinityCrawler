using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using InfinityCrawler.Internal;
using InfinityCrawler.Processing.Requests;
using Microsoft.Extensions.Logging;
using TurnerSoftware.RobotsExclusionTools;
using TurnerSoftware.SitemapTools;

namespace InfinityCrawler
{
	public class Crawler
	{
		private HttpClient HttpClient { get; }
		private ILogger Logger { get; }

		public Crawler(ILogger logger = null)
		{
			HttpClient = new HttpClient(new HttpClientHandler
			{
				AllowAutoRedirect = false,
				UseCookies = false
			});
			Logger = logger;
		}

		public Crawler(HttpClient httpClient, ILogger logger = null)
		{
			HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
			Logger = logger;
		}

		public async Task<CrawlResult> Crawl(Uri siteUri, CrawlSettings settings)
		{
			var result = new CrawlResult
			{
				CrawlStart = DateTime.UtcNow
			};
			var overallCrawlStopwatch = new Stopwatch();
			overallCrawlStopwatch.Start();

			var baseUri = new Uri(siteUri.GetLeftPart(UriPartial.Authority));
			var robotsFile = await new RobotsFileParser(HttpClient).FromUriAsync(baseUri);

			UpdateCrawlDelay(robotsFile, settings.UserAgent, settings.RequestProcessorOptions);

			var crawlRunner = new CrawlRunner(baseUri, robotsFile, settings, Logger);

			//Use any links referred to by the sitemap as a starting point
			var urisFromSitemap = (await new SitemapQuery(HttpClient)
				.GetAllSitemapsForDomainAsync(siteUri.Host))
				.SelectMany(s => s.Urls.Select(u => u.Location).Distinct());
			foreach (var uri in urisFromSitemap)
			{
				crawlRunner.AddRequest(uri);
			}

			result.CrawledUris = await crawlRunner.ProcessAsync(async (requestResult, crawlState) =>
			{
				Logger.LogInformation("Location: {}", crawlState.Location);
				CrawledContent content = settings.ContentProcessor.Parse(crawlState.Location, requestResult.Headers, requestResult.Content);
				content.RawContent = requestResult.Content;
				crawlRunner.AddResult(crawlState.Location, content);
			});

			overallCrawlStopwatch.Stop();
			result.ElapsedTime = overallCrawlStopwatch.Elapsed;
			return result;
		}

		private static void UpdateCrawlDelay(RobotsFile robotsFile, string userAgent, RequestProcessorOptions requestProcessorOptions)
		{
			var minimumCrawlDelayInMilliseconds = 0;

			//Apply Robots.txt crawl-delay (if defined)
			if (robotsFile.TryGetEntryForUserAgent(userAgent, out var accessEntry))
			{
				minimumCrawlDelayInMilliseconds = accessEntry.CrawlDelay ?? 0 * 1000;
			}

			var taskDelay = Math.Max(minimumCrawlDelayInMilliseconds, requestProcessorOptions.DelayBetweenRequestStart.TotalMilliseconds);
			requestProcessorOptions.DelayBetweenRequestStart = new TimeSpan(0, 0, 0, 0, (int)taskDelay);
		}
	}
}
