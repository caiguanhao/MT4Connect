using Nancy;
using Nancy.Bootstrapper;
using Nancy.Hosting.Self;
using Nancy.ModelBinding;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace MT4Connect
{
    public class Logger : IApplicationStartup
    {
        public void Initialize(IPipelines pipelines)
        {
            pipelines.BeforeRequest.AddItemToStartOfPipeline(ctx =>
            {
                var timer = new Stopwatch();
                ctx.Items["timer"] = timer;
                timer.Start();
                return null;
            });
            pipelines.AfterRequest.AddItemToEndOfPipeline(ctx =>
            {
                var timer = (Stopwatch)ctx.Items["timer"];
                timer.Stop();
                Console.WriteLine("[{0}] {1} {2} - ReqBodySize: {3} - RespStatus: {4} - Duration: {5}ms",
                    DateTime.Now.ToString("HH:mm:ss"), ctx.Request.Method,
                    ctx.Request.Path, ctx.Request.Body.Length,
                    ctx.Response.StatusCode.ToString("D"), timer.ElapsedMilliseconds);
                Console.Out.Flush();
            });
        }
    }

    public class MT4Account
    {
        public bool Connected { get; set; }
        public bool Master { get; set; }
        public uint Login { get; set; }
        public int TradeMode { get; set; }
        public int Leverage { get; set; }
        public int LimitOrders { get; set; }
        public double Balance { get; set; }
        public double Credit { get; set; }
        public double Profit { get; set; }
        public double Equity { get; set; }
        public double Margin { get; set; }
        public double MarginFree { get; set; }
        public string MarginLevel { get; set; }
        public string Currency { get; set; }
        public string Server { get; set; }
        public string AccountName { get; set; }
    }

    public class ApiModule : Nancy.NancyModule
    {
        public class Server
        {
            public string Name { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public long Delay { get; set; }
        }

        public class Account
        {
            public uint Login { get; set; }
            public string Password { get; set; }
            public string ServerName { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
        }

        static Task<PingReply> PingAsync(string address)
        {
            var tcs = new TaskCompletionSource<PingReply>();
            Ping ping = new Ping();
            ping.PingCompleted += (obj, sender) =>
            {
                tcs.SetResult(sender.Reply);
            };
            ping.SendAsync(address, 1000, new object());
            return tcs.Task;
        }

        public ApiModule()
        {
            Post["/server"] = _ =>
            {
                var file = this.Request.Files.FirstOrDefault();
                if (file == null)
                {
                    return Response.AsJson(new Dictionary<string, int?> { { "servers", null } });
                }
                var temp = Path.GetTempFileName();
                using (FileStream stream = File.Create(temp))
                {
                    file.Value.CopyTo(stream);
                }
                TradingAPI.MT4Server.Server[] mt4servers;
                TradingAPI.MT4Server.QuoteClient.LoadSrv(temp, out mt4servers);
                try { File.Delete(temp); } catch { }
                var size = mt4servers.Length;
                Server[] servers = new Server[size];
                List<Task<PingReply>> pingTasks = new List<Task<PingReply>>();
                for (var i = 0; i < size; i++)
                {
                    servers[i] = new Server();
                    servers[i].Name = mt4servers[i].desc;
                    servers[i].Host = mt4servers[i].Host;
                    servers[i].Port = mt4servers[i].Port;
                    pingTasks.Add(PingAsync(servers[i].Host));
                }
                Task.WaitAll(pingTasks.ToArray());
                for (var i = 0; i < size; i++)
                {
                    if (pingTasks[i].Result.Status == IPStatus.Success)
                    {
                        servers[i].Delay = pingTasks[i].Result.RoundtripTime;
                    }
                    else
                    {
                        servers[i].Delay = -1;
                    }
                }
                Array.Sort(servers, (Server a, Server b) => {
                    if (a.Delay < 0 || b.Delay < 0) return b.Delay.CompareTo(a.Delay);
                    return a.Delay.CompareTo(b.Delay);
                });
                return Response.AsJson(new Dictionary<string, Server[]> { { "servers", servers } });
            };

            Post["/login"] = _ =>
            {
                try
                {
                    var account = this.Bind<Account>();
                    if (Current.Accounts.ContainsKey(account.Login))
                    {
                        return Response.AsJson(new Dictionary<string, string> { { "message", "already added" } }, HttpStatusCode.BadRequest);
                    }
                    var client = new FXClient(account.Login, account.Password, account.ServerName, account.Host, account.Port);
                    client.Connect();
                    Current.Accounts.Add(account.Login, client);
                    return Response.AsJson(new Dictionary<string, MT4Account> { { "account", client.AsMT4Account() } });
                }
                catch (Exception ex)
                {
                    return Response.AsJson(new Dictionary<string, string> { { "message", ex.Message } }, HttpStatusCode.Unauthorized);
                }
            };

            Get["/accounts"] = _ =>
            {
                return Response.AsJson(new Dictionary<string, List<MT4Account>> { { "accounts", Current.Accounts.AsMT4Accounts } });
            };
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

    public class FXClients : Dictionary<uint, FXClient>
    {
        public List<MT4Account> AsMT4Accounts
        {
            get
            {
                var accounts = new List<MT4Account>{ };
                foreach (KeyValuePair<uint, FXClient> account in this)
                {
                    accounts.Add(account.Value.AsMT4Account());
                }
                return accounts;
            }
        }
    }

    public class FXClient
    {
        private TradingAPI.MT4Server.QuoteClient Client { get; set; }
        private string ServerName { get; set; }
        private System.Timers.Timer ReconnectTimer;

        public FXClient(uint login, string password, string serverName, string host, int port)
        {
            ServerName = serverName;
            Client = new TradingAPI.MT4Server.QuoteClient(login, password, host, port);
            Client.OnDisconnect += (_, e) =>
            {
                Console.WriteLine("Disconnected to {0}: {1}", Client.User, e.Exception.Message);
                ReconnectTimer = new System.Timers.Timer(3000);
                ReconnectTimer.Elapsed += (__, ee) =>
                {
                    ReconnectTimer.Stop();
                    Console.WriteLine("Reconnecting to {0}...", Client.User);
                    try
                    {
                        Client.Connect();
                    }
                    catch (Exception)
                    {
                        ReconnectTimer.Start();
                    }
                };
                ReconnectTimer.Start();
            };
            Client.OnConnect += (_, e) =>
            {
                if (e.Exception != null)
                {
                    Console.WriteLine("Failed to connect to {0}: {1}", Client.User, e.Exception.Message);
                    return;
                }
                if (Client.Connected)
                {
                    if (ReconnectTimer != null)
                    {
                        ReconnectTimer.Dispose();
                    }
                    Console.WriteLine("Connected to {0}", Client.User);
                    UpdateAccount();
                    UpdateCurrentOrders();
                    InsertHistoryOrders();
                }
            };
            Client.OnOrderUpdate += (_, e) =>
            {
                UpdateAccount();
                switch (e.Action)
                {
                    case TradingAPI.MT4Server.UpdateAction.PendingClose:
                    case TradingAPI.MT4Server.UpdateAction.PositionClose:
                        DeleteCurrentOrder(e.Order);
                        goto case TradingAPI.MT4Server.UpdateAction.Balance;
                    case TradingAPI.MT4Server.UpdateAction.Balance:
                    case TradingAPI.MT4Server.UpdateAction.Credit:
                        var inserted = InsertHistoryOrder(e.Order);
                        Console.WriteLine("Inserted {0} history orders", inserted);
                        break;
                    default:
                        UpdateCurrentOrder(e.Order.Symbol);
                        break;
                }
            };
            Client.OnQuote += (_, e) =>
            {
                UpdateAccount();
                UpdateCurrentOrder(e.Symbol);
            };
        }

        public void Connect()
        {
            Client.Connect();
        }

        public MT4Account AsMT4Account()
        {
            var marginLevel = 0.0;
            if (Client.AccountMargin > 0) marginLevel = Round(Client.AccountEquity / Client.AccountMargin * 100);
            return new MT4Account()
            {
                Connected = Client.Connected,
                Master = Client.AccountMode == 0,
                Login = Client.User,
                TradeMode = Client.IsDemoAccount ? 0 : 2,
                Leverage = Client.AccountLeverage,
                LimitOrders = Client.Account.maxpositions,
                Balance = Round(Client.AccountBalance),
                Credit = Round(Client.AccountCredit),
                Profit = Round(Client.AccountProfit),
                Equity = Round(Client.AccountEquity),
                Margin = Round(Client.AccountMargin),
                MarginFree = Round(Client.AccountFreeMargin),
                MarginLevel = String.Format("{0:0.00}%", marginLevel),
                Currency = Client.Account.currency,
                Server = ServerName,
                AccountName = Client.AccountName,
            };
        }

        private double Round(double number)
        {
            return Math.Round(number, 2, MidpointRounding.AwayFromZero);
        }

        private int InsertHistoryOrder(TradingAPI.MT4Server.Order order)
        {
            var reason = "";
            if (order.Comment.Contains("[sl]")) reason = "sl";
            else if (order.Comment.Contains("[tp]")) reason = "tp";
            Postgres.InsertStmt.Parameters["login"].Value = (long)Client.User;
            Postgres.InsertStmt.Parameters["ticket"].Value = (long)order.Ticket;
            Postgres.InsertStmt.Parameters["order_type"].Value = (short)order.Type;
            Postgres.InsertStmt.Parameters["symbol"].Value = order.Symbol;
            Postgres.InsertStmt.Parameters["open_time"].Value = order.OpenTime.AddSeconds(-7200); // convert UTC+2 to UTC
            Postgres.InsertStmt.Parameters["close_time"].Value = order.CloseTime.AddSeconds(-7200); // convert UTC+2 to UTC
            Postgres.InsertStmt.Parameters["open_price"].Value = order.OpenPrice;
            Postgres.InsertStmt.Parameters["close_price"].Value = order.ClosePrice;
            Postgres.InsertStmt.Parameters["stop_loss"].Value = order.StopLoss;
            Postgres.InsertStmt.Parameters["take_profit"].Value = order.TakeProfit;
            Postgres.InsertStmt.Parameters["reason"].Value = reason;
            Postgres.InsertStmt.Parameters["commission"].Value = order.Commission;
            Postgres.InsertStmt.Parameters["swap"].Value = order.Swap;
            Postgres.InsertStmt.Parameters["volume"].Value = order.Lots;
            Postgres.InsertStmt.Parameters["net_profit"].Value = order.Profit;
            Postgres.InsertStmt.Parameters["profit"].Value = order.Profit + order.Commission + order.Swap;
            return Postgres.InsertStmt.ExecuteNonQuery();
        }

        private void InsertHistoryOrders()
        {
            Postgres.QueryStmt.Parameters["login"].Value = (long)Client.User;
            var from = new DateTime(2010, 1, 1);
            using (var reader = Postgres.QueryStmt.ExecuteReader())
            {
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    from = reader.GetDateTime(0).AddSeconds(7200); // convert UTC to UTC+2
                }
            }
            // from and to of DownloadOrderHistory() is close time in UTC+2
            // but orders returned are sorted by open_time asc
            var orders = Client.DownloadOrderHistory(from, DateTime.Now);
            var size = orders.Length;
            var inserted = 0;
            for (var i = 0; i < size; i++)
            {
                try
                {
                    inserted += InsertHistoryOrder(orders[i]);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    break;
                }
            }
            Console.WriteLine("Inserted {0} history orders", inserted);
        }

        private void UpdateCurrentOrders()
        {
            var setKey = String.Format("forex:account#{0:D}#orders", Client.User);
            var items = Redis.Db.SetMembers(setKey);
            for (var i = 0; i < items.Length; i++)
            {
                Redis.Db.KeyDelete(String.Format("forex:order#{0:D}", items[i]));
            }
            Redis.Db.KeyDelete(setKey);
            var opened = Client.GetOpenedOrders();
            for (var i = 0; i < opened.Length; i++)
            {
                var o = opened[i];
                var key = String.Format("forex:order#{0:D}", o.Ticket);
                var value = String.Format("{0:D}#{1:D}#{2:D}#{3}#{4}#{5}#{6:G}#{7:G}#{8:G}#{9:G}##{10:G}#{11:G}#{12:G}#{13:G}#{14:G}",
                    Client.User, o.Ticket, o.Type, o.Symbol, ToUnix(o.OpenTime), ToUnix(o.CloseTime), o.OpenPrice, o.ClosePrice,
                    o.StopLoss, o.TakeProfit, o.Commission, o.Swap, o.Lots, o.Profit, o.Profit + o.Commission + o.Swap);
                Redis.Db.StringSet(key, value);
                Redis.Db.SetAdd(setKey, o.Ticket);
            }
        }

        private void UpdateCurrentOrder(string Symbol)
        {
            var setKey = String.Format("forex:account#{0:D}#orders", Client.User);
            var opened = Client.GetOpenedOrders();
            for (var i = 0; i < opened.Length; i++)
            {
                var o = opened[i];
                if (o.Symbol != Symbol) continue;
                var key = String.Format("forex:order#{0:D}", o.Ticket);
                var value = String.Format("{0:D}#{1:D}#{2:D}#{3}#{4}#{5}#{6:G}#{7:G}#{8:G}#{9:G}##{10:G}#{11:G}#{12:G}#{13:G}#{14:G}",
                    Client.User, o.Ticket, o.Type, o.Symbol, ToUnix(o.OpenTime), ToUnix(o.CloseTime), o.OpenPrice, o.ClosePrice,
                    o.StopLoss, o.TakeProfit, o.Commission, o.Swap, o.Lots, o.Profit, o.Profit + o.Commission + o.Swap);
                Redis.Db.StringSet(key, value);
                Redis.Db.SetAdd(setKey, o.Ticket);
            }
        }

        private void DeleteCurrentOrder(TradingAPI.MT4Server.Order order)
        {
            Redis.Db.KeyDelete(String.Format("forex:order#{0:D}", order.Ticket));
            Redis.Db.StringSet(String.Format("forex:deleteorder#{0:D}", order.Ticket), Client.User, TimeSpan.FromSeconds(10));
            Redis.Db.SetRemove(String.Format("forex:account#{0:D}#orders", Client.User), order.Ticket);
        }

        private void UpdateAccount()
        {
            var key = String.Format("forex:account#{0:D}", Client.User);
            var marginLevel = 0.0;
            if (Client.AccountMargin > 0) marginLevel = Math.Round(Client.AccountEquity / Client.AccountMargin * 100, 2, MidpointRounding.AwayFromZero);
            var value = String.Format("{0:D}#{1:D}#{2:D}#{3:D}#{4:G}#{5:G}#{6:G}#{7:G}#{8:G}#{9:G}#{10:G}#{11}#{12}#{13}",
                    Client.User, Client.IsDemoAccount ? 0 : 2, Client.AccountLeverage, Client.Account.maxpositions, Client.AccountBalance,
                    Client.AccountCredit, Client.AccountProfit, Client.AccountEquity, Client.AccountMargin, Client.AccountFreeMargin, marginLevel,
                    Client.Account.currency, ServerName, Client.AccountName);
            Redis.Db.StringSet(key, value);
        }

        private double ToUnix(DateTime dateTime)
        {
            return (dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;
        }
    }

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

    public sealed class Postgres
    {
        private static NpgsqlConnection _Conn = null;
        private static NpgsqlCommand _QueryStmt = null;
        private static NpgsqlCommand _InsertStmt = null;
        private static readonly object padlock = new object();

        public static NpgsqlConnection Conn
        {
            get
            {
                lock (padlock)
                {
                    if (_Conn == null)
                    {
                        var connString = "Host=10.211.55.2; Username=tcp; Password=; Database=forex_mtdata";
                        _Conn = new NpgsqlConnection(connString);
                    }
                    return _Conn;
                }
            }
        }

        public static NpgsqlCommand QueryStmt
        {
            get
            {
                lock (padlock)
                {
                    if (_QueryStmt == null)
                    {
                        _QueryStmt = new NpgsqlCommand("SELECT MAX(close_time) FROM account_orders WHERE login=@login", Conn);
                        _QueryStmt.Parameters.Add("login", NpgsqlDbType.Bigint);
                        _QueryStmt.Prepare();
                    }
                    return _QueryStmt;
                }
            }
        }

        public static NpgsqlCommand InsertStmt
        {
            get
            {
                lock (padlock)
                {
                    if (_InsertStmt == null)
                    {
                        _InsertStmt = new NpgsqlCommand("INSERT INTO account_orders (" +
                            "login, ticket, order_type, symbol, open_time, close_time, open_price, close_price, " +
                            "stop_loss, take_profit, reason, commission, swap, volume, net_profit, profit) VALUES (" +
                            "@login, @ticket, @order_type, @symbol, @open_time, @close_time, @open_price, @close_price, " +
                            "@stop_loss, @take_profit, @reason, @commission, @swap, @volume, @net_profit, @profit) ON CONFLICT (ticket) DO NOTHING", Conn);
                        _InsertStmt.Parameters.Add("login", NpgsqlDbType.Bigint);
                        _InsertStmt.Parameters.Add("ticket", NpgsqlDbType.Bigint);
                        _InsertStmt.Parameters.Add("order_type", NpgsqlDbType.Smallint);
                        _InsertStmt.Parameters.Add("symbol", NpgsqlDbType.Varchar);
                        _InsertStmt.Parameters.Add("open_time", NpgsqlDbType.Timestamp);
                        _InsertStmt.Parameters.Add("close_time", NpgsqlDbType.Timestamp);
                        _InsertStmt.Parameters.Add("open_price", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("close_price", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("stop_loss", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("take_profit", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("reason", NpgsqlDbType.Varchar);
                        _InsertStmt.Parameters.Add("commission", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("swap", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("volume", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("net_profit", NpgsqlDbType.Numeric);
                        _InsertStmt.Parameters.Add("profit", NpgsqlDbType.Numeric);
                        _InsertStmt.Prepare();
                    }
                    return _InsertStmt;
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
            Postgres.Conn.Open();
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) {
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
            Console.WriteLine(listen);
            Uri uri = new Uri(listen);
            var host = new NancyHost(hostConfigs, uri);
            host.Start();
            Console.WriteLine("Listening to {0}", listen);
            exitEvent.WaitOne();
            Postgres.Conn.Close();
            Redis.Db.Multiplexer.Close();
            Console.WriteLine("Goodbye!");
        }
    }
}
