using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;
using InfinityCrawler.Internal;

namespace InfinityCrawler.Processing.Content
{
	public class DefaultContentProcessor : IContentProcessor
	{
		public CrawledContent Parse(Uri requestUri, Dictionary<string, string> headers, string content)
		{
			string contentTypeHeaderValue = headers.GetValueOrDefault("content-type");
			var contentTypeParts = contentTypeHeaderValue?.Split(';');
			var mediaType = contentTypeParts?[0].Trim();
			string charset = null;
			if (contentTypeHeaderValue != null && contentTypeHeaderValue.Contains("charset="))
			{
				foreach (var part in contentTypeParts)
				{
					if (part.Trim().StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
					{
						charset = part.Split('=')[1].Trim();
						break;
					}
				}
			}

			var crawledContent = new CrawledContent
			{
				ContentType = contentTypeHeaderValue?.Split(';')[0].Trim(),
				CharacterSet = charset,
				//ContentEncoding = headers.GetValueOrDefault("content-encoding")
			};

			var document = new HtmlDocument();
			document.LoadHtml(content);
			
			var pageRobotRules = new List<string>();
			if (headers.TryGetValue("X-Robots-Tag", out var value))
			{
				var robotsHeaderValues = value;
				pageRobotRules.AddRange(robotsHeaderValues.Split(","));
			}

			var metaNodes = document.DocumentNode.SelectNodes("html/head/meta");
			if (metaNodes != null)
			{
				var robotsMetaValue = metaNodes
					.Where(n => n.Attributes.Any(a => a.Name == "name" && a.Value.Equals("robots", StringComparison.InvariantCultureIgnoreCase)))
					.SelectMany(n => n.Attributes.Where(a => a.Name == "content").Select(a => a.Value))
					.FirstOrDefault();
				if (robotsMetaValue != null)
				{
					pageRobotRules.Add(robotsMetaValue);
				}
			}

			crawledContent.PageRobotRules = [.. pageRobotRules];
			crawledContent.CanonicalUri = GetCanonicalUri(document, requestUri);
			crawledContent.Links = GetLinks(document, requestUri).ToArray();

			return crawledContent;
		}
		
		private static string GetBaseHref(HtmlDocument document)
		{
			var baseNode = document.DocumentNode.SelectSingleNode("html/head/base");
			return baseNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;
		}

		private static Uri GetCanonicalUri(HtmlDocument document, Uri requestUri)
		{
			var linkNodes = document.DocumentNode.SelectNodes("html/head/link");
			if (linkNodes != null)
			{
				var canonicalNode = linkNodes
					.Where(n => n.Attributes.Any(a => a.Name == "rel" && a.Value.Equals("canonical", StringComparison.InvariantCultureIgnoreCase)))
					.FirstOrDefault();
				if (canonicalNode != null)
				{
					var baseHref = GetBaseHref(document);
					var canonicalHref = canonicalNode.GetAttributeValue("href", null);
					return requestUri.BuildUriFromHref(canonicalHref, baseHref);
				}
			}

			return null;
		}

		private static IEnumerable<CrawlLink> GetLinks(HtmlDocument document, Uri requestUri)
		{
			var anchorNodes = document.DocumentNode.SelectNodes("//a");
			if (anchorNodes != null)
			{
				var baseHref = GetBaseHref(document);
				
				foreach (var anchor in anchorNodes)
				{
					var href = anchor.GetAttributeValue("href", null);
					if (href == null)
					{
						continue;
					}

					var anchorLocation = requestUri.BuildUriFromHref(href, baseHref);
					if (anchorLocation == null)
					{
						//Invalid links are ignored
						continue;
					}

					if (anchorLocation.Scheme != Uri.UriSchemeHttp && anchorLocation.Scheme != Uri.UriSchemeHttps)
					{
						//Skip non-HTTP links
						continue;
					}

					yield return new CrawlLink
					{
						Location = anchorLocation,
						Title = anchor.GetAttributeValue("title", null),
						Text = anchor.InnerText,
						Relationship = anchor.GetAttributeValue("rel", null),
					};
				}
			}
		}
	}
}
