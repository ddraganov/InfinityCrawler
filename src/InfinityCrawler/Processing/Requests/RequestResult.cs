using System;
using System.Collections.Generic;
using System.Net;

namespace InfinityCrawler.Processing.Requests
{
	public class RequestResult
	{
		public Uri RequestUri { get; set; }
		public DateTime RequestStart { get; set; }
		public double RequestStartDelay { get; set; }
		public HttpStatusCode? StatusCode { get; set; }
		public Dictionary<string, string> Headers { get; set; }
		public string Content { get; set; }
		public TimeSpan ElapsedTime { get; set; }
		public Exception Exception { get; set; }
	}
}
