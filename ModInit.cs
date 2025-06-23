using Lampac;
using Lampac.Models.LITE;

namespace Uaflix
{
    public class ModInit
    {
        public static OnlinesSettings UaFlix;

        /// <summary>
        /// модуль загружен
        /// </summary>
        public static void loaded()
        {
            UaFlix = new OnlinesSettings("UaFlix", "uafix.net", streamproxy: false)
            {
                displayname = "UaFlix"
            };

            // Выводить "уточнить поиск"
            AppInit.conf.online.with_search.Add("uaflix");
        }
    }
}
