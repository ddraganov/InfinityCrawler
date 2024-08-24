using System;
using System.Collections.Generic;

namespace InfinityCrawler.Processing.Content
{
	public interface IContentProcessor
	{
		CrawledContent Parse(Uri requestUri, Dictionary<string, string> headers, string content);
	}
}
