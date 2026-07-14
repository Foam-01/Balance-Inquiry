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
using System.Linq;
using System.Net.Http;

Console.OutputEncoding = Encoding.UTF8;
var sharedHttpClient = new HttpClient();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Fund Balance Data Pipeline Dashboard", Version = "v1" });
});

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/pipeline-.txt", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var exception = e.ExceptionObject as Exception;
    Log.Fatal(exception, " [FATAL CRASH] Unhandled exception occurred in Application Domain. System is shutting down.");
    Log.CloseAndFlush();
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Log.Error(e.Exception, " [UNOBSERVED TASK EXCEPTION] An unobserved task exception occurred.");
    e.SetObserved();
};

// ใช้งาน Background Service สำหรับการตั้งเวลารันอัตโนมัติ (Auto Run) ในวันแรกของแต่ละเดือน
builder.Services.AddHostedService<AutoPipelineScheduler>(sp => new AutoPipelineScheduler(
    async (accNo, date) => await RunPipelineInternalAsync(accNo, date)
));

// กำหนดค่า ConnectionString จาก App.config (ล้อตามคีย์ BA_Connection ใน App.config)
string xmlConn = AppConfigService.GetConnectionString("BA_Connection");

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Pipeline API v1"));

// ===================================================================
//  ดึงข้อมูลยอดคงเหลือพอร์ต (Balance Inquiry) รายงาน FSTKH / FSTKD
// ===================================================================
// ตัวแปรสำหรับเก็บสถานะการทำงานใน Background เพื่อความปลอดภัยป้องกันการรันซ้อนและ Timeout
bool _isPipelineRunning = false;
string _pipelineStatusMessage = "ระบบยังไม่เคยถูกรันในรอบนี้";
object? _lastPipelineResult = null;

app.MapPost("/api/pipeline/run", ([FromQuery] string? accountNo, [FromQuery] string? targetDate) =>
{
    if (_isPipelineRunning)
    {
        return Results.Conflict(new { Message = "ระบบกำลังประมวลผล Data Pipeline อยู่ใน Background กรุณารอสักครู่...", Status = _pipelineStatusMessage });
    }

    // 1. ดึงข้อมูลจาก SBA บน Main Request Thread (Thread-Safe และไม่เกิด Timeout เนื่องจากดึงข้อมูลคิวรี่รวดเร็ว)
    string targetDateStr = targetDate ?? "";
    if (string.IsNullOrEmpty(targetDateStr))
    {
        DateTime target = DateTime.Today.AddDays(-1);
        while (target.DayOfWeek == DayOfWeek.Saturday || target.DayOfWeek == DayOfWeek.Sunday)
        {
            target = target.AddDays(-1);
        }
        targetDateStr = target.ToString("yyyyMMdd");
    }

    bool isAllAccounts = string.IsNullOrWhiteSpace(accountNo) || accountNo.Equals("ALL", StringComparison.OrdinalIgnoreCase);
    List<string>? specificAccounts = isAllAccounts ? null : accountNo!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    Log.Information(" [Pipeline System] กำลังดึงข้อมูลบัญชีและที่อยู่ลูกค้าประเภท 7 จาก SBA ผ่านกระบวนการแยก (Isolated Process)...");
    List<CustomerPipelineData> pipelineDataList;
    try
    {
        pipelineDataList = LoadPipelineDataIsolated(accountNo);
        Log.Information($" [Pipeline System] พบบัญชีประเภท 7 ที่โหลดขึ้นมาสำเร็จ {pipelineDataList.Count} บัญชี (ผ่านกระบวนการแยก)");
    }
    catch (Exception ex)
    {
        Log.Error(ex, " [Pipeline System] เกิดข้อผิดพลาดในการโหลดข้อมูลจากฐานข้อมูล SBA");
        return Results.Problem($"เกิดข้อผิดพลาดในการเชื่อมต่อ SBA: {ex.Message}");
    }

    _isPipelineRunning = true;
    _pipelineStatusMessage = $"เริ่มต้นดึงข้อมูลจาก API ใน Background สำหรับข้อมูลจำนวน {pipelineDataList.Count} รายการ...";
    _lastPipelineResult = null;

    string finalDateStr = targetDateStr;

    // 2. รันขั้นตอนยิง API และเขียนไฟล์ใน Background Thread (ปลอดภัยจากการแครชของ ODBC เพราะไม่มีการแตะต้อง SBA ใน Background อีกแล้ว)
    _ = Task.Run(async () =>
    {
        try
        {
            var result = await RunPipelineCoreAsync(pipelineDataList, finalDateStr, isAllAccounts);
            _lastPipelineResult = result;
            _pipelineStatusMessage = "ประมวลผล Data Pipeline สำเร็จเสร็จสิ้นอย่างสมบูรณ์แบบ!";
        }
        catch (Exception ex)
        {
            _pipelineStatusMessage = $"การรันเกิดข้อผิดพลาดขัดข้อง: {ex.Message}";
            Log.Error(ex, " [Pipeline System] เกิดข้อผิดพลาดร้ายแรงระหว่างประมวลผลใน Background Thread");
        }
        finally
        {
            _isPipelineRunning = false;
        }
    });

    return Results.Accepted("/api/pipeline/status", new 
    { 
        Message = $"โหลดข้อมูลลูกค้าจาก SBA สำเร็จ {pipelineDataList.Count} รายการ และกำลังเริ่มต้นประมวลผลดึงยอดเงินจาก API ในเบื้องหลัง...", 
        CheckStatusAt = "/api/pipeline/status" 
    });
});

