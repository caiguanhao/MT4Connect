using Npgsql;
using NpgsqlTypes;

namespace MT4Connect
{
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
