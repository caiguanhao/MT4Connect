using System;
using System.Collections.Generic;

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
}
