using System;
using System.Collections.Generic;

namespace AutoBills
{
    public class Models
    {
        public class LoginResponse
        {
            public bool Valid { get; set; }
            public string ErrorCode { get; set; }
        }

        public class AccountDataResponse
        {
            public string AccountNumber { get; set; }
            public string Description { get; set; }
        }

        public class TransactionDetailsResponse
        {
            public List<TransactionLineItem> TransactionDetails { get; set; }
            public bool MoreTransactionsAreAvailable { get; set; }
        }

        public class TransactionLineItem
        {
            public string AccountNumber { get; set; }
            public DateTime EffectiveDate { get; set; }
            public DateTime CreateDate { get; set; }
            public decimal DebitAmount { get; set; }
            public decimal CreditAmount { get; set; }
            public string Description { get; set; }
        }
    }
}