app.MapGet("/api/pipeline/status", () =>
{
    return Results.Ok(new
    {
        IsRunning = _isPipelineRunning,
        Status = _pipelineStatusMessage,
        LastResult = _lastPipelineResult
    });
});

app.Run();

// ฟังก์ชันดั้งเดิมสำหรับการรันผ่าน Scheduler (ซึ่งทำงานเป็น Background Worker อยู่แล้วและไม่ชนเรื่อง Timeout)
async Task<object> RunPipelineInternalAsync(string? accountNo, string? targetDate)
{
    string targetDateStr = targetDate ?? "";
    if (string.IsNullOrEmpty(targetDateStr))
    {
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

    Log.Information(" [Pipeline System] กำลังดึงข้อมูลบัญชีและที่อยู่ลูกค้าประเภท 7 จาก SBA ผ่านกระบวนการแยก (Isolated Process)...");
    var pipelineDataList = LoadPipelineDataIsolated(accountNo);
    Log.Information($" [Pipeline System] พบบัญชีประเภท 7 ที่โหลดขึ้นมาสำเร็จ {pipelineDataList.Count} บัญชี (ผ่านกระบวนการแยก)");

    return await RunPipelineCoreAsync(pipelineDataList, targetDateStr, isAllAccounts);
}

// ฟังก์ชันแกนหลักสำหรับการยิง API และเขียนรายงาน
async Task<object> RunPipelineCoreAsync(List<CustomerPipelineData> pipelineDataList, string targetDateStr, bool isAllAccounts)
{
    string fundConnextBaseUrl = AppConfigService.GetAppSetting("linkAPI_FCN");
    if (string.IsNullOrEmpty(fundConnextBaseUrl)) fundConnextBaseUrl = "https://www.fundconnext.com/api";

    string apiUser = AppConfigService.GetAppSetting("username_FCN");
    if (string.IsNullOrEmpty(apiUser)) apiUser = "API_AIRA01";

    string apiPass = AppConfigService.GetAppSetting("password_FCN");
    if (string.IsNullOrEmpty(apiPass)) apiPass = "q%!c.*(yV3S_WE@F";

    var authService = new FundConnextAuthService(fundConnextBaseUrl, apiUser, apiPass, sharedHttpClient);
    var fundClient = new FundConnextClient(fundConnextBaseUrl, authService, sharedHttpClient);

    string headerFileName = $"FSTKH_{targetDateStr}.txt";
    string detailFileName = $"FSTKD_{targetDateStr}.txt";

    int headerCount = 0;
    int detailCount = 0;
    int successCount = 0;
    int noDataCount = 0;
    int failedCount = 0;
    var summaryTrail = new List<object>();

    bool shouldAppend = !isAllAccounts;

    using (var headerWriter = new StreamWriter(headerFileName, shouldAppend, Encoding.UTF8) { AutoFlush = true })
    using (var detailWriter = new StreamWriter(detailFileName, shouldAppend, Encoding.UTF8) { AutoFlush = true })
    {
        int currentIndex = 0;
        int totalCount = pipelineDataList.Count;

        foreach (var item in pipelineDataList)
        {
            currentIndex++;
            string rawAccount = item.AccountNo;
            try
            {
                // หน่วงเวลา 250ms เพื่อให้ระบบเครือข่ายได้พัก ป้องกันโดน Firewall บล็อกและพอร์ต Socket เต็ม
                await Task.Delay(250);

                string formattedAccount = FormatAiraAccount(item.AccountNo);
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
                    Log.Information($" [Pipeline System] [{currentIndex}/{totalCount}] กำลังทดลองดึงข้อมูลจาก API ด้วยบัญชี: {candidate}");
                    var tempResponse = await fundClient.GetAccountBalancesAsync(candidate);

                    if (tempResponse != null)
                    {
                        balanceResponse = tempResponse;
                        successfulAccountNo = candidate;
                        Log.Information($" [Pipeline System] [{currentIndex}/{totalCount}] ค้นพบรูปแบบบัญชีที่ถูกต้อง: {candidate} (สามารถดึงข้อมูลได้สำเร็จ)");
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
                    successCount++;
                    summaryTrail.Add(new { Account = formattedAccount, Status = $"Data Fetched from API (using {successfulAccountNo})", FundsExtracted = balanceResponse.Result.Count });
                }
                else
                {
                    noDataCount++;
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
    Log.Information("  [Pipeline System] สรุปผลการประมวลผล Data Pipeline สำเร็จเสร็จสิ้น!");
    Log.Information($"  - จำนวนบัญชีทั้งหมดในระบบ: {pipelineDataList.Count} บัญชี");
    Log.Information($"  - ดึงข้อมูลพอร์ตยอดคงเหลือสำเร็จ: {successCount} บัญชี");
    Log.Information($"  - ไม่มีข้อมูลพอร์ตคงเหลืออยู่บนระบบ API: {noDataCount} บัญชี");
    Log.Information($"  - เกิดข้อผิดพลาดในการประมวลผล: {failedCount} บัญชี");
    Log.Information($"  - จำนวนแถวรายงานที่สร้าง (FSTKH - ข้อมูลลูกค้า): {headerCount} แถว");
    Log.Information($"  - จำนวนแถวรายงานที่สร้าง (FSTKD - ข้อมูลยอดกองทุน): {detailCount} แถว");
    Log.Information(" ===================================================================");

    return new
    {
        Message = "ดำเนินการทำ Data Pipeline จากเส้นทาง Balance Inquiry สำเร็จเสร็จสิ้น!",
        TargetDate = targetDateStr,
        ProcessedStats = new
        {
            TotalAccounts = pipelineDataList.Count,
            SuccessAccounts = successCount,
            NoDataAccounts = noDataCount,
            FailedAccounts = failedCount,
            HeaderRows = headerCount,
            DetailRows = detailCount
        },
        FilesGenerated = new { Header = headerFileName, Detail = detailFileName },
        AuditTrail = summaryTrail
    };
}

string FormatAiraAccount(string rawAccount)
{
    if (string.IsNullOrEmpty(rawAccount)) return "";
    rawAccount = rawAccount.Trim();
    int dashIdx = rawAccount.IndexOf('-');
    if (dashIdx >= 0)
    {
        string prefix = rawAccount.Substring(0, dashIdx);
        string suffix = rawAccount.Substring(dashIdx + 1);
        // เติม 0 ให้ส่วนหน้าครบ 7 หลัก (เช่น 61117 -> 0061117)
        return $"{prefix.PadLeft(7, '0')}-{suffix}";
    }
    else
    {
        // เติม 0 ให้ครบ 8 หลัก
        return rawAccount.PadLeft(8, '0');
    }
}

List<CustomerPipelineData> LoadPipelineDataIsolated(string? accountNo)
{
    // 1. ค้นหาไฟล์ SbaExporter.exe โดยจัดลำดับความสำคัญให้โฟลเดอร์ของมันเองในระหว่างพัฒนา (เพื่อเลี่ยงไฟล์ DLL หลอกในโฟลเดอร์หลัก)
    string devExporterPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SbaExporter", "bin", "Debug", "net10.0", "win-x86", "SbaExporter.exe");
    string exporterExePath = File.Exists(devExporterPath) ? devExporterPath : Path.Combine(AppContext.BaseDirectory, "SbaExporter.exe");

    string arguments = accountNo != null ? $"\"{accountNo}\"" : "ALL";
    
    // กำหนดพาธไฟล์ผลลัพธ์ JSON ในโฟลเดอร์เดียวกับโปรเซสย่อย
    string exporterDir = Path.GetDirectoryName(exporterExePath) ?? AppContext.BaseDirectory;
    string tempFile = Path.Combine(exporterDir, "sba_temp_data.json");

    if (File.Exists(tempFile))
    {
        try { File.Delete(tempFile); } catch {}
    }

    using (var process = new System.Diagnostics.Process())
    {
        process.StartInfo.FileName = exporterExePath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.WorkingDirectory = exporterDir;

        // ส่งผ่าน Connection String จาก App.config ไปยังโปรเซสย่อย
        string xmlConn = AppConfigService.GetConnectionString("BA_Connection");
        if (!string.IsNullOrEmpty(xmlConn))
        {
            process.StartInfo.EnvironmentVariables["SBA_CONNECTION_STRING"] = xmlConn;
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.Start();
        
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        
        process.WaitForExit();

        if (!string.IsNullOrEmpty(stdout))
        {
            Log.Information($"[Isolated Output]\n{stdout.Trim()}");
        }
        if (!string.IsNullOrEmpty(stderr))
        {
            Log.Warning($"[Isolated Error]\n{stderr.Trim()}");
        }

        if (process.ExitCode != 0)
        {
            throw new Exception($"กระบวนการแยกดึงข้อมูล SBA (SbaExporter.exe) สิ้นสุดลงด้วยข้อผิดพลาด (Exit Code: {process.ExitCode})");
        }
    }

    if (!File.Exists(tempFile))
    {
        throw new FileNotFoundException("ไม่พบไฟล์ผลลัพธ์ข้อมูล SBA คาดว่าโปรเซสย่อย SbaExporter.exe แครชระหว่างเข้าใช้งานไดรเวอร์ ODBC");
    }

    string json = File.ReadAllText(tempFile, Encoding.UTF8);
    var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<CustomerPipelineData>>(json) ?? new();

    try { File.Delete(tempFile); } catch {}
    return list;
}
