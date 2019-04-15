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
                Console.WriteLine("[{0}] {1} {2} - ReqBodySize: {3} - RespStatus: {4} - Duration: {5}ms",
                    DateTime.Now.ToString("HH:mm:ss"), ctx.Request.Method,
                    ctx.Request.Path, ctx.Request.Body.Length,
                    ctx.Response.StatusCode.ToString("D"), timer.ElapsedMilliseconds);
                Console.Out.Flush();
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
}
