using Nancy;
using Nancy.Bootstrapper;
using Nancy.Hosting.Self;
using Nancy.ModelBinding;
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
            });
        }
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
            public int Login { get; set; }
            public string Password { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
        }

        public class MT4Account
        {
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
                    var qc = new TradingAPI.MT4Server.QuoteClient(account.Login, account.Password, account.Host, account.Port);
                    qc.Connect();
                    var mt4acc = new MT4Account()
                    {
                        Login = qc.User,
                        TradeMode = qc.AccountMode,
                        Leverage = qc.AccountLeverage,
                        LimitOrders = qc.Account.maxpositions,
                        Balance = Round(qc.AccountBalance),
                        Credit = Round(qc.AccountCredit),
                        Profit = Round(qc.AccountProfit),
                        Equity = Round(qc.AccountEquity),
                        Margin = Round(qc.AccountMargin),
                        MarginFree = Round(qc.AccountFreeMargin),
                        MarginLevel = String.Format("{0:0.00}%", Round(qc.AccountEquity / qc.AccountMargin * 100)),
                        Currency = qc.Account.currency,
                        Server = qc.Account.company,
                        AccountName = qc.AccountName,
                    };
                    var resp = Response.AsJson(new Dictionary<string, MT4Account> { { "account", mt4acc } });
                    qc.Disconnect();
                    return resp;
                }
                catch (Exception)
                {
                    return Response.AsJson(new Dictionary<string, int?> { { "account", null } });
                }
            };
        }

        private double Round(double number)
        {
            return Math.Round(number, 2, MidpointRounding.AwayFromZero);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
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
            Console.WriteLine("Goodbye!");
        }
    }
}
