using StackExchange.Redis;

namespace MT4Connect
{
    public sealed class Redis
    {
        private static IDatabase _Db = null;
        private static readonly object padlock = new object();

        public static IDatabase Db
        {
            get
            {
                lock (padlock)
                {
                    if (_Db == null)
                    {
                        ConnectionMultiplexer redis = ConnectionMultiplexer.Connect("10.211.55.2,abortConnect=false");
                        _Db = redis.GetDatabase(15);
                    }
                    return _Db;
                }
            }
        }
    }
}
