using System;
using System.IO;

namespace InfinityCrawler.Processing.Content
{
	public interface IContentProcessor
	{
		CrawledContent Parse(Uri requestUri, CrawlHeaders headers, Stream contentStream);
	}
}
