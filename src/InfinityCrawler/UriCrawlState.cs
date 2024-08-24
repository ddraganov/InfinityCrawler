using System;
using System.Collections.Generic;

namespace InfinityCrawler
{
	public class UriCrawlState
	{
		public Uri Location { get; set; }
		public IList<CrawlRequest> Requests { get; set; } = [];
		public IList<CrawledUriRedirect> Redirects { get; set; }
	}
}
