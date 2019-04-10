﻿using Nancy.Hosting.Self;
using System;
using System.Threading;

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

    class Program
    {
        static void Main(string[] args)
        {
            // start connections
            if (!Redis.Db.Multiplexer.IsConnected)
            {
                throw new Exception("Failed to connect redis");
            }
            OrdersPostgres.Conn.Open();
            InstructionsPostgres.Conn.Open();

            // start services
            Influx.SendMetrics();
            Redis.KeepKeysAlive();
            Postgres.WatchInstructions();

            // start server
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

            // close connections
            InstructionsPostgres.Conn.Close();
            OrdersPostgres.Conn.Close();
            Redis.Db.Multiplexer.Close();
            Logger.Info("Goodbye!");
        }
    }
}
