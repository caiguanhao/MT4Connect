using Nancy.Hosting.Self;
using System;
using System.Threading;

namespace MT4Connect
{
    public class Constants
    {
        public static TimeSpan KeyTimeout = TimeSpan.FromSeconds(3);
        public static TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
        public static string ConfigsFile = "configs.json";

        public static double MSLMinPips = 10;
        public static double MSLFirstLevelPips = MSLMinPips * 2;
        public static double MSLFirstLevelSL = MSLMinPips / 2;
        public static double MSLOtherLevelSLGap = MSLMinPips;

        public static double LPPipsGap = 20;
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

    class Program
    {
        static void Main(string[] args)
        {
            DisableQuickEdit();

            // start connections
            if (!Redis.Db.Multiplexer.IsConnected)
            {
                throw new Exception("Failed to connect redis");
            }
            Logger.Info("connected to redis");
            OrdersPostgres.Conn.Open();
            Logger.Info("connected to orders postgres");
            InstructionsPostgres.Conn.Open();
            Logger.Info("connected to instructions postgres");

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
            var listen = Current.Configs.Listen;
            if (!listen.StartsWith("http://")) listen = "http://" + listen;
            Uri uri = new Uri(listen);
            var host = new NancyHost(hostConfigs, uri);
            host.Start();
            Logger.Info("Listening to {0}", listen);
            exitEvent.WaitOne();

            // close connections
            host.Stop();
            InstructionsPostgres.Conn.Close();
            OrdersPostgres.Conn.Close();
            Redis.Db.Multiplexer.Close();
            Logger.Info("Goodbye!");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, int ioMode);

        private const int QuickEditMode = 64;
        private const int ExtendedFlags = 128;
        private const int STD_INPUT_HANDLE = -10;

        private static void DisableQuickEdit()
        {
            var conHandle = GetStdHandle(STD_INPUT_HANDLE);
            if (!GetConsoleMode(conHandle, out int mode))
            {
                Logger.Info("Failed to get console mode");
                return;
            }
            mode = mode & ~(QuickEditMode | ExtendedFlags);
            if (SetConsoleMode(conHandle, mode))
            {
                Logger.Info("Successfully disabled quick edit mode");
            }
            else
            {
                Logger.Info("Failed to disable quick edit mode");
            }
        }
    }
}
