using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Web;
using System.Linq;
using System.Net.Http;
using HtmlAgilityPack;
using Shared.Engine.CORE;
using Lampac.Engine;
using Shared.Model.Templates;
using Uaflix.Models.UaFlix;
using System.Text.RegularExpressions;

namespace Uaflix.Controllers
{
    public class Controller : BaseController
    {
        ProxyManager proxyManager = new ProxyManager(ModInit.UaFlix);
        static HttpClient httpClient = new HttpClient();

        [HttpGet]
        [Route("uaflix")]
        async public Task<ActionResult> Index(long id, string imdb_id, long kinopoisk_id, string title, string original_title, string original_language, int year, string source, int serial, string account_email, string t, int s = -1, bool rjson = false)
        {
            var init = ModInit.UaFlix;
            if (!init.enable)
                return Forbid();

            var proxy = proxyManager.Get();
            var result = await search(imdb_id, kinopoisk_id, title, original_title, serial);

            if (result == null)
            {
                proxyManager.Refresh();
                return Content("Uaflix", "text/html; charset=utf-8");
            }

            if (result.movie != null)
            {
                var tpl = new MovieTpl(title, original_title, result.movie.Count);

                foreach (var movie in result.movie)
                {
                    var streamquality = new StreamQualityTpl();
                    foreach (var item in movie.links)
                        streamquality.Append(HostStreamProxy(ModInit.UaFlix, item.link, proxy: proxy), item.quality);

                    tpl.Append(
                        movie.translation,
                        streamquality.Firts().link,
                        streamquality: streamquality,
                        subtitles: movie.subtitles
                    );
                }

                return rjson
                    ? Content(tpl.ToJson(), "application/json; charset=utf-8")
                    : Content(tpl.ToHtml(), "text/html; charset=utf-8");
            }

            return Content("Uaflix", "text/html; charset=utf-8");
        }

        async ValueTask<Result> search(string imdb_id, long kinopoisk_id, string title, string original_title, int serial)
        {
            string memKey = $"UaFlix:view:{kinopoisk_id}:{imdb_id}";
            if (!hybridCache.TryGetValue(memKey, out Result res))
            {
                try
                {
                    string filmTitle = !string.IsNullOrEmpty(title) ? title : original_title;
                    string searchUrl = $"https://uafix.net/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(filmTitle)}";

                    httpClient.DefaultRequestHeaders.Clear();
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                    httpClient.DefaultRequestHeaders.Add("Referer", "https://uafix.net/");

                    var searchHtml = await httpClient.GetStringAsync(searchUrl);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(searchHtml);

                    var filmNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'sres-wrap')]");
                    if (filmNode == null) return null;

                    string filmUrl = filmNode.GetAttributeValue("href", "");
                    if (!filmUrl.StartsWith("http"))
                        filmUrl = "https://uafix.net" + filmUrl;

                    var filmHtml = await httpClient.GetStringAsync(filmUrl);
                    doc.LoadHtml(filmHtml);

                    var iframeNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'video-box')]/iframe");
                    if (iframeNodes == null || !iframeNodes.Any()) return null;

                    var movies = new List<Movie>();

                    foreach (var iframe in iframeNodes)
                    {
                        string iframeUrl = iframe.GetAttributeValue("src", "");
                        if (string.IsNullOrEmpty(iframeUrl)) continue;

                        if (iframeUrl.Contains("zetvideo.net"))
                        {
                            var zlinks = await ParseAllZetvideoSources(iframeUrl);
                            foreach (var l in zlinks)
                                movies.Add(new Movie
                                {
                                    translation = $"{filmTitle} (Zetvideo)",
                                    links = new List<(string, string)> { (l.link, l.quality) },
                                    subtitles = null
                                });
                        }
                    }

                    if (movies.Count > 0)
                    {
                        res = new Result()
                        {
                            movie = movies
                        };
                        hybridCache.Set(memKey, res, cacheTime(5));
                        proxyManager.Success();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UaFlix error: {ex.Message}");
                }
            }
            return res;
        }

        async Task<List<(string link, string quality)>> ParseAllZetvideoSources(string iframeUrl)
        {
            var result = new List<(string link, string quality)>();
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                request.Headers.Add("User-Agent", "Mozilla/5.0");
                var response = await httpClient.SendAsync(request);
                var html = await response.Content.ReadAsStringAsync();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var sourceNodes = doc.DocumentNode.SelectNodes("//source[contains(@src, '.m3u8')]");
                if (sourceNodes != null)
                {
                    foreach (var node in sourceNodes)
                    {
                        var url = node.GetAttributeValue("src", null);
                        var label = node.GetAttributeValue("label", null) ?? node.GetAttributeValue("res", null) ?? "1080p";
                        if (!string.IsNullOrEmpty(url))
                            result.Add((url, label));
                    }
                }

                if (result.Count == 0)
                {
                    var scriptNodes = doc.DocumentNode.SelectNodes("//script");
                    if (scriptNodes != null)
                    {
                        foreach (var script in scriptNodes)
                        {
                            var text = script.InnerText;
                            var urls = Regex.Matches(text, @"https?:\/\/[^\s'""]+\.m3u8")
                                .Cast<Match>()
                                .Select(m => m.Value)
                                .Distinct();
                            foreach (var url in urls)
                                result.Add((url, "1080p"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Zetvideo parse error: {ex.Message}");
            }
            return result;
        }

        public class Movie
        {
            public string translation { get; set; }
            public List<(string link, string quality)> links { get; set; }
            public SubtitleTpl? subtitles { get; set; }
        }

        public class Result
        {
            public List<Movie> movie { get; set; }
        }
    }
}
