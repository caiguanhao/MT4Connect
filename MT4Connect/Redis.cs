using StackExchange.Redis;
using System;
using System.Collections.Generic;

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
                        ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(Current.Configs.Redis);
                        _Db = redis.GetDatabase(Current.Configs.RedisDatabase);
                    }
                    return _Db;
                }
            }
        }

        private static bool running = false;
        private static readonly object pl = new object();

        public static void KeepKeysAlive()
        {
            lock (pl)
            {
                if (running) return;
                running = true;
                var redisTimer = new System.Timers.Timer(500);
                redisTimer.Elapsed += (_, e) =>
                {
                    foreach (KeyValuePair<uint, FXClient> account in Current.Accounts)
                    {
                        var jsonKey = String.Format("forex:accountjson#{0:D}", account.Key);
                        var setKey = String.Format("forex:account#{0:D}#orders", account.Key);
                        Redis.Db.KeyExpire(jsonKey, Constants.KeyTimeout);
                        Redis.Db.KeyExpire(setKey, Constants.KeyTimeout);
                        var items = Redis.Db.SetMembers(setKey);
                        for (var i = 0; i < items.Length; i++)
                        {
                            Redis.Db.KeyExpire(String.Format("forex:order#{0:D}", items[i]), Constants.KeyTimeout);
                        }
                    }
                };
                redisTimer.Start();
            }
        }
    }
}
