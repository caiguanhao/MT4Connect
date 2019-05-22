using Nancy;
using Nancy.Bootstrapper;
using Nancy.ModelBinding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace MT4Connect
{
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
                Logger.Info("{0} {1} - ReqBodySize: {2} - RespStatus: {3} - Duration: {4}ms - Client: {5}",
                    ctx.Request.Method, new Uri(ctx.Request.Url).PathAndQuery, ctx.Request.Body.Length,
                    ctx.Response.StatusCode.ToString("D"), timer.ElapsedMilliseconds, ctx.Request.UserHostAddress);
            });
        }
    }

    public class PageNotFoundHandler : Nancy.ErrorHandling.IStatusCodeHandler
    {
        public PageNotFoundHandler() : base()
        {
        }

        public bool HandlesStatusCode(HttpStatusCode statusCode, NancyContext context)
        {
            return statusCode == HttpStatusCode.NotFound;
        }

        public void Handle(HttpStatusCode statusCode, NancyContext context)
        {
            var response = new Nancy.Responses.JsonResponse(new Dictionary<string, string>{ { "message", "Page Not Found" } }, new Nancy.Responses.DefaultJsonSerializer());
            response.StatusCode = HttpStatusCode.NotFound;
            context.Response = response;
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
            public uint Login { get; set; }
            public string Password { get; set; }
            public string ServerName { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public bool MoveStopLoss { get; set; }
            public bool AddToLosingPosition { get; set; }
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

            Post["/batch-login"] = _ => {
                var accounts = this.Bind<List<Account>>();
                Task.Factory.StartNew(() =>
                {
                    var options = new ParallelOptions() { MaxDegreeOfParallelism = Constants.BatchLoginConcurrency };
                    Parallel.ForEach(accounts, options, account =>
                    {
                        if (!Current.Accounts.ContainsKey(account.Login))
                        {
                            Login(account);
                        }
                    });
                });
                return Response.AsJson(new Dictionary<string, string> { { "message", "ok" } });
            };

            Post["/login"] = _ =>
            {
                try
                {
                    var account = this.Bind<Account>();
                    if (Current.Accounts.ContainsKey(account.Login))
                    {
                        var cli = new TradingAPI.MT4Server.QuoteClient(account.Login, account.Password, account.Host, account.Port);
                        var task = Task.Run(() => cli.Connect());
                        if (task.Wait(Constants.LoginTimeout))
                        {
                            cli.Disconnect();
                            return Response.AsJson(new Dictionary<string, Dictionary<string, bool>> {
                                { "account", new Dictionary<string, bool> { { "master", cli.AccountMode == 0 } } } });
                        }
                        else
                        {
                            throw new Exception("timed out");
                        }
                    }
                    var client = Login(account);
                    return Response.AsJson(new Dictionary<string, MT4Account> { { "account", client.AsMT4Account() } });
                }
                catch (Exception ex)
                {
                    return Response.AsJson(new Dictionary<string, string> { { "message", ex.Message } }, HttpStatusCode.Unauthorized);
                }
            };

            FXClient Login(Account account)
            {
                Logger.Info("Connecting to {0}...", account.Login);
                var client = new FXClient(account.Login, account.Password, account.ServerName, account.Host, account.Port);
                client.AddToLosingPosition = account.AddToLosingPosition;
                client.MoveStopLoss = account.MoveStopLoss;
                client.Connect();
                if (client.IsMaster())
                {
                    Current.Accounts.Add(account.Login, client);
                }
                else
                {
                    client.Disconnect();
                }
                return client;
            }

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

            Post["/accounts"] = _ =>
            {
                try
                {
                    var account = this.Bind<NewAccount>();
                    var demoAccount = TradingAPI.MT4Server.QuoteClient.GetDemo(
                        host: account.Host,
                        port: account.Port,
                        leverage: account.Leverage,
                        balance: account.Balance,
                        name: account.Name,
                        accountType: account.AccountType,
                        country: account.Country,
                        city: account.City,
                        state: account.State,
                        zip: account.Zip,
                        address: account.Address,
                        phone: account.Phone,
                        email: account.Email,
                        terminalCompany: account.TerminalCompany
                    );
                    var ret = new Dictionary<string, Dictionary<string, object>> {
                        {
                            "account",
                            new Dictionary<string, object>
                            {
                                {  "login", demoAccount.User },
                                {  "password", demoAccount.Password },
                                {  "investor", demoAccount.Investor },
                            }
                        }
                    };
                    return Response.AsJson(ret);
                }
                catch (Exception ex)
                {
                    return Response.AsJson(new Dictionary<string, string> { { "message", ex.Message } }, HttpStatusCode.Unauthorized);
                }
            };
        }

        public class NewAccount
        {
            public string Host { get; set; }
            public int Port { get; set; }
            public int Leverage { get; set; }
            public double Balance { get; set; }
            public string Name { get; set; }
            public string AccountType { get; set; }
            public string Country { get; set; }
            public string City { get; set; }
            public string State { get; set; }
            public string Zip { get; set; }
            public string Address { get; set; }
            public string Phone { get; set; }
            public string Email { get; set; }
            public string TerminalCompany { get; set; }
        }
    }
}
