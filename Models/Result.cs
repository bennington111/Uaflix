using System.Collections.Generic;

namespace Uaflix.Models.UaFlix
{
    public class Result
    {
        public List<Movie> movie { get; set; }

        /// <summary>
        /// сезон, (перевод, серии)
        /// </summary>
        public Dictionary<string, List<Voice>> serial { get; set; }
    }
}
