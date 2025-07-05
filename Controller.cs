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
using System.Collections.Generic;
using Newtonsoft.Json;

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

            // Гарантуємо, що subtitles не null
            if (result?.movie != null)
            {
                foreach (var m in result.movie)
                {
                    if (m.subtitles == null)
                        m.subtitles = new SubtitleTpl();
                }
            }
            if (result?.serial != null)
            {
                foreach (var voices in result.serial.Values)
                {
                    foreach (var voice in voices)
                    {
                        foreach (var ep in voice.episodes)
                        {
                            if (ep.subtitles == null)
                                ep.subtitles = new SubtitleTpl();
                        }
                    }
                }
            }

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
                        streamquality: streamquality
                    );
                }

                if (rjson)
                {
                    foreach (var m in result.movie)
                        Console.WriteLine("UaFlix: Final subtitles before JSON: " + JsonConvert.SerializeObject(m.subtitles?.ToObject()));
                    var movies = result.movie.Select(m => new {
                        translation = m.translation,
                        links = m.links,
                        subtitles = m.subtitles?.ToObject(),
                        // додайте інші потрібні поля, якщо потрібно
                    }).ToList();
                    return Content(JsonConvert.SerializeObject(new { movie = movies }), "application/json; charset=utf-8");
                }
                else
                {
                    return Content(tpl.ToHtml(), "text/html; charset=utf-8");
                }
            }

            if (result.serial != null)
            {
                string defaultargs = $"&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&serial={serial}";

                if (s == -1)
                {
                    // Показуємо список сезонів
                    var tpl = new SeasonTpl(quality: "1080p");
                
                foreach (var voice in result.serial)
                    {
                        foreach (var season in voice.Value)
                            tpl.Append($"{season.id} сезон", $"{host}/uaflix?s={season.id}&t={voice.Key}" + defaultargs, season.id);
                    }

                    return rjson
                        ? Content(tpl.ToJson(), "application/json; charset=utf-8")
                        : Content(tpl.ToHtml(), "text/html; charset=utf-8");
                }
                else
                {
                    // Показуємо епізоди конкретного сезону
                    var etpl = new EpisodeTpl();
                    Console.WriteLine($"UaFlix: s param = '{s}'");
                    
                    // Використовуємо окремий метод для парсингу сезону з кешем
                    var seasonResult = await searchSeason(imdb_id, kinopoisk_id, title, original_title, s);
                    if (seasonResult == null || seasonResult.serial == null || !seasonResult.serial.ContainsKey("Українська") || seasonResult.serial["Українська"].Count == 0)
                    {
                        return Content("Uaflix", "text/html; charset=utf-8");
                    }
                    
                    var selectedSeason = seasonResult.serial["Українська"].First();
                    Console.WriteLine($"UaFlix: season id = '{selectedSeason.id}'");

                    // Сортуємо і видаляємо дублікати
                    var uniqueEpisodes = selectedSeason.episodes
                        .GroupBy(x => x.episode)
                        .Select(g => g.First())
                        .OrderBy(x => int.Parse(x.episode));

                    var episodes = new List<Episode>();
                    foreach (var ep in uniqueEpisodes)
                    {
                        var episodeUrl = ep.url;
                        var episodeId = ep.episode;

                        var episode = new Episode
                        {
                            episode = episodeId,
                            links = new List<(string link, string quality)>(),
                            url = episodeUrl
                        };
                        SubtitleTpl? subtitles = null;
                        try
                        {
                            var episodeHtml = await httpClient.GetStringAsync(episodeUrl);
                            var episodeDoc = new HtmlDocument();
                            episodeDoc.LoadHtml(episodeHtml);
                            var iframeNodes = episodeDoc.DocumentNode.SelectNodes("//div[contains(@class, 'video-box')]/iframe | //iframe[contains(@src, 'zetvideo.net')] | //meta[@property='og:video:iframe']");
                            if (iframeNodes != null)
                            {
                                var uniqueIframeUrls = new HashSet<string>();

                                foreach (var iframe in iframeNodes)
                                {
                                    string iframeUrl = "";
                                    
                                    // Якщо це meta тег, витягуємо iframe з content
                                    if (iframe.Name == "meta" && iframe.GetAttributeValue("property", "") == "og:video:iframe")
                                    {
                                        var content = iframe.GetAttributeValue("content", "");
                                        var iframeMatch = Regex.Match(content, @"src=['""]([^'""]+)['""]");
                                        if (iframeMatch.Success)
                                            iframeUrl = iframeMatch.Groups[1].Value;
                                    }
                                    else
                                    {
                                        iframeUrl = iframe.GetAttributeValue("src", "");
                                    }
                                    
                                    if (string.IsNullOrEmpty(iframeUrl) || !iframeUrl.Contains("zetvideo.net")) continue;
                                    if (!uniqueIframeUrls.Add(iframeUrl)) continue; // вже додано, пропускаємо

                                    Console.WriteLine($"UaFlix: Processing zetvideo URL: {iframeUrl}");
                                    var zlinks = await ParseAllZetvideoSources(iframeUrl);
                                    Console.WriteLine($"UaFlix: Found {zlinks.Count} zetvideo links");
                                    var subtitlesResult = await ParseSubtitles(iframeUrl);
                                    if (subtitlesResult.HasValue)
                                    {
                                        subtitles = subtitlesResult.Value;
                                    }
                                    foreach (var l in zlinks)
                                        episode.links.Add((l.link, l.quality));
                                }
                            }
                            // Якщо не zetvideo, але хочемо парсити субтитри з основної сторінки
                            episode.subtitles = subtitles;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Episode parse error: {ex.Message}");
                        }
                        episodes.Add(episode);
                    }

                    foreach (var episode in episodes)
                    {
                        var streamquality = new StreamQualityTpl();
                        foreach (var item in episode.links)
                            streamquality.Append(HostStreamProxy(ModInit.UaFlix, item.link, proxy: proxy), item.quality);

                        etpl.Append($"{episode.episode} серия", $"{title ?? original_title} ({episode.episode} серия)", s.ToString(), episode.episode, streamquality.Firts().link, streamquality: streamquality);
                    }

                return rjson
                        ? Content(etpl.ToJson(), "application/json; charset=utf-8")
                        : Content(etpl.ToHtml(), "text/html; charset=utf-8");
                }
            }

            return Content("Uaflix", "text/html; charset=utf-8");
        }

        async ValueTask<Result> search(string imdb_id, long kinopoisk_id, string title, string original_title, int serial)
        {
            string filmTitle = !string.IsNullOrEmpty(title) ? title : original_title;
            string memKey = $"UaFlix:view:{kinopoisk_id}:{imdb_id}";
            Console.WriteLine($"UaFlix: Search started for '{filmTitle}' (serial={serial}, kinopoisk={kinopoisk_id}, imdb={imdb_id})");
            
            if (hybridCache.TryGetValue(memKey, out Result res))
            {
                Console.WriteLine("UaFlix: Result from cache: " + Newtonsoft.Json.JsonConvert.SerializeObject(res));
                if (res == null || (res.movie == null && res.serial == null))
                {
                    Console.WriteLine("UaFlix: Cached result is empty, ignoring cache");
                    res = null;
                }
                else
                {
                    // Гарантуємо ініціалізацію subtitles для всіх фільмів
                    if (res.movie != null)
                    {
                        foreach (var m in res.movie)
                        {
                            // Присвоюємо новий SubtitleTpl лише якщо subtitles не ініціалізований (default)
                            if (m.subtitles == null)
                                m.subtitles = new SubtitleTpl();
                        }
                    }
                    // Аналогічно для серіалів/епізодів, якщо потрібно
                    Console.WriteLine("UaFlix: Search completed, result: True");
                    return res;
                }
            }
            else
            {
                try
                {
                    string searchUrl = $"https://uafix.net/index.php?do=search&subaction=search&story={HttpUtility.UrlEncode(filmTitle)}";
                    
                    Console.WriteLine($"UaFlix: Searching at {searchUrl}");

                    httpClient.DefaultRequestHeaders.Clear();
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                    httpClient.DefaultRequestHeaders.Add("Referer", "https://uafix.net/");

                    var searchHtml = await httpClient.GetStringAsync(searchUrl);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(searchHtml);

                    // Виправлений селектор для пошуку результатів
                    var filmNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'sres-wrap')]");
                    if (filmNode == null) {
                        Console.WriteLine($"UaFlix: No film found for '{filmTitle}' at {searchUrl}");
                        return null;
                    }

                    string filmUrl = filmNode.GetAttributeValue("href", "");
                    if (!filmUrl.StartsWith("http"))
                        filmUrl = "https://uafix.net" + filmUrl;
                    
                    Console.WriteLine($"UaFlix: Found film at {filmUrl}");

                    var filmHtml = await httpClient.GetStringAsync(filmUrl);
                    doc.LoadHtml(filmHtml);

                    // Перевіряємо чи це серіал
                    var serialIndicator = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'serial')]") ?? 
                                        doc.DocumentNode.SelectSingleNode("//div[contains(text(), 'Сезон')]") ??
                                        doc.DocumentNode.SelectSingleNode("//div[contains(text(), 'Епізод')]");

                    Console.WriteLine($"UaFlix: Serial indicator found: {serialIndicator != null}");

                    // Визначаємо тип контенту за URL
                    if (filmUrl.Contains("/serials/"))
                    {
                        Console.WriteLine("UaFlix: URL contains /serials/ - parsing as serial");
                        var serialResult = await ParseSerial(filmUrl, filmTitle, null);
                        if (serialResult != null)
                        {
                            Console.WriteLine("UaFlix: Serial parsing successful");
                            res = new Result() { serial = serialResult };
                            hybridCache.Set(memKey, res, cacheTime(5));
                            proxyManager.Success();
                            return res;
                        }
                        else
                        {
                            Console.WriteLine("UaFlix: Serial parsing failed");
                        }
                    }
                    else if (filmUrl.Contains("/films/"))
                    {
                        Console.WriteLine("UaFlix: URL contains /films/ - parsing as movie");
                        // Парсимо фільм - виправлений селектор для iframe
                        var iframeNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'video-box')]/iframe | //iframe[contains(@src, 'zetvideo.net')] | //meta[@property='og:video:iframe']");
                        if (iframeNodes == null || !iframeNodes.Any()) {
                            Console.WriteLine($"UaFlix: No video iframes found at {filmUrl}");
                            return null;
                        }

                        Console.WriteLine($"UaFlix: Found {iframeNodes.Count} iframe nodes");

                        var movies = new List<Movie>();
                        var uniqueIframeUrls = new HashSet<string>();

                        foreach (var iframe in iframeNodes)
                        {
                            string iframeUrl = "";
                            
                            // Якщо це meta тег, витягуємо iframe з content
                            if (iframe.Name == "meta" && iframe.GetAttributeValue("property", "") == "og:video:iframe")
                            {
                                var content = iframe.GetAttributeValue("content", "");
                                Console.WriteLine($"UaFlix: Found meta iframe content: {content}");
                                var iframeMatch = Regex.Match(content, @"src=['""]([^'""]+)['""]");
                                if (iframeMatch.Success)
                                    iframeUrl = iframeMatch.Groups[1].Value;
                            }
                            else
                            {
                                iframeUrl = iframe.GetAttributeValue("src", "");
                            }
                            
                            Console.WriteLine($"UaFlix: Processing iframe URL: {iframeUrl}");
                            
                            if (string.IsNullOrEmpty(iframeUrl) || !iframeUrl.Contains("zetvideo.net")) continue;
                            if (!uniqueIframeUrls.Add(iframeUrl)) continue; // вже додано, пропускаємо

                            Console.WriteLine($"UaFlix: Processing zetvideo URL: {iframeUrl}");
                            var zlinks = await ParseAllZetvideoSources(iframeUrl);
                            Console.WriteLine($"UaFlix: Found {zlinks.Count} zetvideo links");
                            var subtitlesResult = await ParseSubtitles(iframeUrl);
                            var subtitles = subtitlesResult.GetValueOrDefault();
                            foreach (var l in zlinks)
                                movies.Add(new Movie
                                {
                                    translation = $"{filmTitle} (Zetvideo)",
                                    links = new List<(string, string)> { (l.link, l.quality) },
                                    subtitles = subtitles
                                });
                        }

                        if (movies.Count > 0)
                        {
                            Console.WriteLine($"UaFlix: Movie parsing successful, found {movies.Count} movies");
                            
                            // Парсимо субтитри для фільму
                            var subtitlesResult = await ParseSubtitles(filmUrl);
                            var subtitles = subtitlesResult.GetValueOrDefault();
                            
                            res = new Result()
                            {
                                movie = movies
                            };
                            
                            // Додаємо субтитри до всіх фільмів
                            if (subtitlesResult.HasValue)
                            {
                                foreach (var movie in movies)
                                {
                                    subtitles = subtitlesResult.Value;
                                    movie.subtitles = subtitles;
                                }
                            }
                            
                            hybridCache.Set(memKey, res, cacheTime(5));
                            proxyManager.Success();
                        }
                        else
                        {
                            Console.WriteLine($"UaFlix: Movie parsing failed");
                        }
                    }
                    else
                    {
                        // fallback: стара логіка
                        if (serial == 1 || serialIndicator != null)
                        {
                            Console.WriteLine($"UaFlix: Parsing as serial (fallback)");
                            var serialResult = await ParseSerial(filmUrl, filmTitle, null);
                            if (serialResult != null)
                            {
                                Console.WriteLine($"UaFlix: Serial parsing successful");
                                res = new Result() { serial = serialResult };
                                hybridCache.Set(memKey, res, cacheTime(5));
                                proxyManager.Success();
                                return res;
                            }
                            else
                            {
                                Console.WriteLine($"UaFlix: Serial parsing failed");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"UaFlix: Parsing as movie (fallback)");
                            // Парсимо фільм - виправлений селектор для iframe
                            var iframeNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'video-box')]/iframe | //iframe[contains(@src, 'zetvideo.net')] | //meta[@property='og:video:iframe']");
                            if (iframeNodes == null || !iframeNodes.Any()) {
                                Console.WriteLine($"UaFlix: No video iframes found at {filmUrl}");
                                return null;
                            }

                            Console.WriteLine($"UaFlix: Found {iframeNodes.Count} iframe nodes");

                            var movies = new List<Movie>();
                            var uniqueIframeUrls = new HashSet<string>();

                            foreach (var iframe in iframeNodes)
                            {
                                string iframeUrl = "";
                                
                                // Якщо це meta тег, витягуємо iframe з content
                                if (iframe.Name == "meta" && iframe.GetAttributeValue("property", "") == "og:video:iframe")
                                {
                                    var content = iframe.GetAttributeValue("content", "");
                                    Console.WriteLine($"UaFlix: Found meta iframe content: {content}");
                                    var iframeMatch = Regex.Match(content, @"src=['""]([^'""]+)['""]");
                                    if (iframeMatch.Success)
                                        iframeUrl = iframeMatch.Groups[1].Value;
                                }
                                else
                                {
                                    iframeUrl = iframe.GetAttributeValue("src", "");
                                }
                                
                                Console.WriteLine($"UaFlix: Processing iframe URL: {iframeUrl}");
                                
                                if (string.IsNullOrEmpty(iframeUrl) || !iframeUrl.Contains("zetvideo.net")) continue;
                                if (!uniqueIframeUrls.Add(iframeUrl)) continue; // вже додано, пропускаємо

                                Console.WriteLine($"UaFlix: Processing zetvideo URL: {iframeUrl}");
                                var zlinks = await ParseAllZetvideoSources(iframeUrl);
                                Console.WriteLine($"UaFlix: Found {zlinks.Count} zetvideo links");
                                var subtitlesResult = await ParseSubtitles(iframeUrl);
                                var subtitles = subtitlesResult.GetValueOrDefault();
                                foreach (var l in zlinks)
                                    movies.Add(new Movie
                                    {
                                        translation = $"{filmTitle} (Zetvideo)",
                                        links = new List<(string, string)> { (l.link, l.quality) },
                                        subtitles = subtitles
                                    });
                            }

                            if (movies.Count > 0)
                            {
                                Console.WriteLine($"UaFlix: Movie parsing successful, found {movies.Count} movies");
                                
                                // Парсимо субтитри для фільму
                                var subtitlesResult = await ParseSubtitles(filmUrl);
                                var subtitles = subtitlesResult.GetValueOrDefault();
                                
                                res = new Result()
                                {
                                    movie = movies
                                };
                                
                                // Додаємо субтитри до всіх фільмів
                                if (subtitlesResult.HasValue)
                                {
                                    foreach (var movie in movies)
                                    {
                                        subtitles = subtitlesResult.Value;
                                        movie.subtitles = subtitles;
                                    }
                                }
                                
                                hybridCache.Set(memKey, res, cacheTime(5));
                                proxyManager.Success();
                            }
                            else
                            {
                                Console.WriteLine($"UaFlix: Movie parsing failed");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UaFlix error: {ex.Message}");
                }
            }
            
            if (res != null && (res.movie != null || res.serial != null))
            {
                Console.WriteLine("UaFlix: Caching result: " + JsonConvert.SerializeObject(res));
                hybridCache.Set(memKey, res, cacheTime(5));
            }
            else
            {
                Console.WriteLine("UaFlix: Not caching empty result");
            }
            
            Console.WriteLine($"UaFlix: Search completed, result: {res != null}");
            if (res == null || (res.movie == null && res.serial == null)) {
                Console.WriteLine($"UaFlix: Not caching empty result for '{filmTitle}'");
                return null;
            }
            return res;
        }

        // Додаємо окремий метод для парсингу конкретного сезону з кешем
        async ValueTask<Result> searchSeason(string imdb_id, long kinopoisk_id, string title, string original_title, int seasonNumber)
        {
            string memKey = $"UaFlix:season:{kinopoisk_id}:{imdb_id}:{seasonNumber}";
            Console.WriteLine($"UaFlix: Season search started for '{title}' season {seasonNumber}");
            
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

                    // Парсимо конкретний сезон
                    var serialResult = await ParseSerial(filmUrl, filmTitle, seasonNumber.ToString());
                    if (serialResult != null)
                    {
                        res = new Result() { serial = serialResult };
                        hybridCache.Set(memKey, res, cacheTime(10)); // Кешуємо на 10 хвилин
                        proxyManager.Success();
                        return res;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UaFlix season error: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"UaFlix: Season result found in cache");
            }
            
            return res;
        }

        async Task<Dictionary<string, List<Voice>>> ParseSerial(string filmUrl, string filmTitle, string selectedSeason = null)
        {
            try
            {
                var doc = new HtmlDocument();
                var filmHtml = await httpClient.GetStringAsync(filmUrl);
                doc.LoadHtml(filmHtml);

                // Знаходимо всі сезони (посилання на сторінки сезону)
                var seasonLinks = new List<(string url, string season)>();
                var seasonNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, 'sezon-')]");
                if (seasonNodes != null)
                {
                    foreach (var node in seasonNodes)
                    {
                        var href = node.GetAttributeValue("href", "");
                        if (!string.IsNullOrEmpty(href))
                        {
                            if (!href.StartsWith("http"))
                                href = "https://uafix.net" + href;
                            var seasonMatch = Regex.Match(href, @"sezon-(\d+)");
                            var season = seasonMatch.Success ? seasonMatch.Groups[1].Value : "1";
                            seasonLinks.Add((href, season));
                        }
                    }
                }
                // Якщо не знайдено жодного сезону, додаємо поточну сторінку як сезон 1
                if (seasonLinks.Count == 0)
                    seasonLinks.Add((filmUrl, "1"));

                var serial = new Dictionary<string, List<Voice>>();
                var voice = "Українська";
                serial[voice] = new List<Voice>();

                // Якщо не вибрано конкретний сезон - парсимо всі сезони для списку
                if (string.IsNullOrEmpty(selectedSeason) || selectedSeason == "-1")
                {
                    foreach (var seasonLink in seasonLinks.OrderBy(x => int.Parse(x.season)))
                    {
                        var episodes = new List<Episode>();
                        // Додаємо заглушку епізоду для відображення сезону в списку
                        episodes.Add(new Episode 
                        { 
                            episode = "1",
                            links = new List<(string link, string quality)>(),
                            url = seasonLink.url
                        });
                        
                        serial[voice].Add(new Voice { id = seasonLink.season, name = "Українська", episodes = episodes });
                    }
                }
                else
                {
                    // Парсимо лише вибраний сезон
                    var seasonUrl = seasonLinks.First(x => x.season == selectedSeason).url;

                    // Парсимо лише вибраний сезон
                    var seasonDoc = new HtmlDocument();
                    var seasonHtml = await httpClient.GetStringAsync(seasonUrl);
                    seasonDoc.LoadHtml(seasonHtml);

                    // Збираємо всі посилання на епізоди на сторінці сезону
                    var episodeLinks = new List<(string url, string episode)>();
                    var episodeNodes = seasonDoc.DocumentNode.SelectNodes("//a[contains(@href, 'season-') and contains(@href, 'episode-')]");
                    if (episodeNodes != null)
                    {
                        foreach (var node in episodeNodes)
                        {
                            var href = node.GetAttributeValue("href", "");
                            if (!string.IsNullOrEmpty(href))
                            {
                                if (!href.StartsWith("http"))
                                    href = "https://uafix.net" + href;
                                var episodeMatch = Regex.Match(href, @"episode-(\d+)");
                                var episode = episodeMatch.Success ? episodeMatch.Groups[1].Value : "1";
                                episodeLinks.Add((href, episode));
                            }
                        }
                    }
                    // Якщо не знайдено жодного епізоду, додаємо перший епізод
                    if (episodeLinks.Count == 0)
                        episodeLinks.Add((seasonUrl, "1"));

                    // Сортуємо і видаляємо дублікати
                    var uniqueEpisodes = episodeLinks
                        .GroupBy(x => x.episode)
                        .Select(g => g.First())
                        .OrderBy(x => int.Parse(x.episode));

                    var episodes = new List<Episode>();
                    foreach (var ep in uniqueEpisodes)
                    {
                        var episodeUrl = ep.url;
                        var episodeId = ep.episode;

                        var episode = new Episode 
                        { 
                            episode = episodeId,
                            links = new List<(string link, string quality)>(),
                            url = episodeUrl
                        };
                        SubtitleTpl? subtitles = null;
                        try
                        {
                            var episodeHtml = await httpClient.GetStringAsync(episodeUrl);
                            var episodeDoc = new HtmlDocument();
                            episodeDoc.LoadHtml(episodeHtml);
                            var iframeNodes = episodeDoc.DocumentNode.SelectNodes("//div[contains(@class, 'video-box')]/iframe | //iframe[contains(@src, 'zetvideo.net')] | //meta[@property='og:video:iframe']");
                            if (iframeNodes != null)
                            {
                                var uniqueIframeUrls = new HashSet<string>();

                                foreach (var iframe in iframeNodes)
                                {
                                    string iframeUrl = "";
                                    
                                    // Якщо це meta тег, витягуємо iframe з content
                                    if (iframe.Name == "meta" && iframe.GetAttributeValue("property", "") == "og:video:iframe")
                                    {
                                        var content = iframe.GetAttributeValue("content", "");
                                        var iframeMatch = Regex.Match(content, @"src=['""]([^'""]+)['""]");
                                        if (iframeMatch.Success)
                                            iframeUrl = iframeMatch.Groups[1].Value;
                                    }
                                    else
                                    {
                                        iframeUrl = iframe.GetAttributeValue("src", "");
                                    }
                                    
                                    if (string.IsNullOrEmpty(iframeUrl) || !iframeUrl.Contains("zetvideo.net")) continue;
                                    if (!uniqueIframeUrls.Add(iframeUrl)) continue;
                                    Console.WriteLine($"UaFlix: Processing zetvideo URL: {iframeUrl}");
                                    var zlinks = await ParseAllZetvideoSources(iframeUrl);
                                    Console.WriteLine($"UaFlix: Found {zlinks.Count} zetvideo links");
                                    var subtitlesResult = await ParseSubtitles(iframeUrl);
                                    if (subtitlesResult.HasValue)
                                    {
                                        subtitles = subtitlesResult.Value;
                                    }
                                    foreach (var l in zlinks)
                                        episode.links.Add((l.link, l.quality));
                                }
                            }
                            // Якщо не zetvideo, але хочемо парсити субтитри з основної сторінки
                            episode.subtitles = subtitles;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Episode parse error: {ex.Message}");
                        }
                        episodes.Add(episode);
                    }
                    serial[voice].Add(new Voice { id = selectedSeason, name = "Українська", episodes = episodes });
                }

                return serial.Count > 0 ? serial : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Serial parse error: {ex.Message}");
                return null;
            }
        }

        async Task<List<(string link, string quality)>> ParseAllZetvideoSources(string iframeUrl)
        {
            var result = new List<(string link, string quality)>();
            try
            {
                Console.WriteLine($"UaFlix: Parsing zetvideo sources from: {iframeUrl}");
                var request = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                request.Headers.Add("User-Agent", "Mozilla/5.0");
                var response = await httpClient.SendAsync(request);
                var html = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"UaFlix: Got zetvideo HTML, length: {html.Length}");
                
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var sourceNodes = doc.DocumentNode.SelectNodes("//source[contains(@src, '.m3u8')]");
                if (sourceNodes != null)
                {
                    Console.WriteLine($"UaFlix: Found {sourceNodes.Count} source nodes with m3u8");
                    foreach (var node in sourceNodes)
                    {
                        var url = node.GetAttributeValue("src", null);
                        var label = node.GetAttributeValue("label", null) ?? node.GetAttributeValue("res", null) ?? "1080p";
                        if (!string.IsNullOrEmpty(url))
                        {
                            Console.WriteLine($"UaFlix: Found m3u8 source: {url} ({label})");
                            result.Add((url, label));
                        }
                    }
                }

                if (result.Count == 0)
                {
                    Console.WriteLine($"UaFlix: No source nodes found, searching in scripts");
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
                            {
                                Console.WriteLine($"UaFlix: Found m3u8 in script: {url}");
                                result.Add((url, "1080p"));
                            }
                        }
                    }
                }
                
                Console.WriteLine($"UaFlix: Total zetvideo sources found: {result.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Zetvideo parse error: {ex.Message}");
            }
            return result;
        }

        // Додаємо метод для парсингу субтитрів
        async Task<SubtitleTpl?> ParseSubtitles(string filmUrl)
        {
            try
            {
                var doc = new HtmlDocument();
                var filmHtml = await httpClient.GetStringAsync(filmUrl);
                doc.LoadHtml(filmHtml);

                var subtitles = new SubtitleTpl();

                // Шукаємо субтитри в різних форматах
                var subtitleNodes = doc.DocumentNode.SelectNodes("//track[@kind='subtitles'] | //a[contains(@href, '.srt')] | //a[contains(@href, '.vtt')] | //div[contains(@class, 'subtitle')]//a");
                
                if (subtitleNodes != null)
                {
                    foreach (var node in subtitleNodes)
                    {
                        var subtitleUrl = node.GetAttributeValue("src", "") ?? node.GetAttributeValue("href", "");
                        var language = node.GetAttributeValue("srclang", "") ?? node.GetAttributeValue("lang", "") ?? "uk";
                        
                        if (!string.IsNullOrEmpty(subtitleUrl))
                        {
                            if (!subtitleUrl.StartsWith("http"))
                                subtitleUrl = "https://uafix.net" + subtitleUrl;
                            
                            subtitles.Append(language, subtitleUrl);
                            Console.WriteLine($"UaFlix: Parsed subtitle: {language} => {subtitleUrl}");
                        }
                    }
                }

                // Шукаємо субтитри в iframe
                var iframeNodes = doc.DocumentNode.SelectNodes("//iframe[contains(@src, 'subtitles') or contains(@src, 'sub')]");
                if (iframeNodes != null)
                {
                    foreach (var iframe in iframeNodes)
                    {
                        var iframeUrl = iframe.GetAttributeValue("src", "");
                        if (!string.IsNullOrEmpty(iframeUrl))
                        {
                            if (!iframeUrl.StartsWith("http"))
                                iframeUrl = "https://uafix.net" + iframeUrl;
                            
                            try
                            {
                                var iframeHtml = await httpClient.GetStringAsync(iframeUrl);
                                var iframeDoc = new HtmlDocument();
                                iframeDoc.LoadHtml(iframeHtml);

                                var subNodes = iframeDoc.DocumentNode.SelectNodes("//a[contains(@href, '.srt') or contains(@href, '.vtt')]");
                                if (subNodes != null)
                                {
                                    foreach (var subNode in subNodes)
                                    {
                                        var subUrl = subNode.GetAttributeValue("href", "");
                                        var subText = subNode.InnerText.Trim();
                                        
                                        if (!string.IsNullOrEmpty(subUrl))
                                        {
                                            if (!subUrl.StartsWith("http"))
                                                subUrl = "https://uafix.net" + subUrl;
                                            
                                            var lang = subText.Contains("укр") ? "uk" : 
                                                      subText.Contains("рос") ? "ru" : 
                                                      subText.Contains("eng") ? "en" : "uk";
                                            
                                            subtitles.Append(lang, subUrl);
                                            Console.WriteLine($"UaFlix: Parsed subtitle from iframe: {lang} => {subUrl}");
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Subtitle iframe parse error: {ex.Message}");
                            }
                        }
                    }
                }

                // Додаємо парсинг JS-конфігурації Zetvideo (subtitle: '[Назва]URL,')
                var scriptNodes = doc.DocumentNode.SelectNodes("//script");
                if (scriptNodes != null)
                {
                    foreach (var script in scriptNodes)
                    {
                        var text = script.InnerText;
                        // Шукаємо subtitle: "..." або subtitle: '...'
                        var match = Regex.Match(text, "subtitle\\s*:\\s*[\"']([^\"']+)[\"']");
                        if (match.Success)
                        {
                            var subtitleString = match.Groups[1].Value;
                            // Може бути кілька субтитрів через кому
                            var subs = subtitleString.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var sub in subs)
                            {
                                var subMatch = Regex.Match(sub, "\\[(.*?)\\](https?://[^,]+)");
                                if (subMatch.Success)
                                {
                                    var lang = subMatch.Groups[1].Value.Trim();
                                    var url = subMatch.Groups[2].Value.Trim();
                                    subtitles.Append(lang, url);
                                    Console.WriteLine($"UaFlix: Parsed subtitle from JS: {lang} => {url}");
                                }
                            }
                        }
                    }
                }

                Console.WriteLine("UaFlix: All parsed subtitles: " + Newtonsoft.Json.JsonConvert.SerializeObject(subtitles));
                return subtitles;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Subtitle parse error: {ex.Message}");
                return null;
            }
        }

        public class Movie
        {
            public string translation { get; set; }
            public List<(string link, string quality)> links { get; set; }
            public SubtitleTpl? subtitles { get; set; }
        }

        public class Episode
        {
            public string episode { get; set; }
            public List<(string link, string quality)> links { get; set; }
            public string url { get; set; }
            public SubtitleTpl? subtitles { get; set; }
        }

        public class Voice
        {
            public string id { get; set; }
            public string name { get; set; }
            public List<Episode> episodes { get; set; }
        }

        public class Result
        {
            public List<Movie> movie { get; set; }
            public Dictionary<string, List<Voice>> serial { get; set; }
        }
    }
}
