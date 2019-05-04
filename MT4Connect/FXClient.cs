using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MT4Connect
{
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
        public bool MoveStopLoss { get; set; }
        public bool AddToLosingPosition { get; set; }
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

        public bool MoveStopLoss { get; set; }
        public bool AddToLosingPosition { get; set; }

        private string ServerName { get; set; }
        private System.Timers.Timer ReconnectTimer;
        private System.Timers.Timer ModifyTimer;
        private System.Timers.Timer ResumeLPTimer;

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
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("Invalid account")) // account has changed password
                        {
                            Current.Accounts.Remove(Client.User);
                            ReconnectTimer.Dispose();
                        }
                        else
                        {
                            ReconnectTimer.Start();
                        }
                    }
                };
                ReconnectTimer.Start();
                if (ModifyTimer != null)
                {
                    ModifyTimer.Stop();
                    ModifyTimer.Dispose();
                }
                if (ResumeLPTimer != null)
                {
                    ResumeLPTimer.Stop();
                    ResumeLPTimer.Dispose();
                }
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
                        ReconnectTimer.Stop();
                        ReconnectTimer.Dispose();
                    }
                    var features = "";
                    if (MoveStopLoss) features += ", MoveStopLoss enabled";
                    if (AddToLosingPosition) features += ", AddToLosingPosition enabled";
                    Logger.Info("Connected to {0}{1}", Client.User, features);

                    // init
                    Redis.Db.StringSet(String.Format("forex:live#{0:D}", Client.User), "", Constants.KeyTimeout);
                    UpdateAccount();
                    UpdateCurrentOrders();
                    InsertHistoryOrders();

                    // update orders
                    if (ModifyTimer != null)
                    {
                        ModifyTimer.Stop();
                        ModifyTimer.Dispose();
                    }
                    ModifyTimer = new System.Timers.Timer(500);
                    ModifyTimer.Elapsed += (__, ee) =>
                    {
                        ModifyTimer.Stop();
                        try
                        {
                            DynamicallyUpdateOrders();
                        }
                        catch (Exception ex)
                        {
                            Logger.Info("{0} Error {1}", Client.User, ex.Message);
                        }
                        finally
                        {
                            ModifyTimer.Start();
                        }
                    };
                    ModifyTimer.Start();
                }
            };
            Client.OnOrderUpdate += (_, e) =>
            {
                if (!IsMaster()) return;
                Logger.Info("{0} updating order #{1}", Client.User, e.Order.Ticket);
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
            var setKey = String.Format("forex:account#{0:D}#orders", Client.User);
            var items = Redis.Db.SetMembers(setKey);
            for (var i = 0; i < items.Length; i++)
            {
                Redis.Db.KeyDelete(String.Format("forex:order#{0:D}", items[i]));
            }
            Redis.Db.KeyDelete(setKey);
            Redis.Db.KeyDelete(String.Format("forex:accountjson#{0:D}", Client.User));
            Redis.Db.KeyDelete(String.Format("forex:account#{0:D}", Client.User));
            Redis.Db.KeyDelete(String.Format("forex:live#{0:D}", Client.User));
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
                MoveStopLoss = MoveStopLoss,
                AddToLosingPosition = AddToLosingPosition,
            };
        }

        private double Round(double number)
        {
            return Math.Round(number, 2, MidpointRounding.AwayFromZero);
        }

        private static readonly object padlock = new object();

        private int InsertHistoryOrder(TradingAPI.MT4Server.Order order)
        {
            lock (padlock)
            {
                var reason = "";
                if (order.Comment.Contains("[sl]")) reason = "sl";
                else if (order.Comment.Contains("[tp]")) reason = "tp";
                OrdersPostgres.InsertStmt.Parameters["login"].Value = (long)Client.User;
                OrdersPostgres.InsertStmt.Parameters["ticket"].Value = (long)order.Ticket;
                OrdersPostgres.InsertStmt.Parameters["order_type"].Value = (short)order.Type;
                OrdersPostgres.InsertStmt.Parameters["symbol"].Value = order.Symbol;
                OrdersPostgres.InsertStmt.Parameters["open_time"].Value = ToUTC(order.OpenTime);
                OrdersPostgres.InsertStmt.Parameters["close_time"].Value = ToUTC(order.CloseTime);
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
            Logger.Info("{0} downloaded {1} histories", Client.User, size);
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
                    Client.User, o.Ticket, o.Type, o.Symbol, ToUnix(ToUTC(o.OpenTime)), 0, o.OpenPrice, o.ClosePrice,
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
                    Client.User, o.Ticket, o.Type, o.Symbol, ToUnix(ToUTC(o.OpenTime)), 0, o.OpenPrice, o.ClosePrice,
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

            // if there is no order left after deletion, make prevEquity to -1 to force UpdateAccount()
            if (Client.GetOpenedOrders().Length == 0)
            {
                prevEquity = -1;
            }
        }

        private double prevEquity;
        private readonly object l1 = new object();

        private void UpdateAccount()
        {
            lock (l1)
            {
                if (prevEquity == Client.AccountEquity) return;
                prevEquity = Client.AccountEquity;
                var key = String.Format("forex:account#{0:D}", Client.User);
                var value = String.Format("{0:D}#{1:G}#{2:G}#{3:G}", Client.User, Round(Client.AccountBalance), Round(Client.AccountEquity), Round(Client.AccountMargin));
                Redis.Db.StringSet(key, value);
                var jsonKey = String.Format("forex:accountjson#{0:D}", Client.User);
                var json = new Nancy.Json.JavaScriptSerializer().Serialize(this.AsMT4Account());
                Redis.Db.StringSet(jsonKey, json);
            }
        }

        private void DynamicallyUpdateOrders()
        {
            if (!MoveStopLoss && !AddToLosingPosition) return;
            var opened = Client.GetOpenedOrders();
            for (var i = 0; i < opened.Length; i++)
            {
                var o = opened[i];
                if (MoveStopLoss) DynamicallyUpdateStopLoss(o);
                if (AddToLosingPosition) DynamicallyAddOrders(o);
            }
        }

        private void DynamicallyUpdateStopLoss(TradingAPI.MT4Server.Order o)
        {
            if (o.StopLoss == 0) return;
            var mul = 10000;
            if (o.Symbol.Contains("JPY") || o.Symbol == "XAGUSD") mul = 100;
            if (o.Symbol == "XAUUSD") mul = 10;
            var currentPips = 0.0;
            var currentStopLoss = 0.0;
            if (o.Type == TradingAPI.MT4Server.Op.Buy)
            {
                currentPips = (o.ClosePrice - o.OpenPrice) * mul;
                currentStopLoss = (o.StopLoss - o.OpenPrice) * mul; // should be negative at first, positive when winning
            }
            else if (o.Type == TradingAPI.MT4Server.Op.Sell)
            {
                currentPips = (o.OpenPrice - o.ClosePrice) * mul;
                currentStopLoss = (o.OpenPrice - o.StopLoss) * mul; // should be negative at first, positive when winning
            }
            if (currentPips < Constants.MSLMinPips)
            {
                return;
            }
            var targetStopLoss = 0.0;
            if (currentPips < Constants.MSLFirstLevelPips)
            {
                targetStopLoss = Constants.MSLFirstLevelSL; // should always be positive
            }
            else
            {
                targetStopLoss = (Math.Floor(currentPips / Constants.MSLOtherLevelSLGap) - 1) * Constants.MSLOtherLevelSLGap; // should always be positive
            }
            if (targetStopLoss - currentStopLoss < 0.1)
            {
                return;
            }
            var sl = o.StopLoss;
            var tp = o.TakeProfit;
            if (o.Type == TradingAPI.MT4Server.Op.Buy)
            {
                sl = o.OpenPrice + targetStopLoss / mul; // should be greater than open price
                tp = sl + Constants.MSLTakeProfit / mul;
            }
            else if (o.Type == TradingAPI.MT4Server.Op.Sell)
            {
                sl = o.OpenPrice - targetStopLoss / mul; // should be less than open price
                tp = sl - Constants.MSLTakeProfit / mul;
            }
            var oc = new TradingAPI.MT4Server.OrderClient(Client);
            var task = Task.Run(() => oc.OrderModify(o.Type, o.Ticket, o.OpenPrice, sl, tp, new DateTime()));
            if (task.Wait(Constants.CommandTimeout))
            {
                Logger.Info("{0} moved stop loss of {1} from {2:0.#} to {3:0.#}", Client.User, o.Ticket, currentStopLoss, targetStopLoss);
            }
            else
            {
                Logger.Info("{0} timed out moving stop loss of order #{1} from {2:0.#} to {3:0.#}", Client.User, o.Ticket, currentStopLoss, targetStopLoss);
            }
        }

        private void DynamicallyAddOrders(TradingAPI.MT4Server.Order o)
        {
            if (o.Comment.Contains("[LP]")) return;
            if (o.StopLoss == 0) return;
            var mul = 10000;
            if (o.Symbol.Contains("JPY") || o.Symbol == "XAGUSD") mul = 100;
            if (o.Symbol == "XAUUSD") mul = 10;
            var currentPips = 0.0;
            var remainingStopLoss = 0.0;
            if (o.Type == TradingAPI.MT4Server.Op.Buy)
            {
                currentPips = (o.ClosePrice - o.OpenPrice) * mul;
                remainingStopLoss = (o.ClosePrice - o.StopLoss) * mul; // should be positive
            }
            else if (o.Type == TradingAPI.MT4Server.Op.Sell)
            {
                currentPips = (o.OpenPrice - o.ClosePrice) * mul;
                remainingStopLoss = (o.StopLoss - o.ClosePrice) * mul; // should be positive
            }
            if (currentPips > (-1 * Constants.LPPipsGap) || remainingStopLoss < (Constants.LPPipsGap + 10))
            {
                // ignore if current order is not losing enough or close to the stop loss point
                return;
            }
            var lvl = (int)Math.Floor((currentPips * -1) / Constants.LPPipsGap);
            var identifier = String.Format("[LP-{0}]", lvl);
            if (HasOrder(o.Symbol, o.Type, identifier))
            {
                return;
            }
            var oc = new TradingAPI.MT4Server.OrderClient(Client);
            var task = Task.Run(() => oc.OrderSend(o.Symbol, o.Type, o.Lots, o.ClosePrice, 5, o.StopLoss, o.TakeProfit, o.Comment + "[LP]" + identifier, 0, new DateTime()));
            if (task.Wait(Constants.CommandTimeout))
            {
                var newOrder = task.Result;
                Logger.Info("{0} added order #{1} to losing position {2}", Client.User, newOrder.Ticket, newOrder.Symbol);
            }
            else
            {
                Logger.Info("{0} timed out adding order", Client.User);
                AddToLosingPosition = false;
                Logger.Info("{0} temporarily disabled AddToLosingPosition", Client.User);
                if (ResumeLPTimer != null)
                {
                    ResumeLPTimer.Stop();
                    ResumeLPTimer.Dispose();
                }
                ResumeLPTimer = new System.Timers.Timer(Constants.LPResumeTimeout);
                ResumeLPTimer.Elapsed += (__, ee) =>
                {
                    ResumeLPTimer.Stop();
                    AddToLosingPosition = true;
                    Logger.Info("{0} enabled AddToLosingPosition again", Client.User);
                };
                ResumeLPTimer.Start();
            }
        }

        private bool HasOrder(string symbol, TradingAPI.MT4Server.Op type, string comment)
        {
            var opened = Client.GetOpenedOrders();
            for (var i = 0; i < opened.Length; i++)
            {
                var o = opened[i];
                if (o.Symbol != symbol) continue;
                if (o.Type != type) continue;
                if (o.Comment.Contains(comment)) return true;
            }
            return false;
        }

        private double ToUnix(DateTime dateTime)
        {
            return (dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;
        }

        private static readonly Dictionary<int, int> dstStarts = new Dictionary<int, int>
        {
            { 2001, 25 }, { 2002, 31 }, { 2003, 30 }, { 2004, 28 }, { 2005, 27 },
            { 2006, 26 }, { 2007, 25 }, { 2008, 30 }, { 2009, 29 }, { 2010, 28 },
            { 2011, 27 }, { 2012, 25 }, { 2013, 31 }, { 2014, 30 }, { 2015, 29 },
            { 2016, 27 }, { 2017, 26 }, { 2018, 25 }, { 2019, 31 }, { 2020, 29 },
            { 2021, 28 }, { 2022, 27 }, { 2023, 26 }, { 2024, 31 }, { 2025, 30 },
            { 2026, 29 }, { 2027, 28 }, { 2028, 26 }, { 2029, 25 }, { 2030, 31 },
            { 2031, 30 }, { 2032, 28 }, { 2033, 27 }, { 2034, 26 }, { 2035, 25 },
            { 2036, 30 }, { 2037, 29 }, { 2038, 28 }, { 2039, 27 }, { 2040, 25 },
            { 2041, 31 }, { 2042, 30 }, { 2043, 29 }, { 2044, 27 }, { 2045, 26 },
            { 2046, 25 }, { 2047, 31 }, { 2048, 29 }, { 2049, 28 }, { 2050, 27 },
        };

        private static readonly Dictionary<int, int> dstEnds = new Dictionary<int, int>
        {
            { 2001, 28 }, { 2002, 27 }, { 2003, 26 }, { 2004, 31 }, { 2005, 30 },
            { 2006, 29 }, { 2007, 28 }, { 2008, 26 }, { 2009, 25 }, { 2010, 31 },
            { 2011, 30 }, { 2012, 28 }, { 2013, 27 }, { 2014, 26 }, { 2015, 25 },
            { 2016, 30 }, { 2017, 29 }, { 2018, 28 }, { 2019, 27 }, { 2020, 25 },
            { 2021, 31 }, { 2022, 30 }, { 2023, 29 }, { 2024, 27 }, { 2025, 26 },
            { 2026, 25 }, { 2027, 31 }, { 2028, 29 }, { 2029, 28 }, { 2030, 27 },
            { 2031, 26 }, { 2032, 31 }, { 2033, 30 }, { 2034, 29 }, { 2035, 28 },
            { 2036, 26 }, { 2037, 25 }, { 2038, 31 }, { 2039, 30 }, { 2040, 28 },
            { 2041, 27 }, { 2042, 26 }, { 2043, 25 }, { 2044, 30 }, { 2045, 29 },
            { 2046, 28 }, { 2047, 27 }, { 2048, 25 }, { 2049, 31 }, { 2050, 30 },
        };

        private bool IsSummer(DateTime dt)
        {
            if (dt.Month == 3) return dt.Day > dstStarts[dt.Year];
            if (dt.Month == 10) return dt.Day < dstEnds[dt.Year];
            if (dt.Month > 3 && dt.Month < 10) return true;
            return false;
        }

        private DateTime ToUTC(DateTime dt)
        {
            return dt.AddHours(IsSummer(dt) ? -3 : -2);
        }
    }
}
