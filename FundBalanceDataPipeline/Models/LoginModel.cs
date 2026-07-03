using Newtonsoft.Json;

namespace FundBalanceDataPipeline.Models
{
    public class LoginModel
    {
        [JsonProperty("username")]
        public string Username { get; set; } = "";

        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonProperty("saCode")]
        public string SaCode { get; set; } = "";

        [JsonProperty("isPassthrough")]
        public bool IsPassthrough { get; set; }

        [JsonProperty("errMsg")]
        public string ErrMsg { get; set; } = "";
    }
}