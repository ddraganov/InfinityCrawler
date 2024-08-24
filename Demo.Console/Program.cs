using InfinityCrawler;
using InfinityCrawler.Processing.Requests;
using Microsoft.Extensions.Logging;

namespace Demo.Console
{
	internal class Program
	{
		static async Task Main(string[] args)
		{
			var loggerFactory = LoggerFactory.Create(builder =>
			{
				builder
					.AddConsole();
			});
			var logger = loggerFactory.CreateLogger<Program>();
			logger.LogInformation("Example log message");

			var crawler = new Crawler(logger);
			var result = await crawler.Crawl(new Uri("https://playwright.dev"), new CrawlSettings
			{
				UserAgent = "MyVeryOwnWebCrawler/1.0",
				RequestProcessorOptions = new RequestProcessorOptions
				{
					MaxNumberOfSimultaneousRequests = 5,
				}
			});
		}
	}
}
