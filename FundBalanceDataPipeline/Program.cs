#pragma warning disable CS8618
#pragma warning disable CS8603

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FundBalanceDataPipeline.Infrastructure;
using FundBalanceDataPipeline.Models;
using FundBalanceDataPipeline.Services;
using Serilog;
using System.Data.Odbc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Fund Balance Data Pipeline Dashboard", Version = "v1" });
});

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

// ใช้งาน Background Service สำหรับการตั้งเวลารันอัตโนมัติ (Auto Run) ในวันแรกของแต่ละเดือน
builder.Services.AddHostedService<AutoPipelineScheduler>(sp => new AutoPipelineScheduler(
    async (accNo, date) => await RunPipelineInternalAsync(accNo, date)
));

// กำหนดค่า ConnectionString จาก App.config (ล้อตามคีย์ BA_Connection ใน App.config)
string xmlConn = AppConfigService.GetConnectionString("BA_Connection");
SbaDatabaseService.ConnectionString = !string.IsNullOrEmpty(xmlConn) ? xmlConn : SbaDatabaseService.ConnectionString;

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Pipeline API v1"));

// ===================================================================
//  ดึงข้อมูลยอดคงเหลือพอร์ต (Balance Inquiry) รายงาน FSTKH / FSTKD
// ===================================================================
app.MapPost("/api/pipeline/run", async ([FromQuery] string? accountNo, [FromQuery] string? targetDate) =>
{
    try
    {
        var result = await RunPipelineInternalAsync(accountNo, targetDate);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem($" ระบบประมวลผลขัดข้อง: {ex.Message}");
    }
});

app.Run();

