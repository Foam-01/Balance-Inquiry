using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using FundBalanceDataPipeline.Models;
using Serilog;

namespace FundBalanceDataPipeline.Infrastructure
{
    public class FundConnextClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly FundConnextAuthService _authService;

        public FundConnextClient(string baseUrl, FundConnextAuthService authService)
        {
            _httpClient = new HttpClient();
            _baseUrl = baseUrl;
            _authService = authService;
        }

        public async Task<BalanceInquiryResponse> GetAccountBalancesAsync(string accountNo)
        {
            string accessToken = await _authService.GetAccessTokenAsync();
            string requestUrl = $"{_baseUrl}/account/balances?accountNo={accountNo}";

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("X-Auth-Token", accessToken);

                Log.Information($" [API Client] ยิงคำขอข้อมูลบัญชี: {accountNo}");
                
                var response = await _httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<BalanceInquiryResponse>(responseBody);
                    return result ?? new BalanceInquiryResponse();
                }
                
                Log.Warning($" [API Client] เซิร์ฟเวอร์ตอบกลับสถานะของบัญชี {accountNo}: {response.StatusCode}");
                return new BalanceInquiryResponse(); 
            }
            catch (Exception ex)
            {
                Log.Error(ex, $" [API Client Error] เกิดข้อผิดพลาดของบัญชี {accountNo}");
                return new BalanceInquiryResponse();
            }
        }
    }
}