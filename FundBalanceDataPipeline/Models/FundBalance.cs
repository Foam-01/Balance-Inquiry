using System.Collections.Generic;
using Newtonsoft.Json;

namespace FundBalanceDataPipeline.Models
{
    public class BalanceInquiryResponse
    {
        [JsonProperty("result")]
        public List<BalanceInquiryResult> Result { get; set; } = new List<BalanceInquiryResult>();
    }

    public class BalanceInquiryResult
    {
        [JsonProperty("unitholderId")]
        public string UnitholderId { get; set; } = "";

        [JsonProperty("fundCode")]
        public string FundCode { get; set; } = "";

        [JsonProperty("unit")]
        public decimal Unit { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("remainUnit")]
        public decimal RemainUnit { get; set; }

        [JsonProperty("remainAmount")]
        public decimal RemainAmount { get; set; }

        [JsonProperty("pendingAmount")]
        public decimal PendingAmount { get; set; }

        [JsonProperty("pendingUnit")]
        public decimal PendingUnit { get; set; }

        [JsonProperty("avgCost")]
        public decimal AvgCost { get; set; }

        [JsonProperty("nav")]
        public decimal Nav { get; set; }

        [JsonProperty("navDate")]
        public string NavDate { get; set; } = "";

        [JsonProperty("costAmount")]
        public decimal CostAmount { get; set; }
    }
}