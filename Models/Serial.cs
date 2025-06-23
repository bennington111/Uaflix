using System.Collections.Generic;

namespace Uaflix.Models.UaFlix
{
    public class Serial
    {
        public string id { get; set; }

        public List<(string link, string quality)> links { get; set; }
    }
}
