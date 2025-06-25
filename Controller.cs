using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Web;
using System.Linq;
using Shared.Engine.CORE;
using Lampac.Engine;
using Shared.Model.Templates;
using Uaflix.Models.UaFlix;

namespace Uaflix.Controllers
{
    public class UaFlix : BaseController
    {
        ProxyManager proxyManager = new ProxyManager(ModInit.UaFlix);

        [HttpGet]
        [Route("uaflix")]
        async public Task<ActionResult> Index(long id, string imdb_id, long kinopoisk_id, string title, string original_title, string original_language, int year, string source, int serial, string account_email, string t, int s = -1, bool rjson = false)
        {
            var init = ModInit.UaFlix;
            if (!init.enable)
                return Forbid();

            var proxy = proxyManager.Get();

            var result = await search(imdb_id, kinopoisk_id, serial);
            if (result == null)
            {
                proxyManager.Refresh();
                return Ok();
            }

            if (result.movie != null)
            {
                var tpl = new MovieTpl(title, original_title);

                foreach (var movie in result.movie)
                {
                    var streamquality = new StreamQualityTpl();
                    foreach (var item in movie.links)
                        streamquality.Append(HostStreamProxy(init, item.link, proxy: proxy), item.quality);

                    tpl.Append(movie.translation, streamquality.Firts().link, quality: streamquality.Firts().quality, streamquality: streamquality);
                }

                if (rjson)
                    return Content(tpl.ToJson(), "application/json; charset=utf-8");

                return Content(tpl.ToHtml(), "text/html; charset=utf-8");
            }
            else
            {
                if (result.serial == null)
                {
                    proxyManager.Refresh();
                    return Ok();
                }

                string defaultargs = $"&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&serial={serial}";

                if (s == -1)
                {
                    var tpl = new SeasonTpl(quality: "4K HDR");

                    foreach (var season in result.serial)
                        tpl.Append($"{season.Key} сезон", $"{host}/uaflix?s={season.Key}" + defaultargs, season.Key);

                    if (rjson)
                        return Content(tpl.ToJson(), "application/json; charset=utf-8");

                    return Content(tpl.ToHtml(), "text/html; charset=utf-8");
                }
                else
                {
                    var vtpl = new VoiceTpl();
                    var etpl = new EpisodeTpl();

                    string activTranslate = t;

                    foreach (var translation in result.serial[s.ToString()])
                    {
                        if (string.IsNullOrEmpty(activTranslate))
                            activTranslate = translation.id;

                        vtpl.Append(translation.name, activTranslate == translation.id, $"{host}/uaflix?s={s}&t={translation.id}" + defaultargs);
                    }

                    foreach (var episode in result.serial[s.ToString()].First(i => i.id == activTranslate).episodes)
                    {
                        var streamquality = new StreamQualityTpl();
                        foreach (var item in episode.links)
                            streamquality.Append(HostStreamProxy(init, item.link, proxy: proxy), item.quality);

                        etpl.Append($"{episode.id} серия", $"{title ?? original_title} ({episode.id} серия)", s.ToString(), episode.id, streamquality.Firts().link, streamquality: streamquality);
                    }

                    if (rjson)
                        return Content(etpl.ToJson(vtpl), "application/json; charset=utf-8");

                    return Content(vtpl.ToHtml() + etpl.ToHtml(), "text/html; charset=utf-8");
                }
            }
        }


        async ValueTask<Result> search(string imdb_id, long kinopoisk_id, int serial)
        {
            string memKey = $"UaFlix:view:{kinopoisk_id}:{imdb_id}";
            if (!hybridCache.TryGetValue(memKey, out Result res))
            {
                await Task.Delay(1000); // имитация поиска

                var defaultLinks = new List<(string link, string quality)>()
                {
                    ("https://www.elecard.com/storage/video/TheaterSquare_3840x2160.mp4", "2160p"),
                    ("https://www.elecard.com/storage/video/TheaterSquare_1920x1080.mp4", "1080p"),
                    ("https://www.elecard.com/storage/video/TheaterSquare_1280x720.mp4", "720p")
                };

                if (serial == 0)
                {
                    res = new Result()
                    {
                        movie = new List<Movie>()
                        { 
                            new Movie()
                            {
                                translation = "RHS",
                                links = defaultLinks
                            },
                            new Movie()
                            {
                                translation = "ViruseProject",
                                links = defaultLinks
                            }
                        }
                    };
                }
                else
                {
                    res = new Result()
                    {
                        serial = new Dictionary<string, List<Voice>>() 
                        {
                            ["1"] = new List<Voice>() 
                            {
                                new Voice()
                                {
                                    id = "36",
                                    name = "ViruseProject",
                                    episodes = new List<Serial> 
                                    {
                                        new Serial() 
                                        { 
                                            id = "1",
                                            links = defaultLinks
                                        },
                                        new Serial()
                                        {
                                            id = "2",
                                            links = defaultLinks
                                        },
                                        new Serial()
                                        {
                                            id = "3",
                                            links = defaultLinks
                                        }
                                    }
                                },
                                new Voice()
                                {
                                    id = "12",
                                    name = "RHS",
                                    episodes = new List<Serial>
                                    {
                                        new Serial()
                                        {
                                            id = "1",
                                            links = defaultLinks
                                        },
                                        new Serial()
                                        {
                                            id = "2",
                                            links = defaultLinks
                                        }
                                    }
                                }
                            },
                            ["2"] = new List<Voice>()
                            {
                                new Voice()
                                {
                                    id = "36",
                                    name = "ViruseProject",
                                    episodes = new List<Serial>
                                    {
                                        new Serial()
                                        {
                                            id = "1",
                                            links = defaultLinks
                                        }
                                    }
                                },
                                new Voice()
                                {
                                    id = "12",
                                    name = "RHS",
                                    episodes = new List<Serial>
                                    {
                                        new Serial()
                                        {
                                            id = "1",
                                            links = defaultLinks
                                        }
                                    }
                                }
                            }
                        }
                    };
                }

                proxyManager.Success();
                hybridCache.Set(memKey, res, cacheTime(5));
            }

            return res;
        }
    }
}
