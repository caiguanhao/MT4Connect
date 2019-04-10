using Nancy.Hosting.Self;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MT4Connect
{
    public class Constants
    {
        public static TimeSpan KeyTimeout = TimeSpan.FromSeconds(2);
        public static TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    }

    public class Logger
    {
        public static void Info(string arg0)
        {
            Console.WriteLine("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), arg0);
        }
        
        public static void Info(string format, params object[] arg)
        {
            Console.WriteLine(DateTime.Now.ToString("[HH:mm:ss] ") + format, arg);
        }
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
    }

    class Program
    {
        static void Main(string[] args)
        {
            if (!Redis.Db.Multiplexer.IsConnected)
            {
                throw new Exception("Failed to connect redis");
            }
            OrdersPostgres.Conn.Open();
            InstructionsPostgres.Conn.Open();
            Update();
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exitEvent.Set();
            };
            var hostConfigs = new HostConfiguration
            {
                UrlReservations = new UrlReservations() { CreateAutomatically = true }
            };
            var listen = "localhost:1234";
            if (args.Length > 0) listen = args[0];
            if (!listen.StartsWith("http://")) listen = "http://" + listen;
            Uri uri = new Uri(listen);
            var host = new NancyHost(hostConfigs, uri);
            host.Start();
            Logger.Info("Listening to {0}", listen);
            exitEvent.WaitOne();
            InstructionsPostgres.Conn.Close();
            OrdersPostgres.Conn.Close();
            Redis.Db.Multiplexer.Close();
            Logger.Info("Goodbye!");
        }

        private static void Update()
        {
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

            var pgTimer = new System.Timers.Timer(200);
            pgTimer.Elapsed += (_, e) =>
            {
                if (Current.Accounts.Count == 0) return;
                pgTimer.Stop();
                var orders = new List<Order>();
                InstructionsPostgres.SelectStmt.Parameters["login"].Value = Current.Accounts.Keys.ToArray();
                using (var reader = InstructionsPostgres.SelectStmt.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        orders.Add(new Order()
                        {
                            Id = reader.GetInt64(0),
                            Login = Convert.ToUInt32(reader.GetInt32(1)),
                            Action = reader.GetString(2),
                            Symbol = reader.IsDBNull(3) ? string.Empty: reader.GetString(3),
                            OrderType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                            Volume = reader.IsDBNull(5) ? 0 : Convert.ToDouble(reader.GetDecimal(5)),
                            Price = reader.IsDBNull(6) ? 0 : Convert.ToDouble(reader.GetDecimal(6)),
                            StopLoss = reader.IsDBNull(7) ? 0 : Convert.ToDouble(reader.GetDecimal(7)),
                            TakeProfit = reader.IsDBNull(8) ? 0 : Convert.ToDouble(reader.GetDecimal(8)),
                            Comment = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                            Ticket = reader.IsDBNull(10) ? 0 : reader.GetInt64(10)
                        });
                    }
                }
                if (orders.Count > 0)
                {
                    orders.ForEach((order) =>
                    {
                        order.Process();
                    });
                }
                pgTimer.Start();
            };
            pgTimer.Start();
        }
    }
}
