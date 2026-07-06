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

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Pipeline API v1"));

// ===================================================================
//  ดึงข้อมูลยอดคงเหลือพอร์ต (Balance Inquiry) พ่นรายงาน FSTKH / FSTKD
// ===================================================================
app.MapPost("/api/pipeline/run", async ([FromQuery] string accountNo, [FromQuery] string? targetDate) =>
{
    if (string.IsNullOrWhiteSpace(accountNo))
    {
        return Results.BadRequest(new { Message = "กรุณาระบุเลขที่บัญชีลูกค้า (accountNo)" });
    }

    Log.Information($" [Pipeline System] เริ่มทำการดึงยอดคงเหลือบัญชี {accountNo} จาก FundConnext API... ");

    string fundConnextBaseUrl = "https://stage.fundconnext.com/api";
    string apiUser = "API_AIRA01";
    string apiPass = "Xc8uAgvP:Y]/t3*y";

    try
    {
        var authService = new FundConnextAuthService(fundConnextBaseUrl, apiUser, apiPass);
        var fundClient = new FundConnextClient(fundConnextBaseUrl, authService);

        // วันทำการสิ้นเดือนที่ใช้ตั้งชื่อไฟล์และระบุในส่วนหัวรายงาน
        string targetDateStr = targetDate ?? "20260529";

        string headerFileName = $"FSTKH_{targetDateStr}.txt";
        string detailFileName = $"FSTKD_{targetDateStr}.txt";

        // ใช้เลขบัญชีลูกค้าที่ส่งมาทาง Query Parameter
        List<string> rawAccounts = new List<string> { accountNo };

        int headerCount = 0;
        int detailCount = 0;
        var summaryTrail = new List<object>();

        using (var headerWriter = new StreamWriter(headerFileName, false, Encoding.UTF8))
        using (var detailWriter = new StreamWriter(detailFileName, false, Encoding.UTF8))
        {
            foreach (var rawAccount in rawAccounts)
            {
                // ตรวจสอบความถูกต้องของเลขบัญชี (SBA อาจเก็บ *****-7 หรือ 00*****-7)
                string formattedAccount = rawAccount.StartsWith("00") ? rawAccount : "00" + rawAccount;
                string dbAccount = formattedAccount.StartsWith("00") ? formattedAccount.Substring(2) : formattedAccount;

                // ดึงข้อมูลลูกค้าจากฐานข้อมูล SBA (ชื่อ, สาขา, ที่อยู่, AE)
                var customerData = await SbaDatabaseService.GetCustomerDataFromSbaAsync(dbAccount);

                //  1. สังเคราะห์ข้อมูลส่วนหัวลูกค้า (Header - FSTKH) คั่นด้วย Pipe "|" จำนวน 13 คอลัมน์
                string headerLine = $"{targetDateStr}|{customerData.BranchName}|{formattedAccount}|{customerData.CustomerName}|{customerData.AEName}|{customerData.HouseNo}|{customerData.Soi}|{customerData.Road}|{customerData.Subdistrict}|{customerData.District}|{customerData.Province}|{customerData.Zipcode}|0107550000211";
                await headerWriter.WriteLineAsync(headerLine);
                headerCount++;

                //  2. ดึงข้อมูลพอร์ตจริงจาก FundConnext API -> GET /api/account/balances
                // ค้นหา Account ID ที่ถูกต้องสำหรับ FundConnext API โดยลองฟอร์แมตต่าง ๆ
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(customerData.FrontAccount))
                {
                    candidates.Add(customerData.FrontAccount);
                }
                candidates.Add(formattedAccount); // e.g. 0074001-7
                candidates.Add(dbAccount);        // e.g. 74001-7
                candidates.Add(formattedAccount.Replace("-", "")); // e.g. 00740017
                candidates.Add(dbAccount.Replace("-", ""));        // e.g. 740017

                // หากไม่มี frontaccount ใน DB ให้ลองฟอร์แมต 99 + account (แบบไม่มี dash) ด้วย
                if (string.IsNullOrEmpty(customerData.FrontAccount))
                {
                    string fallbackFront = "99" + dbAccount.Replace("-", "");
                    candidates.Add(fallbackFront); // e.g. 99740017
                }

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
                        //  3. สูตรคำนวณ: Unrealized Gain/Loss = amount - costAmount
                        decimal amount = fund.Amount;
                        decimal costAmount = fund.CostAmount;
                        decimal unrealized = amount - costAmount;

                        string unrealizedStr = unrealized.ToString("F2"); // แสดงผลลัพธ์ทศนิยม 2 ตำแหน่ง หากติดลบจะมีเครื่องหมาย - นำหน้าโดยอัตโนมัติ

                        //  ดึง AMC Code แบบ dynamic ตามชื่อกองทุน
                        string amcCode = "KSAM";
                        if (fund.FundCode.StartsWith("PRINCIPAL", StringComparison.OrdinalIgnoreCase))
                        {
                            amcCode = "PRINCIPAL";
                        }
                        else if (fund.FundCode.StartsWith("SCB", StringComparison.OrdinalIgnoreCase))
                        {
                            amcCode = "SCBAM";
                        }

                        //  4. บันทึกรายละเอียดลงไฟล์รายละเอียด (Detail - FSTKD)
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
        }

        return Results.Ok(new
        {
            Message = " ดำเนินการทำ Data Pipeline จากเส้นทาง Balance Inquiry สำเร็จเสร็จสิ้น!",
            TargetDate = targetDateStr,
            FilesGenerated = new { Header = headerFileName, Detail = detailFileName },
            ProcessedRows = new { HeaderRows = headerCount, DetailRows = detailCount },
            AuditTrail = summaryTrail
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($" ระบบประมวลผลขัดข้อง: {ex.Message}");
    }
});

app.Run();

// ===================================================================
//  โมเดลและฟังก์ชันดึงข้อมูลจากฐานข้อมูล SBA (Informix)
// ===================================================================

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

public static class SbaDatabaseService
{
    private const string ConnectionString = "DSN=SBA;Uid=airaftp;Pwd=airaftp@aira;";

    private static string _taccTable = "refdbnew@testsmonline:tacc";
    private static string _tcustTable = "refdbnew@testsmonline:tcust";
    private static string _tuserTable = "refdbnew@testsmonline:tuser";
    private static string _taddressTable = "refdbnew@testsmonline:tcustaddr";
    private static List<string> _taccColumns = new();
    private static List<string> _tcustColumns = new();
    private static List<string> _taddressColumns = new();
    private static List<string> _tuserColumns = new();
    private static bool _tablesResolved = false;

    private static async Task ResolveTableNamesAsync(OdbcConnection conn)
    {
        if (_tablesResolved) return;

        // 1. Resolve tacc table
        if (await TestTableAsync(conn, "refdbnew@testsmonline:tacc")) _taccTable = "refdbnew@testsmonline:tacc";
        else if (await TestTableAsync(conn, "tacc")) _taccTable = "tacc";
        else if (await TestTableAsync(conn, "refdb@testsmonline:tca")) _taccTable = "refdb@testsmonline:tca";
        else if (await TestTableAsync(conn, "tca")) _taccTable = "tca";

        // 2. Resolve tcust table
        if (await TestTableAsync(conn, "refdbnew@testsmonline:tcust")) _tcustTable = "refdbnew@testsmonline:tcust";
        else if (await TestTableAsync(conn, "tcust")) _tcustTable = "tcust";
        else if (await TestTableAsync(conn, "refdbnew@testsmonline:tcustinfo")) _tcustTable = "refdbnew@testsmonline:tcustinfo";
        else if (await TestTableAsync(conn, "refdb@testsmonline:tcustinfo")) _tcustTable = "refdb@testsmonline:tcustinfo";
        else if (await TestTableAsync(conn, "tcustinfo")) _tcustTable = "tcustinfo";

        // 3. Resolve tuser table
        if (await TestTableAsync(conn, "refdbnew@testsmonline:tuser")) _tuserTable = "refdbnew@testsmonline:tuser";
        else if (await TestTableAsync(conn, "refdb@testsmonline:tuser")) _tuserTable = "refdb@testsmonline:tuser";
        else if (await TestTableAsync(conn, "tuser")) _tuserTable = "tuser";

        // 4. Resolve taddress table
        if (await TestTableAsync(conn, "refdbnew@testsmonline:tcustaddr")) _taddressTable = "refdbnew@testsmonline:tcustaddr";
        else if (await TestTableAsync(conn, "refdbnew@testsmonline:taddress")) _taddressTable = "refdbnew@testsmonline:taddress";
        else if (await TestTableAsync(conn, "refdb@testsmonline:tcustaddr")) _taddressTable = "refdb@testsmonline:tcustaddr";
        else if (await TestTableAsync(conn, "tcustaddr")) _taddressTable = "tcustaddr";
        else if (await TestTableAsync(conn, "taddress")) _taddressTable = "taddress";

        _taccColumns = await GetTableColumnsAsync(conn, _taccTable);
        _tcustColumns = await GetTableColumnsAsync(conn, _tcustTable);
        _taddressColumns = await GetTableColumnsAsync(conn, _taddressTable);
        _tuserColumns = await GetTableColumnsAsync(conn, _tuserTable);

        Log.Information($"[DB Schema] Resolved tables: tacc={_taccTable}, tcust={_tcustTable}, tuser={_tuserTable}, taddress={_taddressTable}");
        Log.Information($"[DB Schema] tacc columns: {string.Join(", ", _taccColumns)}");
        Log.Information($"[DB Schema] tcust columns: {string.Join(", ", _tcustColumns)}");
        Log.Information($"[DB Schema] taddress columns: {string.Join(", ", _taddressColumns)}");
        Log.Information($"[DB Schema] tuser columns: {string.Join(", ", _tuserColumns)}");

        _tablesResolved = true;
    }

    private static async Task<List<string>> GetTableColumnsAsync(OdbcConnection conn, string tableName)
    {
        var cols = new List<string>();
        try
        {
            string pureTableName = tableName;
            string prefix = "";
            int colonIdx = tableName.LastIndexOf(':');
            if (colonIdx >= 0)
            {
                prefix = tableName.Substring(0, colonIdx + 1);
                pureTableName = tableName.Substring(colonIdx + 1);
            }

            string sql = $@"
                SELECT colname 
                FROM {prefix}syscolumns 
                WHERE tabid = (SELECT tabid FROM {prefix}systables WHERE tabname = '{pureTableName.ToLower()}')";

            using (var cmd = new OdbcCommand(sql, conn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string col = reader["colname"]?.ToString()?.Trim()?.ToLower() ?? "";
                    if (!string.IsNullOrEmpty(col)) cols.Add(col);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[DB Schema Debug] Cannot get columns of {tableName}: {ex.Message}");
        }
        return cols;
    }

    private static async Task<bool> TestTableAsync(OdbcConnection conn, string tableName)
    {
        try
        {
            // ใช้ "SELECT FIRST 1" ซึ่งเป็น syntax ของ Informix ในการทดสอบตาราง
            string sql = $"SELECT FIRST 1 * FROM {tableName}";
            using (var cmd = new OdbcCommand(sql, conn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public static async Task<List<string>> GetAccountsFromSbaAsync()
    {
        var accounts = new List<string>();
        try
        {
            using (var conn = new OdbcConnection(ConnectionString))
            {
                await conn.OpenAsync();
                await ResolveTableNamesAsync(conn);

                string accountCol = _taccColumns.Contains("account") ? "account" : "";
                string custacctCol = _taccColumns.Contains("custacct") ? "custacct" : "";

                if (!string.IsNullOrEmpty(accountCol) && !string.IsNullOrEmpty(custacctCol))
                {
                    // ดึงบัญชีที่ประเภทบัญชีเป็น 7 ตามเงื่อนไข (custacct = '7')
                    string sql = $"SELECT DISTINCT {accountCol} FROM {_taccTable} WHERE TRIM({custacctCol}) = '7'";
                    using (var cmd = new OdbcCommand(sql, conn))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            string acct = reader[accountCol]?.ToString()?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(acct))
                            {
                                accounts.Add(acct);
                            }
                        }
                    }
                }
            }
            Log.Information($"[DB] ดึงเลขบัญชีลูกค้าจากฐานข้อมูลสำเร็จ: {accounts.Count} บัญชี");
        }
        catch (Exception ex)
        {
            Log.Error($"[DB Error] ไม่สามารถดึงเลขบัญชีจากฐานข้อมูลได้ ({ex.Message})");
            throw;
        }

        return accounts;
    }

    public static async Task<CustomerData> GetCustomerDataFromSbaAsync(string dbAccount)
    {
        var data = new CustomerData();
        
        // แยกข้อมูล custcode และ custacct ออกจาก dbAccount (เช่น "99901-7" -> code="99901", suffix="7")
        string code = dbAccount;
        string suffix = "7";
        int dashIdx = dbAccount.IndexOf('-');
        if (dashIdx >= 0)
        {
            code = dbAccount.Substring(0, dashIdx);
            suffix = dbAccount.Substring(dashIdx + 1);
        }

        try
        {
            using (var conn = new OdbcConnection(ConnectionString))
            {
                await conn.OpenAsync();
                await ResolveTableNamesAsync(conn);

                string cardId = "";
                string branchCode = "";
                string frontaccountVal = "";

                // 1. ดึงข้อมูลสาขา, cardid และ frontaccount จาก tacc
                try
                {
                    string branchCol = _taccColumns.Contains("branch") ? "branch" : (_taccColumns.Contains("branchcode") ? "branchcode" : "");
                    string accountCol = _taccColumns.Contains("account") ? "account" : "";
                    string cardidCol = _taccColumns.Contains("cardid") ? "cardid" : "";
                    string frontaccountCol = _taccColumns.Contains("frontaccount") ? "frontaccount" : "";

                    var selectAccFields = new List<string>();
                    if (!string.IsNullOrEmpty(branchCol)) selectAccFields.Add(branchCol);
                    if (!string.IsNullOrEmpty(cardidCol)) selectAccFields.Add(cardidCol);
                    if (!string.IsNullOrEmpty(frontaccountCol)) selectAccFields.Add(frontaccountCol);

                    if (!string.IsNullOrEmpty(accountCol) && selectAccFields.Count > 0)
                    {
                        string sqlAcc = $"SELECT {string.Join(", ", selectAccFields)} FROM {_taccTable} WHERE TRIM({accountCol}) = ?";
                        using (var cmd = new OdbcCommand(sqlAcc, conn))
                        {
                            var param = new OdbcParameter("?", OdbcType.VarChar);
                            param.Value = dbAccount;
                            cmd.Parameters.Add(param);
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    if (!string.IsNullOrEmpty(branchCol)) branchCode = reader[branchCol]?.ToString()?.Trim() ?? "";
                                    if (!string.IsNullOrEmpty(cardidCol)) cardId = reader[cardidCol]?.ToString()?.Trim() ?? "";
                                    if (!string.IsNullOrEmpty(frontaccountCol)) frontaccountVal = reader[frontaccountCol]?.ToString()?.Trim() ?? "";
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(branchCode))
                    {
                        data.BranchName = (branchCode == "99" || branchCode == "00111" || branchCode == "01" || branchCode == "1") ? "สำนักงานใหญ่" : "สุรวงศ์";
                    }
                    data.FrontAccount = frontaccountVal;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[DB Error] Step 1 (tacc) failed for account {dbAccount}");
                }

                // 1.5 ดึงข้อมูล AE จาก tuser โดยใช้ frontaccountVal
                try
                {
                    if (!string.IsNullOrEmpty(frontaccountVal) && _tuserColumns.Count > 0)
                    {
                        string userKeyCol = "";
                        if (_tuserColumns.Contains("username")) userKeyCol = "username";
                        else if (_tuserColumns.Contains("userid")) userKeyCol = "userid";
                        else if (_tuserColumns.Contains("usercode")) userKeyCol = "usercode";
                        else if (_tuserColumns.Contains("user_id")) userKeyCol = "user_id";
                        else if (_tuserColumns.Contains("name")) userKeyCol = "name";

                        if (!string.IsNullOrEmpty(userKeyCol))
                        {
                            string aeNameCol = _tuserColumns.Contains("tname") ? "tname" : (_tuserColumns.Contains("name") ? "name" : "");
                            string aeSurnameCol = _tuserColumns.Contains("tsurname") ? "tsurname" : (_tuserColumns.Contains("surname") ? "surname" : "");

                            var userCols = new List<string>();
                            if (!string.IsNullOrEmpty(aeNameCol)) userCols.Add(aeNameCol);
                            if (!string.IsNullOrEmpty(aeSurnameCol)) userCols.Add(aeSurnameCol);

                            if (userCols.Count > 0)
                            {
                                string sqlUser = $"SELECT {string.Join(", ", userCols)} FROM {_tuserTable} WHERE TRIM({userKeyCol}) = ?";
                                using (var cmd = new OdbcCommand(sqlUser, conn))
                                {
                                    var param = new OdbcParameter("?", OdbcType.VarChar);
                                    param.Value = frontaccountVal;
                                    cmd.Parameters.Add(param);
                                    using (var reader = await cmd.ExecuteReaderAsync())
                                    {
                                        if (await reader.ReadAsync())
                                        {
                                            string aeName = !string.IsNullOrEmpty(aeNameCol) ? (reader[aeNameCol]?.ToString()?.Trim() ?? "") : "";
                                            string aeSurname = !string.IsNullOrEmpty(aeSurnameCol) ? (reader[aeSurnameCol]?.ToString()?.Trim() ?? "") : "";
                                            data.AEName = $"{aeName} {aeSurname}".Replace("  ", " ").Trim();
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[DB Error] Step 1.5 (tuser) failed for frontaccount {frontaccountVal}");
                }

                // 2. ดึงชื่อลูกค้าจาก tcust โดยใช้ cardid
                try
                {
                    if (!string.IsNullOrEmpty(cardId) && _tcustColumns.Contains("cardid"))
                    {
                        string titleCol = _tcustColumns.Contains("ttitle") ? "ttitle" : (_tcustColumns.Contains("title") ? "title" : "");
                        string nameCol = _tcustColumns.Contains("tname") ? "tname" : (_tcustColumns.Contains("name") ? "name" : "");
                        string surnameCol = _tcustColumns.Contains("tsurname") ? "tsurname" : (_tcustColumns.Contains("surname") ? "surname" : "");

                        var colsToSelect = new List<string>();
                        if (!string.IsNullOrEmpty(titleCol)) colsToSelect.Add(titleCol);
                        if (!string.IsNullOrEmpty(nameCol)) colsToSelect.Add(nameCol);
                        if (!string.IsNullOrEmpty(surnameCol)) colsToSelect.Add(surnameCol);

                        if (colsToSelect.Count > 0)
                        {
                            string sqlCust = $"SELECT {string.Join(", ", colsToSelect)} FROM {_tcustTable} WHERE TRIM(cardid) = ?";
                            using (var cmd = new OdbcCommand(sqlCust, conn))
                            {
                                var param = new OdbcParameter("?", OdbcType.VarChar);
                                param.Value = cardId;
                                cmd.Parameters.Add(param);
                                using (var reader = await cmd.ExecuteReaderAsync())
                                {
                                    if (await reader.ReadAsync())
                                    {
                                        string title = !string.IsNullOrEmpty(titleCol) ? (reader[titleCol]?.ToString()?.Trim() ?? "") : "";
                                        string name = !string.IsNullOrEmpty(nameCol) ? (reader[nameCol]?.ToString()?.Trim() ?? "") : "";
                                        string surname = !string.IsNullOrEmpty(surnameCol) ? (reader[surnameCol]?.ToString()?.Trim() ?? "") : "";

                                        data.CustomerName = $"{title}{name} {surname}".Replace("  ", " ").Trim();
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[DB Error] Step 2 (tcust) failed for cardId {cardId}");
                }

                // 3. ดึงที่อยู่ลูกค้าจาก tcustaddr / taddress โดยใช้ custcode หรือ cardid
                try
                {
                    string addressLinkCol = "";
                    if (_taddressColumns.Contains("custcode")) addressLinkCol = "custcode";
                    else if (_taddressColumns.Contains("cardid")) addressLinkCol = "cardid";

                    if (!string.IsNullOrEmpty(addressLinkCol))
                    {
                        string linkVal = addressLinkCol == "custcode" ? code : cardId;
                        if (!string.IsNullOrEmpty(linkVal))
                        {
                            string houseCol = _taddressColumns.Contains("houseno") ? "houseno" : "";
                            string soiCol = _taddressColumns.Contains("soi") ? "soi" : "";
                            string roadCol = _taddressColumns.Contains("road") ? "road" : "";
                            string tambonCol = _taddressColumns.Contains("tambon") ? "tambon" : (_taddressColumns.Contains("subdistrict") ? "subdistrict" : "");
                            string amphurCol = _taddressColumns.Contains("amphur") ? "amphur" : (_taddressColumns.Contains("district") ? "district" : "");
                            string provinceCol = _taddressColumns.Contains("province") ? "province" : "";
                            string zipcodeCol = _taddressColumns.Contains("zipcode") ? "zipcode" : "";

                            var selectFields = new List<string>();
                            if (!string.IsNullOrEmpty(houseCol)) selectFields.Add(houseCol);
                            if (!string.IsNullOrEmpty(soiCol)) selectFields.Add(soiCol);
                            if (!string.IsNullOrEmpty(roadCol)) selectFields.Add(roadCol);
                            if (!string.IsNullOrEmpty(tambonCol)) selectFields.Add(tambonCol);
                            if (!string.IsNullOrEmpty(amphurCol)) selectFields.Add(amphurCol);
                            if (!string.IsNullOrEmpty(provinceCol)) selectFields.Add(provinceCol);
                            if (!string.IsNullOrEmpty(zipcodeCol)) selectFields.Add(zipcodeCol);
                            if (_taddressColumns.Contains("addrtype")) selectFields.Add("addrtype");

                            if (selectFields.Count > 0)
                            {
                                string sqlAddr = $"SELECT {string.Join(", ", selectFields)} FROM {_taddressTable} WHERE TRIM({addressLinkCol}) = ?";
                                
                                using (var cmd = new OdbcCommand(sqlAddr, conn))
                                {
                                    var param = new OdbcParameter("?", OdbcType.VarChar);
                                    param.Value = linkVal;
                                    cmd.Parameters.Add(param);
                                    using (var reader = await cmd.ExecuteReaderAsync())
                                    {
                                        while (await reader.ReadAsync())
                                        {
                                            string currentAddrType = "";
                                            if (_taddressColumns.Contains("addrtype"))
                                            {
                                                currentAddrType = reader["addrtype"]?.ToString()?.Trim() ?? "";
                                            }

                                            // กรองเฉพาะที่อยู่บ้าน (H) หรือ ประเภท 1 (หรือถ้าไม่มีการระบุประเภท ให้เลือกแถวแรก)
                                            if (string.IsNullOrEmpty(currentAddrType) || currentAddrType.Equals("H", StringComparison.OrdinalIgnoreCase) || currentAddrType == "1")
                                            {
                                                if (!string.IsNullOrEmpty(houseCol)) data.HouseNo = reader[houseCol]?.ToString()?.Trim() ?? "";
                                                if (!string.IsNullOrEmpty(soiCol)) data.Soi = reader[soiCol]?.ToString()?.Trim() ?? "";
                                                if (!string.IsNullOrEmpty(roadCol)) data.Road = reader[roadCol]?.ToString()?.Trim() ?? "";
                                                if (!string.IsNullOrEmpty(tambonCol)) data.Subdistrict = reader[tambonCol]?.ToString()?.Trim() ?? "";
                                                if (!string.IsNullOrEmpty(amphurCol)) data.District = reader[amphurCol]?.ToString()?.Trim() ?? "";
                                                if (!string.IsNullOrEmpty(provinceCol)) data.Province = reader[provinceCol]?.ToString()?.Trim() ?? "";
                                                if (!string.IsNullOrEmpty(zipcodeCol)) data.Zipcode = reader[zipcodeCol]?.ToString()?.Trim() ?? "";

                                                if (currentAddrType.Equals("H", StringComparison.OrdinalIgnoreCase) || currentAddrType == "1")
                                                {
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[DB Error] Step 3 (tcustaddr) failed for linkVal {cardId}/{code}");
                }

                Log.Information($"[DB] ดึงข้อมูลลูกค้าสำเร็จสำหรับบัญชี {dbAccount}: ชื่อ={data.CustomerName}, สาขา={data.BranchName}, เจ้าหน้าที่={data.AEName}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[DB Warning] ไม่สามารถดึงข้อมูลลูกค้าของบัญชี {dbAccount} ได้ ({ex.Message})");
        }

        return data;
    }
}