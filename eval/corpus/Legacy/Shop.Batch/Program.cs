using System;
using System.Configuration;
using System.Globalization;

namespace Shop.Batch
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            DateTime businessDate;
            if (args.Length == 0)
            {
                businessDate = DateTime.Today.AddDays(-1);
            }
            else if (!DateTime.TryParseExact(args[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out businessDate))
            {
                Console.Error.WriteLine("usage: Shop.Batch.exe [yyyyMMdd]");
                return 2;
            }

            var connectionString = ConfigurationManager.ConnectionStrings["Shop"].ConnectionString;
            var batch = new SettlementBatch(connectionString);
            var result = batch.Execute(businessDate);
            Console.WriteLine(result.Report);
            return result.Success ? 0 : 1;
        }
    }
}