// ฟังก์ชันภายในสำหรับการรัน Data Pipeline (รองรับทั้งการสั่งงานผ่าน API และการสั่งงานอัตโนมัติจาก Scheduler)
async Task<object> RunPipelineInternalAsync(string? accountNo, string? targetDate)
{
    string targetDateStr = targetDate ?? "";
    if (string.IsNullOrEmpty(targetDateStr))
    {
        // คำนวณวันทำการย้อนหลัง 1 วันทำการโดยอัตโนมัติ (ไม่ตรงกับเสาร์-อาทิตย์)
        DateTime target = DateTime.Today.AddDays(-1);
        while (target.DayOfWeek == DayOfWeek.Saturday || target.DayOfWeek == DayOfWeek.Sunday)
        {
            target = target.AddDays(-1);
        }
        targetDateStr = target.ToString("yyyyMMdd");
        Log.Information($" [Pipeline System] ตรวจพบการรันแบบ Auto คำนวณวันทำการย้อนหลัง 1 วันทำการ: {targetDateStr}");
    }

    bool isAllAccounts = string.IsNullOrWhiteSpace(accountNo) || accountNo.Equals("ALL", StringComparison.OrdinalIgnoreCase);
    List<string>? specificAccounts = isAllAccounts ? null : accountNo!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    Log.Information(" [Pipeline System] กำลังดึงข้อมูลบัญชีและที่อยู่ลูกค้าประเภท 7 จาก SBA ขึ้น Memory...");
    var pipelineDataList = SbaDatabaseService.LoadAllPipelineDataFromSba(specificAccounts);

    // หากระบุบัญชีทดสอบที่ไม่มีในฐานข้อมูล SBA (เช่น บัญชีทดสอบ 0099901-7 บน FundConnext Stage)
    // ระบบจะทำการจำลองเฉพาะ "ชื่อ-ที่อยู่ลูกค้า" เพื่อใช้สร้างรายงาน FSTKH และยอมให้ไปยิงดึงยอดกองทุนจริงจาก API
    if (pipelineDataList.Count == 0 && specificAccounts != null && specificAccounts.Count > 0)
    {
        Log.Warning(" [Pipeline System] ไม่พบเลขบัญชีดังกล่าวในฐานข้อมูล SBA ระบบทำการจำลองข้อมูลลูกค้าชั่วคราว (Fallback Customer Data) เพื่อส่งไปเรียกใช้งาน API ทดสอบ...");
        foreach (var acc in specificAccounts)
        {
            pipelineDataList.Add(new CustomerPipelineData
            {
                AccountNo = acc,
                DbAccount = acc,
                Customer = new CustomerData
                {
                    CustomerName = "บัญชีทดสอบพิเศษ (STAGE TEST)",
                    BranchName = "สำนักงานใหญ่",
                    AEName = "ผู้ดูแลระบบเทส",
                    HouseNo = "99/9",
                    Soi = "ซอยทดสอบ",
                    Road = "ถนนทดสอบ",
                    Subdistrict = "พญาไท",
                    District = "พญาไท",
                    Province = "กรุงเทพมหานคร",
                    Zipcode = "10400"
                }
            });
        }
    }

    Log.Information($" [Pipeline System] พบบัญชีประเภท 7 ที่โหลดขึ้นมาสำเร็จ {pipelineDataList.Count} บัญชี (ปิดการเชื่อมต่อฐานข้อมูล SBA เรียบร้อย)");

    string fundConnextBaseUrl = AppConfigService.GetAppSetting("linkAPI_FCN");
    if (string.IsNullOrEmpty(fundConnextBaseUrl)) fundConnextBaseUrl = "https://stage.fundconnext.com/api";

    string apiUser = AppConfigService.GetAppSetting("username_FCN");
    if (string.IsNullOrEmpty(apiUser)) apiUser = "API_AIRA01";

    string apiPass = AppConfigService.GetAppSetting("password_FCN");
    if (string.IsNullOrEmpty(apiPass)) apiPass = "Xc8uAgvP:Y]/t3*y";

    var authService = new FundConnextAuthService(fundConnextBaseUrl, apiUser, apiPass);
    var fundClient = new FundConnextClient(fundConnextBaseUrl, authService);

    string headerFileName = $"FSTKH_{targetDateStr}.txt";
    string detailFileName = $"FSTKD_{targetDateStr}.txt";

    int headerCount = 0;
    int detailCount = 0;
    int failedCount = 0;
    var summaryTrail = new List<object>();

    bool shouldAppend = !isAllAccounts;

    using (var headerWriter = new StreamWriter(headerFileName, shouldAppend, Encoding.UTF8) { AutoFlush = true })
    using (var detailWriter = new StreamWriter(detailFileName, shouldAppend, Encoding.UTF8) { AutoFlush = true })
    {
        foreach (var item in pipelineDataList)
        {
            string rawAccount = item.AccountNo;
            try
            {
                // หน่วงเวลา 250ms เพื่อให้ระบบเครือข่ายได้พัก ป้องกันโดน Firewall บล็อกและพอร์ต Socket เต็ม
                await Task.Delay(250);

                string formattedAccount = item.AccountNo;
                string dbAccount = item.DbAccount;
                var customerData = item.Customer;

                string headerLine = $"{targetDateStr}|{customerData.BranchName}|{formattedAccount}|{customerData.CustomerName}|{customerData.AEName}|{customerData.HouseNo}|{customerData.Soi}|{customerData.Road}|{customerData.Subdistrict}|{customerData.District}|{customerData.Province}|{customerData.Zipcode}|0107550000211";
                await headerWriter.WriteLineAsync(headerLine);
                headerCount++;

                // คัดกรองตัวเลือกในการยิง API เฉพาะรูปแบบบัญชีลูกค้าหลัก 2 รูปแบบเท่านั้น
                var candidates = new List<string>();
                candidates.Add(formattedAccount);                  // แบบมีขีด เช่น 61140-7
                candidates.Add(formattedAccount.Replace("-", "")); // แบบไม่มีขีด เช่น 611407

                BalanceInquiryResponse? balanceResponse = null;
                string successfulAccountNo = "";

                foreach (var candidate in candidates)
                {
                    Log.Information($" [Pipeline System] กำลังทดลองดึงข้อมูลจาก API ด้วยบัญชี: {candidate}");
                    var tempResponse = await fundClient.GetAccountBalancesAsync(candidate);

                    if (tempResponse != null)
                    {
                        balanceResponse = tempResponse;
                        successfulAccountNo = candidate;
                        Log.Information($" [Pipeline System] ค้นพบรูปแบบบัญชีที่ถูกต้อง: {candidate} (สามารถดึงข้อมูลได้สำเร็จ)");
                        break;
                    }
                }

                if (balanceResponse != null && balanceResponse.Result != null && balanceResponse.Result.Count > 0)
                {
                    foreach (var fund in balanceResponse.Result)
                    {
                        decimal amount = fund.Amount;
                        decimal costAmount = fund.CostAmount;
                        decimal unrealized = amount - costAmount;

                        string unrealizedStr = unrealized.ToString("F2");

                        string amcCode = "KSAM";
                        if (fund.FundCode.StartsWith("PRINCIPAL", StringComparison.OrdinalIgnoreCase))
                        {
                            amcCode = "PRINCIPAL";
                        }
                        else if (fund.FundCode.StartsWith("SCB", StringComparison.OrdinalIgnoreCase))
                        {
                            amcCode = "SCBAM";
                        }

                        string detailLine = $"{formattedAccount}|{amcCode}|{fund.FundCode}|{fund.Unit:F4}|{fund.NavDate}|{fund.AvgCost:F4}|{fund.Nav:F4}|{costAmount:F2}|{amount:F2}|{unrealizedStr}";
                        await detailWriter.WriteLineAsync(detailLine);
                        detailCount++;
                    }
                    summaryTrail.Add(new { Account = formattedAccount, Status = $"Data Fetched from API (using {successfulAccountNo})", FundsExtracted = balanceResponse.Result.Count });
                }
                else
                {
                    summaryTrail.Add(new { Account = formattedAccount, Status = $"No Records on API Path (Tried {candidates.Count} formats)", FundsExtracted = 0 });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $" [Pipeline System] เกิดข้อผิดพลาดในการประมวลผลบัญชี {rawAccount}");
                summaryTrail.Add(new { Account = rawAccount, Status = $"Failed: {ex.Message}", FundsExtracted = 0 });
                failedCount++;
            }
        }
    }

    Log.Information(" ===================================================================");
    Log.Information("  [Pipeline System] สรุปผลการประมวลผล Data Pipeline:");
    Log.Information($"  - จำนวนบัญชีที่โหลดมาประมวลผลทั้งหมด: {pipelineDataList.Count} บัญชี");
    Log.Information($"  - จำนวนแถวที่เขียนลงรายงาน FSTKH (สำเร็จ): {headerCount} แถว");
    Log.Information($"  - จำนวนแถวที่เขียนลงรายงาน FSTKD (กองทุน): {detailCount} แถว");
    Log.Information($"  - จำนวนบัญชีที่เกิดข้อผิดพลาด: {failedCount} บัญชี");
    Log.Information(" ===================================================================");

    return new
    {
        Message = " ดำเนินการทำ Data Pipeline จากเส้นทาง Balance Inquiry สำเร็จเสร็จสิ้น!",
        TargetDate = targetDateStr,
        FilesGenerated = new { Header = headerFileName, Detail = detailFileName },
        ProcessedRows = new { HeaderRows = headerCount, DetailRows = detailCount },
        AuditTrail = summaryTrail
    };
}