using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MT4Connect
{
    public class Postgres
    {
        private static bool running = false;
        private static readonly object padlock = new object();

        public static void WatchInstructions()
        {
            lock (padlock)
            {
                if (running) return;
                running = true;
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
                                Symbol = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
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
}
