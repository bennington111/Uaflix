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
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace Uaflix.Controllers
{
    public class UaflixController : BaseController
    {
        ProxyManager proxyManager = new ProxyManager(ModInit.UaFlix);
        static HttpClient httpClient = new HttpClient();

        public class AshdiEpisode
        {
            public string title { get; set; }
            public string file { get; set; }
            public string id { get; set; }
            public string poster { get; set; }
            public string subtitle { get; set; }
        }
        public class AshdiSeason
        {
            public string title { get; set; }
            public List<AshdiEpisode> folder { get; set; }
        }
        public class AshdiVoice
        {
            public string title { get; set; }
            public List<AshdiSeason> folder { get; set; }
        }

        public class Result
        {
            public List<Movie> movie { get; set; }
            public List<Episode> episodes { get; set; }
        }
        public class Movie
        {
            public string translation { get; set; }
            public List<(string link, string quality)> links { get; set; }
            public SubtitleTpl? subtitles { get; set; }
        }
        public class Episode
        {
            public string id { get; set; }
            public string title { get; set; }
            public List<(string link, string quality)> links { get; set; }
            public SubtitleTpl? subtitles { get; set; }
        }

        void Log(string msg)
        {
            System.IO.File.AppendAllText("lampac-log.txt", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
        }

        // Універсальний декодер для юнікод-escape url
        string DecodeUnicodeEscapes(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            // Декодує \u002B -> +, \u0026 -> &, \u003F -> ?, \u003D -> = тощо
            input = Regex.Replace(
                input,
                @"\\u([0-9A-Fa-f]{4})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
            );
            // Декодує екрановані слеші
            input = input.Replace("\\/", "/");
            return input;
        }

        [HttpGet]
        [Route("uaflix")]
        async public Task<ActionResult> Index(
            long id, string imdb_id, long kinopoisk_id, string title, string original_title, string original_language,
            int year, string source, int serial, string account_email, string t = null,
            int e = -1, bool rjson = false)
        {
            Log($"=== Index START === e={e}");

            var result = await search(imdb_id, kinopoisk_id, title, original_title, serial);

            string host = $"{Request.Scheme}://{Request.Host}";

            // Серіали
            if (result != null && result.episodes != null && result.episodes.Count > 0)
            {
                Log($"DEBUG: episodes.Count={result.episodes.Count}, e={e}");

                // Корекція індексу серії
                if (e < 0 || e >= result.episodes.Count)
                {
                    Log($"WARNING: e={e} out of range, set to 0");
                    e = 0;
                }

                // Вибір серії
                if (result.episodes.Count > 1 && (Request.Query["e"].Count == 0 || Request.Query["e"] == "-1"))
                {
                    var tpl = new EpisodeTpl(result.episodes.Count);
                    for (int i = 0; i < result.episodes.Count; i++)
                    {
                        string epNum = Regex.Match(result.episodes[i].id ?? "", @"\d+").Value;
                        string displayName = !string.IsNullOrEmpty(epNum) ? $"{epNum} серія" : $"{i + 1} серія";
                        string link = $"{host}/uaflix?id={id}&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&e={i}";
                        tpl.Append(displayName, title, "", (i + 1).ToString(), link);
                    }
                    var html = tpl.ToHtml();
                    html = DecodeUnicodeEscapes(html); // <- важливо!
                    Log($"EpisodeTpl HTML: {html}");
                    return Content(html, "text/html; charset=utf-8");
                }

                // Відео для серії
                var serialEp = result.episodes[e];
                Log($"DEBUG: Show episode e={e} id={serialEp.id} title={serialEp.title}");

                var tpl2 = new MovieTpl(title, original_title, 1);
                var streamquality = new StreamQualityTpl();
                foreach (var item in serialEp.links)
                    streamquality.Append(item.link, item.quality);

                tpl2.Append(
                    $"Серія {serialEp.id}",
                    streamquality.Firts().link,
                    streamquality: streamquality,
                    subtitles: serialEp.subtitles
                );
                var html2 = tpl2.ToHtml();
                html2 = DecodeUnicodeEscapes(html2); // <- важливо!
                Log($"MovieTpl HTML: {html2}");
                return Content(html2, "text/html; charset=utf-8");
            }

            // Фільми
            if (result != null && result.movie != null && result.movie.Count > 0)
            {
                Log($"DEBUG: movie.Count={result.movie.Count}");
                var tpl = new MovieTpl(title, original_title, result.movie.Count);
                foreach (var movie in result.movie)
                {
                    var streamquality = new StreamQualityTpl();
                    foreach (var item in movie.links)
                        streamquality.Append(item.link, item.quality);

                    tpl.Append(
                        movie.translation,
                        streamquality.Firts().link,
                        streamquality: streamquality,
                        subtitles: movie.subtitles
                    );
                }
                var html = tpl.ToHtml();
                html = DecodeUnicodeEscapes(html); // <- важливо!
                Log($"MovieTpl (film) HTML: {html}");
                return Content(html, "text/html; charset=utf-8");
            }

            Log("WARNING: No data to show (return default)");
            return Content("Uaflix", "text/html; charset=utf-8");
        }

        async ValueTask<Result> search(string imdb_id, long kinopoisk_id, string title, string original_title, int serial)
        {
            string memKey = $"UaFlix:view:{kinopoisk_id}:{imdb_id}";
            Log($"search: START {memKey}");
            if (hybridCache.TryGetValue(memKey, out Result res))
            {
                Log("search: знайдено у кеші");
                return res;
            }

            try
            {
                string filmTitle = !string.IsNullOrEmpty(title) ? title : original_title;
                string searchUrl = $"https://uafix.net/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(filmTitle)}";
                Log("search: searchUrl " + searchUrl);

                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                httpClient.DefaultRequestHeaders.Add("Referer", "https://uafix.net/");

                var searchHtml = await httpClient.GetStringAsync(searchUrl);
                Log($"search: отримано HTML пошуку, довжина = {searchHtml.Length}");
                var doc = new HtmlDocument();
                doc.LoadHtml(searchHtml);

                var filmNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'sres-wrap')]");
                Log($"search: filmNodes.Count = {(filmNodes == null ? 0 : filmNodes?.Count ?? 0)}");
                if (filmNodes == null)
                    return null;

                // Логування всіх знайдених фільмів
                foreach (var node in filmNodes)
                {
                    string href = node.GetAttributeValue("href", "");
                    string name = node.InnerText.Trim();
                    Log($"search: foundNode href={href} name={name}");
                }

                // ---- Патч вибору foundNode ----
                string norm(string s) => Regex.Replace((s ?? "").ToLower(), @"[^\w]", "");
                string reqTitle = norm(title);
                string reqOrigTitle = norm(original_title);

                HtmlNode foundNode = null;
                // 1. Точна відповідність по першій частині (до / або повністю)
                foundNode = filmNodes.FirstOrDefault(node =>
                {
                    var mainName = node.InnerText.Split('/')[0].Trim();
                    var nodeText = norm(mainName);
                    return nodeText == reqTitle || nodeText == reqOrigTitle;
                });

                // 2. Якщо не знайшли — по всьому рядку
                if (foundNode == null)
                {
                    foundNode = filmNodes.FirstOrDefault(node =>
                    {
                        var nodeText = norm(node.InnerText);
                        return nodeText.Contains(reqTitle) || nodeText.Contains(reqOrigTitle);
                    });
                }

                // 3. Якщо все одно не знайшли — по перших 6 символах
                if (foundNode == null)
                {
                    foundNode = filmNodes.FirstOrDefault(node =>
                    {
                        var nodeText = norm(node.InnerText);
                        return reqTitle.Length > 6 && nodeText.Contains(reqTitle.Substring(0, 6));
                    });
                }

                // 4. Фолбек: перший у видачі
                if (foundNode == null)
                    foundNode = filmNodes.First();

                string foundHref = foundNode.GetAttributeValue("href", "");
                string foundText = foundNode.InnerText.Trim();
                Log($"search: foundNode (used) href={foundHref} name={foundText}");

                string filmUrl = foundHref;
                if (!filmUrl.StartsWith("http"))
                    filmUrl = "https://uafix.net" + filmUrl;

                Log($"search: filmUrl = {filmUrl}");

                var filmHtml = await httpClient.GetStringAsync(filmUrl);
                Log($"search: отримано HTML фільму, довжина = {filmHtml.Length}");
                doc.LoadHtml(filmHtml);

                // Ashdi iframe (серіали)
                var ashdiIframeNode = doc.DocumentNode.SelectSingleNode("//iframe[contains(@src,'ashdi.vip/serial/')]");
                if (ashdiIframeNode != null)
                {
                    string ashdiUrl = ashdiIframeNode.GetAttributeValue("src", "");
                    Log($"search: знайдено Ashdi iframe: {ashdiUrl}");
                    var ashdiHtml = await httpClient.GetStringAsync(ashdiUrl);
                    Log($"search: отримано HTML Ashdi, довжина = {ashdiHtml.Length}");

                    // file:'[...]'
                    var ashdiFileMatch = Regex.Match(ashdiHtml, @"file:'(\[.*?\])'");
                    if (ashdiFileMatch.Success)
                    {
                        string fileJson = ashdiFileMatch.Groups[1].Value;
                        Log($"search: знайдено file:'[...]' (довжина json: {fileJson.Length})");
                        var voices = JsonConvert.DeserializeObject<List<AshdiVoice>>(fileJson);

                        var episodesList = new List<Episode>();
                        foreach (var voice in voices)
                        {
                            foreach (var season in voice.folder ?? new List<AshdiSeason>())
                            {
                                foreach (var ep in season.folder ?? new List<AshdiEpisode>())
                                {
                                    string epLink = DecodeUnicodeEscapes(ep.file ?? "");
                                    episodesList.Add(new Episode
                                    {
                                        id = ep.title?.Trim().Replace("Серія ", "").Trim(),
                                        title = ep.title?.Trim(),
                                        links = new List<(string, string)> { (epLink, "1080p") },
                                        subtitles = ParseAshdiSubtitles(ep.subtitle)
                                    });
                                }
                            }
                        }

                        Log($"search: Ashdi Серій: {episodesList.Count}");

                        res = new Result
                        {
                            episodes = episodesList,
                            movie = new List<Movie>()
                        };
                        hybridCache.Set(memKey, res, cacheTime(5));
                        proxyManager.Success();
                        Log("search: Ashdi серіал успішно розпарсено");
                        return res;
                    }
                    else
                    {
                        Log("search: Ashdi file:'[...]' не знайдено");
                    }
                }

                // Фільми: шукаємо iframe Zetvideo/Ashdi
                var iframeNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'video-box')]/iframe");
                Log($"search: iframeNodes.Count = {(iframeNodes == null ? 0 : iframeNodes?.Count ?? 0)}");
                if (iframeNodes != null && iframeNodes.Any())
                {
                    var movies = new List<Movie>();
                    foreach (var iframe in iframeNodes)
                    {
                        string iframeUrl = iframe.GetAttributeValue("src", "");
                        if (string.IsNullOrEmpty(iframeUrl)) continue;

                        // Zetvideo
                        if (iframeUrl.Contains("zetvideo.net"))
                        {
                            Log($"search: Zetvideo iframe: {iframeUrl}");
                            var zlinks = await ParseAllZetvideoSources(iframeUrl);
                            foreach (var l in zlinks)
                                movies.Add(new Movie
                                {
                                    translation = $"{filmTitle} (Zetvideo)",
                                    links = new List<(string, string)> { (DecodeUnicodeEscapes(l.link), l.quality) },
                                    subtitles = null
                                });
                        }
                        // Ashdi
                        else if (iframeUrl.Contains("ashdi.vip"))
                        {
                            Log($"search: Ashdi (фільм) iframe: {iframeUrl}");
                            var alinks = await ParseAllAshdiSources(iframeUrl);
                            SubtitleTpl? subtitles = null;
                            try
                            {
                                var ashdiHtml = await httpClient.GetStringAsync(iframeUrl);
                                var subMatch = Regex.Match(ashdiHtml, @"subtitle[""']?\s*:\s*[""']([^""']+)[""']");
                                if (subMatch.Success)
                                    subtitles = ParseAshdiSubtitles(subMatch.Groups[1].Value);
                            }
                            catch { }
                            foreach (var l in alinks)
                                movies.Add(new Movie
                                {
                                    translation = $"{filmTitle} (Ashdi)",
                                    links = new List<(string, string)> { (DecodeUnicodeEscapes(l.link), l.quality) },
                                    subtitles = subtitles
                                });
                        }
                    }
                    if (movies.Count > 0)
                    {
                        Log($"search: знайдено фільмів: {movies.Count}");
                        res = new Result()
                        {
                            movie = movies,
                            episodes = null
                        };
                        hybridCache.Set(memKey, res, cacheTime(5));
                        proxyManager.Success();
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"search: ERROR {ex}");
            }
            Log("search: Повертаємо null");
            return null;
        }

        SubtitleTpl? ParseAshdiSubtitles(string subtitle)
        {
            if (string.IsNullOrEmpty(subtitle))
                return null;
            var st = new SubtitleTpl();
            var matches = Regex.Matches(subtitle, "\\[([^\\]]+)\\](https?://[^\\,]+)");
            foreach (Match match in matches)
                st.Append(match.Groups[1].Value, match.Groups[2].Value);
            return st.IsEmpty() ? null : st;
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
                Log($"ParseAllZetvideoSources: {ex.Message}");
            }
            return result;
        }

        async Task<List<(string link, string quality)>> ParseAllAshdiSources(string iframeUrl)
        {
            var result = new List<(string link, string quality)>();
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                request.Headers.Add("User-Agent", "Mozilla/5.0");
                request.Headers.Add("Referer", "https://ashdi.vip/");
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
                Log($"ParseAllAshdiSources: {ex.Message}");
            }
            return result;
        }
    }
}