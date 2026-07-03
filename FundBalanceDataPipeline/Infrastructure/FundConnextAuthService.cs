using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using FundBalanceDataPipeline.Models;
using Serilog;

namespace FundBalanceDataPipeline.Infrastructure
{
    public class FundConnextAuthService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _username;
        private readonly string _password;
        
        private string _cachedToken = "";
        private DateTime _tokenExpiry;

        public FundConnextAuthService(string baseUrl, string username, string password)
        {
            _httpClient = new HttpClient();
            _baseUrl = baseUrl;
            _username = username;
            _password = password;
        }

        public async Task<string> GetAccessTokenAsync()
        {
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
            {
                return _cachedToken;
            }

            Log.Information(" [Auth] กำลังขอ Access Token ใหม่จาก FundConnext...");
            string uri = $"{_baseUrl}/auth";

            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var bodyContent = new { username = _username, password = _password };
            var httpContent = new StringContent(JsonConvert.SerializeObject(bodyContent), Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(uri, httpContent);
            string resultStr = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var loginResult = JsonConvert.DeserializeObject<LoginModel>(resultStr);
                if (loginResult != null && !string.IsNullOrEmpty(loginResult.AccessToken))
                {
                    //  ลบเครื่องหมายคำพูด ฟันหนู ( " ) ออกจาก Token ป้องกัน 401 Unauthorized
                    string sanitizedToken = loginResult.AccessToken.Replace("\"", "").Trim();
                    
                    _cachedToken = sanitizedToken;
                    _tokenExpiry = DateTime.UtcNow.AddMinutes(25);

                    Log.Information(" [Auth] รับ Access Token สำเร็จ (ล้างเครื่องหมายคำพูดเรียบร้อย)");
                    return _cachedToken;
                }
            }

            Log.Error($" [Auth Failed] สถานะ: {response.StatusCode} - {resultStr}");
            throw new UnauthorizedAccessException("ไม่สามารถล็อกอินเข้าสู่ระบบ FundConnext ได้");
        }
    }
}