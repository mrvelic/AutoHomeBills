using System.Collections.Generic;

namespace AutoBills
{
    public class BillsWorkerOptions
    {
        public const string OptionsKey = "BillsWorker";

        public string Username { get; set; }
        public string Password { get; set; }
        public string AccountNumber { get; set; }
        public List<string> MerchantNames { get; set; }

        public string GoogleKeyFile { get; set; }
        public string GoogleSheetId { get; set; }
        public string GoogleDelegatedAuthority { get; set; }
        public string BillsSheetName { get; set; }
        public List<string> PersonalSheetNames { get; set; }

        public string CronSchedule { get; set; }
        public string CronTimeZone { get; set; }

        public string NetBankingAddress { get; set; }
        public string UserAgent { get; set; }
    }
}
