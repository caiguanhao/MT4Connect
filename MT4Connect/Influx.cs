using InfluxDB.LineProtocol.Client;
using InfluxDB.LineProtocol.Payload;
using System;
using System.Collections.Generic;

namespace MT4Connect
{
    class Influx
    {
        private static bool running = false;
        private static readonly object padlock = new object();

        public static void SendMetrics()
        {
            lock (padlock)
            {
                if (running) return;
                running = true;
                var influxClient = new LineProtocolClient(new Uri("http://10.211.55.2:8086"), "forex");
                var influxTimer = new System.Timers.Timer(1000);
                influxTimer.Elapsed += (_, e) =>
                {
                    var payload = new LineProtocolPayload();
                    foreach (KeyValuePair<uint, FXClient> account in Current.Accounts)
                    {
                        var now = DateTime.UtcNow;
                        var tags = new Dictionary<string, string> { { "login", account.Key.ToString() } };

                        var equity = new LineProtocolPoint(
                            measurement: "equity",
                            fields: new Dictionary<string, object> { { "value", account.Value.Client.AccountEquity } },
                            tags: tags,
                            utcTimestamp: now
                        );
                        payload.Add(equity);

                        var balance = new LineProtocolPoint(
                            measurement: "balance",
                            fields: new Dictionary<string, object> { { "value", account.Value.Client.AccountBalance } },
                            tags: tags,
                            utcTimestamp: now
                        );
                        payload.Add(balance);

                        var margin = new LineProtocolPoint(
                            measurement: "margin",
                            fields: new Dictionary<string, object> { { "value", account.Value.Client.AccountMargin } },
                            tags: tags,
                            utcTimestamp: now
                        );
                        payload.Add(margin);

                        var orders = new LineProtocolPoint(
                            measurement: "orders",
                            fields: new Dictionary<string, object> { { "value", account.Value.Client.GetOpenedOrders().Length } },
                            tags: tags,
                            utcTimestamp: now
                        );
                        payload.Add(orders);
                    }
                    influxClient.WriteAsync(payload);
                };
                influxTimer.Start();
            }
        }
    }
}
