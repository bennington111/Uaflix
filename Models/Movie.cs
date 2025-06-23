using System.Collections.Generic;

namespace Uaflix.Models.UaFlix
{
    public class Movie
    {
        public string translation { get; set; }

        public List<(string link, string quality)> links { get; set; }
    }
}
