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

    public class NancyLogger : IApplicationStartup
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
                        var c = Current.Accounts[account.Login];
                        return Response.AsJson(new Dictionary<string, object> { { "message", "already added" }, { "account", c.AsMT4Account() } }, HttpStatusCode.BadRequest);
                    }
                    Logger.Info("Connecting to {0}...", account.Login);
                    var client = new FXClient(account.Login, account.Password, account.ServerName, account.Host, account.Port);
                    client.Connect();
                    if (client.IsMaster())
                    {
                        Current.Accounts.Add(account.Login, client);
                    }
                    else
                    {
                        client.Disconnect();
                    }
                    return Response.AsJson(new Dictionary<string, MT4Account> { { "account", client.AsMT4Account() } });
                }
                catch (Exception ex)
                {
                    return Response.AsJson(new Dictionary<string, string> { { "message", ex.Message } }, HttpStatusCode.Unauthorized);
                }
            };

            Post["/logout"] = _ =>
            {
                try
                {
                    var account = this.Bind<Account>();
                    if (!Current.Accounts.ContainsKey(account.Login))
                    {
                        return Response.AsJson(new Dictionary<string, string> { { "message", "no such account" } });
                    }
                    Current.Accounts[account.Login].Disconnect();
                    Current.Accounts.Remove(account.Login);
                    return Response.AsJson(new Dictionary<string, string> { { "message", "disconnected" } });
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
                var accounts = new List<MT4Account> { };
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
        public TradingAPI.MT4Server.QuoteClient Client { get; set; }
        private string ServerName { get; set; }
        private System.Timers.Timer ReconnectTimer;

        public FXClient(uint login, string password, string serverName, string host, int port)
        {
            ServerName = serverName;
            Client = new TradingAPI.MT4Server.QuoteClient(login, password, host, port);
            Client.OnDisconnect += (_, e) =>
            {
                if (!IsMaster()) return;
                Logger.Info("Disconnected to {0}: {1}", Client.User, e.Exception.Message);
                ReconnectTimer = new System.Timers.Timer(3000);
                ReconnectTimer.Elapsed += (__, ee) =>
                {
                    ReconnectTimer.Stop();
                    Logger.Info("Reconnecting to {0}...", Client.User);
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
                if (!IsMaster()) return;
                if (e.Exception != null)
                {
                    Logger.Info("Failed to connect to {0}: {1}", Client.User, e.Exception.Message);
                    return;
                }
                if (Client.Connected)
                {
                    if (ReconnectTimer != null)
                    {
                        ReconnectTimer.Dispose();
                    }
                    Logger.Info("Connected to {0}", Client.User);
                    UpdateAccount();
                    UpdateCurrentOrders();
                    InsertHistoryOrders();
                }
            };
            Client.OnOrderUpdate += (_, e) =>
            {
                if (!IsMaster()) return;
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
                        Logger.Info("{0} inserted {1} history orders", Client.User, inserted);
                        break;
                    default:
                        UpdateCurrentOrder(e.Order.Symbol);
                        break;
                }
            };
            Client.OnQuote += (_, e) =>
            {
                if (!IsMaster()) return;
                UpdateAccount();
                UpdateCurrentOrder(e.Symbol);
            };
        }

        public bool IsMaster()
        {
            return Client.AccountMode == 0;
        }

        public void Connect()
        {
            Client.Connect();
        }

        public void Disconnect()
        {
            Client.Disconnect();
            var jsonKey = String.Format("forex:accountjson#{0:D}", Client.User);
            var setKey = String.Format("forex:account#{0:D}#orders", Client.User);
            var items = Redis.Db.SetMembers(setKey);
            for (var i = 0; i < items.Length; i++)
            {
                Redis.Db.KeyDelete(String.Format("forex:order#{0:D}", items[i]));
            }
            Redis.Db.KeyDelete(setKey);
            Redis.Db.KeyDelete(jsonKey);
        }

        public MT4Account AsMT4Account()
        {
            var marginLevel = 0.0;
            if (Client.AccountMargin > 0) marginLevel = Round(Client.AccountEquity / Client.AccountMargin * 100);
            return new MT4Account()
            {
                Connected = Client.Connected,
                Master = IsMaster(),
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
            OrdersPostgres.InsertStmt.Parameters["login"].Value = (long)Client.User;
            OrdersPostgres.InsertStmt.Parameters["ticket"].Value = (long)order.Ticket;
            OrdersPostgres.InsertStmt.Parameters["order_type"].Value = (short)order.Type;
            OrdersPostgres.InsertStmt.Parameters["symbol"].Value = order.Symbol;
            OrdersPostgres.InsertStmt.Parameters["open_time"].Value = order.OpenTime.AddSeconds(-7200); // convert UTC+2 to UTC
            OrdersPostgres.InsertStmt.Parameters["close_time"].Value = order.CloseTime.AddSeconds(-7200); // convert UTC+2 to UTC
            OrdersPostgres.InsertStmt.Parameters["open_price"].Value = order.OpenPrice;
            OrdersPostgres.InsertStmt.Parameters["close_price"].Value = order.ClosePrice;
            OrdersPostgres.InsertStmt.Parameters["stop_loss"].Value = order.StopLoss;
            OrdersPostgres.InsertStmt.Parameters["take_profit"].Value = order.TakeProfit;
            OrdersPostgres.InsertStmt.Parameters["reason"].Value = reason;
            OrdersPostgres.InsertStmt.Parameters["commission"].Value = order.Commission;
            OrdersPostgres.InsertStmt.Parameters["swap"].Value = order.Swap;
            OrdersPostgres.InsertStmt.Parameters["volume"].Value = order.Lots;
            OrdersPostgres.InsertStmt.Parameters["net_profit"].Value = order.Profit;
            OrdersPostgres.InsertStmt.Parameters["profit"].Value = order.Profit + order.Commission + order.Swap;
            return OrdersPostgres.InsertStmt.ExecuteNonQuery();
        }

        private void InsertHistoryOrders()
        {
            OrdersPostgres.QueryStmt.Parameters["login"].Value = (long)Client.User;
            var from = new DateTime(2010, 1, 1);
            using (var reader = OrdersPostgres.QueryStmt.ExecuteReader())
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
                    Logger.Info(ex.Message);
                    break;
                }
            }
            Logger.Info("{0} inserted {1} history orders", Client.User, inserted);
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
                Redis.Db.StringSet(key, value, Constants.KeyTimeout);
                Redis.Db.SetAdd(setKey, o.Ticket);
                Redis.Db.KeyExpire(setKey, Constants.KeyTimeout);
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
                Redis.Db.StringSet(key, value, Constants.KeyTimeout);
                Redis.Db.SetAdd(setKey, o.Ticket);
            }
        }

        private void DeleteCurrentOrder(TradingAPI.MT4Server.Order order)
        {
            Redis.Db.KeyDelete(String.Format("forex:order#{0:D}", order.Ticket));
            Redis.Db.StringSet(String.Format("forex:deleteorder#{0:D}", order.Ticket), Client.User, Constants.KeyTimeout);
            Redis.Db.SetRemove(String.Format("forex:account#{0:D}#orders", Client.User), order.Ticket);
        }

        private void UpdateAccount()
        {
            var key = String.Format("forex:account#{0:D}", Client.User);
            var marginLevel = 0.0;
            if (Client.AccountMargin > 0) marginLevel = Round(Client.AccountEquity / Client.AccountMargin * 100);
            var value = String.Format("{0:D}#{1:D}#{2:D}#{3:D}#{4:G}#{5:G}#{6:G}#{7:G}#{8:G}#{9:G}#{10:G}#{11}#{12}#{13}",
                    Client.User, Client.IsDemoAccount ? 0 : 2, Client.AccountLeverage, Client.Account.maxpositions,
                    Round(Client.AccountBalance), Round(Client.AccountCredit), Round(Client.AccountProfit),
                    Round(Client.AccountEquity), Round(Client.AccountMargin), Round(Client.AccountFreeMargin), marginLevel,
                    Client.Account.currency, ServerName, Client.AccountName);
            Redis.Db.StringSet(key, value);
            var jsonKey = String.Format("forex:accountjson#{0:D}", Client.User);
            var json = new Nancy.Json.JavaScriptSerializer().Serialize(this.AsMT4Account());
            Redis.Db.StringSet(jsonKey, json, Constants.KeyTimeout);
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

    public sealed class OrdersPostgres
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

    public sealed class InstructionsPostgres
    {
        private static NpgsqlConnection _Conn = null;
        private static NpgsqlCommand _SelectStmt = null;
        private static NpgsqlCommand _UpdateStmt = null;
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

        public static NpgsqlCommand SelectStmt
        {
            get
            {
                lock (padlock)
                {
                    if (_SelectStmt == null)
                    {
                        _SelectStmt = new NpgsqlCommand("SELECT id, login, action, symbol, order_type, volume, price, stop_loss, take_profit, comment, ticket " +
                            "FROM instructions WHERE created_at > NOW() - INTERVAL '1 minute' AND executed_at IS NULL AND login = ANY(@login) ORDER BY created_at ASC", Conn);
                        _SelectStmt.Parameters.Add("login", NpgsqlDbType.Array | NpgsqlDbType.Integer);
                        _SelectStmt.Prepare();
                    }
                    return _SelectStmt;
                }
            }
        }

        public static NpgsqlCommand UpdateStmt
        {
            get
            {
                lock (padlock)
                {
                    if (_UpdateStmt == null)
                    {
                        _UpdateStmt = new NpgsqlCommand("UPDATE instructions SET executed_at = NOW(), ticket = @ticket, error = @error " +
                            "WHERE id = @id", Conn);
                        _UpdateStmt.Parameters.Add("id", NpgsqlDbType.Bigint);
                        _UpdateStmt.Parameters.Add("ticket", NpgsqlDbType.Integer);
                        _UpdateStmt.Parameters.Add("error", NpgsqlDbType.Varchar);
                        _UpdateStmt.Prepare();
                    }
                    return _UpdateStmt;
                }
            }
        }
    }

    public class Order
    {
        public long Id { get; set; }
        public uint Login { get; set; }
        public string Action { get; set; }
        private string _symbol;
        public string Symbol {
            get => _symbol;
            set => _symbol = value.ToUpper();
        }
        public string OrderType { get; set; }
        public double Price { get; set; }
        public double Volume { get; set; }
        public double StopLoss { get; set; }
        public double TakeProfit { get; set; }
        public string Comment { get; set; }
        public long Ticket { get; set; }

        public void Process()
        {
            try
            {
                if (Action == "Open")
                {
                    Open();
                }
                else if (Action == "Modify")
                {
                    Modify();
                }
                else if (Action == "Close")
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                Report(ex.Message);
            }
        }

        private void Close()
        {
            var oc = new TradingAPI.MT4Server.OrderClient(TheQuoteClient);
            var opened = TheQuoteClient.GetOpenedOrders();
            var closed = 0;
            for (var i = 0; i < opened.Length; i++)
            {
                var o = opened[i];
                if (Ticket > 0 && Ticket != o.Ticket) continue;
                if (Symbol != "" && Symbol != o.Symbol) continue;
                if (OrderType != "" && TypeToOp(OrderType) != o.Type) continue;
                if (Volume > 0 && Volume != o.Lots) continue;
                int tried = 0;
                while (true)
                {
                    try
                    {
                        if (o.Type == TradingAPI.MT4Server.Op.Buy || o.Type == TradingAPI.MT4Server.Op.Sell)
                        {
                            var task = Task.Run(() => oc.OrderClose(o.Symbol, o.Ticket, o.Lots, o.ClosePrice, 5));
                            if (!task.Wait(Constants.CommandTimeout)) throw new Exception("timed out");
                        }
                        else
                        {
                            var task = Task.Run(() => oc.OrderDelete(o.Ticket, o.Type, o.Symbol, o.Lots, o.ClosePrice));
                            if (!task.Wait(Constants.CommandTimeout)) throw new Exception("timed out");
                        }
                        closed++;
                        break;
                    }
                    catch
                    {
                        tried++;
                        if (tried > 3)
                        {
                            throw;
                        }
                        Task.Delay(TimeSpan.FromMilliseconds(200)).Wait();
                    }
                }
            }
            
            if (closed > 0)
            {
                Report();
            }
            else
            {
                Report("no matches");
            }
            Logger.Info("{0} closed {1} orders (ID#{2})", Login, closed, Id);
        }

        private void Modify()
        {
            var oc = new TradingAPI.MT4Server.OrderClient(TheQuoteClient);
            var opened = TheQuoteClient.GetOpenedOrders();
            var modified = 0;
            for (var i = 0; i < opened.Length; i++)
            {
                var o = opened[i];
                if (Ticket > 0 && Ticket != o.Ticket) continue;
                if (Symbol != "" && Symbol != o.Symbol) continue;
                if (OrderType != "" && TypeToOp(OrderType) != o.Type) continue;
                if (Volume > 0 && Volume != o.Lots) continue;
                if (o.Type == TradingAPI.MT4Server.Op.Buy || o.Type == TradingAPI.MT4Server.Op.Sell)
                {
                    Price = o.OpenPrice;
                }
                int tried = 0;
                while (true)
                {
                    try
                    {
                        var pst = GetPST(getQuote: false, symbol: o.Symbol, order_type: OpToType(o.Type));
                        if (pst.Item1 != o.OpenPrice || pst.Item2 != o.StopLoss || pst.Item3 != o.TakeProfit)
                        {
                            var task = Task.Run(() => oc.OrderModify(o.Type, o.Ticket, pst.Item1, pst.Item2, pst.Item3, new DateTime()));
                            if (!task.Wait(Constants.CommandTimeout)) throw new Exception("timed out");
                            modified++;
                        }
                        break;
                    }
                    catch
                    {
                        tried++;
                        if (tried > 3)
                        {
                            throw;
                        }
                        Task.Delay(TimeSpan.FromMilliseconds(200)).Wait();
                    }
                }
            }

            if (modified > 0)
            {
                Report();
            }
            else
            {
                Report("no matches or changes");
            }
            Logger.Info("{0} modified {1} orders (ID#{2})", Login, modified, Id);
        }

        private void Open()
        {
            var oc = new TradingAPI.MT4Server.OrderClient(TheQuoteClient);
            var op = TypeToOp(OrderType);
            TradingAPI.MT4Server.Order newOrder;
            int tried = 0;
            while (true)
            {
                try
                {
                    var pst = GetPST(getQuote: true);
                    var task = Task.Run(() => oc.OrderSend(Symbol, op, Volume, pst.Item1, 5, pst.Item2, pst.Item3, Comment, 0, new DateTime()));
                    if (!task.Wait(Constants.CommandTimeout)) throw new Exception("timed out");
                    newOrder = task.Result;
                    break;
                }
                catch
                {
                    tried++;
                    if (tried > 3)
                    {
                        throw;
                    }
                    Task.Delay(TimeSpan.FromMilliseconds(200)).Wait();
                }
            }
            Ticket = newOrder.Ticket;
            Report();
            Logger.Info("{0} created order #{1} ({2},{3},{4}) (ID#{5})", Login, Ticket, Symbol, OrderType, Volume, Id);
        }

        private void Report(object error = null)
        {
            InstructionsPostgres.UpdateStmt.Parameters["id"].Value = Id;
            if (Ticket > 0)
            {
                InstructionsPostgres.UpdateStmt.Parameters["ticket"].Value = Ticket;
            }
            else
            {
                InstructionsPostgres.UpdateStmt.Parameters["ticket"].Value = DBNull.Value;
            }
            if (error == null) error = DBNull.Value;
            InstructionsPostgres.UpdateStmt.Parameters["error"].Value = error;
            InstructionsPostgres.UpdateStmt.ExecuteNonQuery();
        }

        private Tuple<double, double, double> GetPST(bool getQuote = true, string symbol = null, string order_type = null)
        {
            if (symbol == null) symbol = Symbol;
            if (order_type == null) order_type = OrderType;
            var pips = 0.0001;
            if (symbol.Contains("JPY") || symbol == "XAGUSD") pips = 0.01;
            if (symbol == "XAUUSD") pips = 0.1;
            if (getQuote)
            {
                while (TheQuoteClient.GetQuote(symbol) == null)
                {
                    Thread.Sleep(10);
                }
            }
            double price = Price;
            double stop_loss = StopLoss;
            double take_profit = TakeProfit;
            switch (order_type)
            {
                case "BUY":
                    if (getQuote)
                    {
                        price = TheQuoteClient.GetQuote(symbol).Ask;
                    }
                    goto case "BUYLIMIT";
                case "BUYLIMIT":
                case "BUYSTOP":
                    if (stop_loss != 0) stop_loss = price - stop_loss * pips;
                    if (take_profit != 0) take_profit = price + take_profit * pips;
                    break;
                case "SELL":
                    if (getQuote)
                    {
                        price = TheQuoteClient.GetQuote(symbol).Bid;
                    }
                    goto case "SELLLIMIT";
                case "SELLLIMIT":
                case "SELLSTOP":
                    if (stop_loss != 0) stop_loss = price + stop_loss * pips;
                    if (take_profit != 0) take_profit = price - take_profit * pips;
                    break;
            }
            return Tuple.Create(price, stop_loss, take_profit);
        }

        private TradingAPI.MT4Server.Op TypeToOp(string type)
        {
            switch (type)
            {
                case "BUY":
                    return TradingAPI.MT4Server.Op.Buy;
                case "BUYLIMIT":
                    return TradingAPI.MT4Server.Op.BuyLimit;
                case "BUYSTOP":
                    return TradingAPI.MT4Server.Op.BuyStop;
                case "SELL":
                    return TradingAPI.MT4Server.Op.Sell;
                case "SELLLIMIT":
                    return TradingAPI.MT4Server.Op.SellLimit;
                case "SELLSTOP":
                    return TradingAPI.MT4Server.Op.SellStop;
                default:
                    throw new Exception("unknown order type");
            }
        }

        private string OpToType(TradingAPI.MT4Server.Op op)
        {
            switch (op)
            {
                case TradingAPI.MT4Server.Op.Buy:
                    return "BUY";
                case TradingAPI.MT4Server.Op.BuyLimit:
                    return "BUYLIMIT";
                case TradingAPI.MT4Server.Op.BuyStop:
                    return "BUYSTOP";
                case TradingAPI.MT4Server.Op.Sell:
                    return "SELL";
                case TradingAPI.MT4Server.Op.SellLimit:
                    return "SELLLIMIT";
                case TradingAPI.MT4Server.Op.SellStop:
                    return "SELLSTOP";
                default:
                    throw new Exception("unknown order type");
            }
        }

        private TradingAPI.MT4Server.QuoteClient TheQuoteClient
        {
            get => Current.Accounts[Login].Client;
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
