using System;
using System.Configuration;
using System.Globalization;

namespace Shop.Jobs
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var businessDate = DateTime.Today.AddDays(-1);
            if (args.Length > 0 && !DateTime.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out businessDate))
            {
                Console.Error.WriteLine("사용법: Shop.Jobs [yyyy-MM-dd]");
                return 2;
            }

            var connectionString = Environment.GetEnvironmentVariable("SHOP_DB") ?? "Server=.;Database=Shop;Integrated Security=true";
            var job = new DailySettlementJob(connectionString, Console.Out);
            return job.Run(businessDate) ? 0 : 1;
        }
    }
}
