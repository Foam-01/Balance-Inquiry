namespace FundBalanceDataPipeline.Models
{
    public class CustomerData
    {
        public string BranchName { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string AEName { get; set; } = "";
        public string HouseNo { get; set; } = "";
        public string Soi { get; set; } = "";
        public string Road { get; set; } = "";
        public string Subdistrict { get; set; } = "";
        public string District { get; set; } = "";
        public string Province { get; set; } = "";
        public string Zipcode { get; set; } = "";
        public string FrontAccount { get; set; } = "";
    }

    public class CustomerPipelineData
    {
        public string AccountNo { get; set; } = ""; // 00XXXXX-7
        public string DbAccount { get; set; } = "";  // XXXXX-7
        public CustomerData Customer { get; set; } = new();
    }
}
