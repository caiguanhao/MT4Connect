using System;
using System.Threading;
using System.Threading.Tasks;

namespace MT4Connect
{
    public class ProcessTimedOutException : Exception
    {
        public ProcessTimedOutException() : base("timed out")
        {
        }
    }

    public class Order
    {
        public long Id { get; set; }
        public uint Login { get; set; }
        public string Action { get; set; }
        private string _symbol;
        public string Symbol
        {
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
                else
                {
                    throw new Exception("unknown action");
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
                            if (!task.Wait(Constants.CommandTimeout)) throw new ProcessTimedOutException();
                        }
                        else
                        {
                            var task = Task.Run(() => oc.OrderDelete(o.Ticket, o.Type, o.Symbol, o.Lots, o.ClosePrice));
                            if (!task.Wait(Constants.CommandTimeout)) throw new ProcessTimedOutException();
                        }
                        closed++;
                        break;
                    }
                    catch (ProcessTimedOutException)
                    {
                        throw;
                    }
                    catch (Exception)
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
                            if (!task.Wait(Constants.CommandTimeout)) throw new ProcessTimedOutException();
                            modified++;
                        }
                        break;
                    }
                    catch (ProcessTimedOutException)
                    {
                        throw;
                    }
                    catch (Exception)
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
                    if (!task.Wait(Constants.CommandTimeout)) throw new ProcessTimedOutException();
                    newOrder = task.Result;
                    break;
                }
                catch (ProcessTimedOutException)
                {
                    throw;
                }
                catch (Exception)
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
}
