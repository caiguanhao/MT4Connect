using System.IO;

namespace MT4Connect
{
    public class Configs
    {
        public string Listen { get; set; }
        public string Postgres { get; set; }
        public string Redis { get; set; }
        public int RedisDatabase { get; set; }
        public string InfluxAddress { get; set; }
        public string InfluxDatabase { get; set; }
    }

    public sealed class Current
    {
        private static FXClients _accounts;
        private static readonly object padlock = new object();

        public static FXClients Accounts
        {
            get
            {
                lock (padlock)
                {
                    if (_accounts == null)
                    {
                        _accounts = new FXClients { };
                    }
                    return _accounts;
                }
            }
        }

        private static Configs _configs = null;
        private static readonly object pl = new object();

        public static Configs Configs
        {
            get
            {
                lock (pl)
                {
                    if (_configs == null)
                    {
                        var serializer = new Nancy.Json.JavaScriptSerializer();
                        if (!File.Exists(Constants.ConfigsFile))
                        {
                            var content = serializer.Serialize(new Configs()
                            {
                                Listen = "http://localhost:1234",
                                Postgres = "Host=127.0.0.1; Username=postgres; Password=; Database=forex_mtdata",
                                Redis = "127.0.0.1, abortConnect = false",
                                RedisDatabase = 15,
                                InfluxAddress = "http://127.0.0.1:8086",
                                InfluxDatabase = "forex",
                            });
                            // prettify
                            content = content.Replace("{", "{\r\n  ").Replace("\":", "\": ").Replace(",\"", ",\r\n  \"").Replace("\"}", "\"\r\n}");
                            File.WriteAllText(Constants.ConfigsFile, content);
                        }
                        _configs = serializer.Deserialize<Configs>(File.ReadAllText(Constants.ConfigsFile));

                    }
                    return _configs;
                }
            }
        }
    }
}
