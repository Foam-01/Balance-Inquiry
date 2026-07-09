using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using Serilog;

namespace FundBalanceDataPipeline.Services
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

    public class SbaAccountRaw
    {
        public string Account { get; set; } = "";
        public string CardId { get; set; } = "";
        public string BranchCode { get; set; } = "";
        public string FrontAccount { get; set; } = "";
    }

    public class SbaCustRaw
    {
        public string Title { get; set; } = "";
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";
    }

    public class SbaAddrRaw
    {
        public string HouseNo { get; set; } = "";
        public string Soi { get; set; } = "";
        public string Road { get; set; } = "";
        public string Subdistrict { get; set; } = "";
        public string District { get; set; } = "";
        public string Province { get; set; } = "";
        public string Zipcode { get; set; } = "";
        public string AddrType { get; set; } = ""; // สำหรับใช้จัดลำดับความสำคัญในแรม
    }

    public class SbaUserRaw
    {
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";
    }

    public static class SbaDatabaseService
    {
        public static string ConnectionString { get; set; } = "DSN=SBA;Uid=airaftp;Pwd=airaftp@aira;";

        // ป้องกันไม่ให้ GC ทำลายการเชื่อมต่อ Native ODBC จนทำให้เกิด Access Violation ในระดับระบบปฏิบัติการ
        private static OdbcConnection? _keepAliveConn;

        private static string _taccTable = "refdbnew@testsmonline:tacc";
        private static string _tcustTable = "refdbnew@testsmonline:tcust";
        private static string _tuserTable = "refdbnew@testsmonline:tuser";
        private static string _taddressTable = "refdbnew@testsmonline:tcustaddr";
        private static List<string> _taccColumns = new();
        private static List<string> _tcustColumns = new();
        private static List<string> _taddressColumns = new();
        private static List<string> _tuserColumns = new();
        private static bool _tablesResolved = false;

        private static void ResolveTableNames(OdbcConnection conn)
        {
            if (_tablesResolved) return;

            // 1. Resolve tacc table
            if (TestTable(conn, "refdbnew@testsmonline:tacc")) _taccTable = "refdbnew@testsmonline:tacc";
            else if (TestTable(conn, "tacc")) _taccTable = "tacc";
            else if (TestTable(conn, "refdb@testsmonline:tca")) _taccTable = "refdb@testsmonline:tca";
            else if (TestTable(conn, "tca")) _taccTable = "tca";

            // 2. Resolve tcust table
            if (TestTable(conn, "refdbnew@testsmonline:tcust")) _tcustTable = "refdbnew@testsmonline:tcust";
            else if (TestTable(conn, "tcust")) _tcustTable = "tcust";
            else if (TestTable(conn, "refdbnew@testsmonline:tcustinfo")) _tcustTable = "refdbnew@testsmonline:tcustinfo";
            else if (TestTable(conn, "refdb@testsmonline:tcustinfo")) _tcustTable = "refdb@testsmonline:tcustinfo";
            else if (TestTable(conn, "tcustinfo")) _tcustTable = "tcustinfo";

            // 3. Resolve tuser table
            if (TestTable(conn, "refdbnew@testsmonline:tuser")) _tuserTable = "refdbnew@testsmonline:tuser";
            else if (TestTable(conn, "refdb@testsmonline:tuser")) _tuserTable = "refdb@testsmonline:tuser";
            else if (TestTable(conn, "tuser")) _tuserTable = "tuser";

            // 4. Resolve taddress table
            if (TestTable(conn, "refdbnew@testsmonline:tcustaddr")) _taddressTable = "refdbnew@testsmonline:tcustaddr";
            else if (TestTable(conn, "refdbnew@testsmonline:taddress")) _taddressTable = "refdbnew@testsmonline:taddress";
            else if (TestTable(conn, "refdb@testsmonline:tcustaddr")) _taddressTable = "refdb@testsmonline:tcustaddr";
            else if (TestTable(conn, "tcustaddr")) _taddressTable = "tcustaddr";
            else if (TestTable(conn, "taddress")) _taddressTable = "taddress";

            _taccColumns = GetTableColumns(conn, _taccTable);
            _tcustColumns = GetTableColumns(conn, _tcustTable);
            _taddressColumns = GetTableColumns(conn, _taddressTable);
            _tuserColumns = GetTableColumns(conn, _tuserTable);

            // Log.Debug($"[DB Schema] Resolved tables: tacc={_taccTable}, tcust={_tcustTable}, tuser={_tuserTable}, taddress={_taddressTable}");
            _tablesResolved = true;
        }

        private static List<string> GetTableColumns(OdbcConnection conn, string tableName)
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
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
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

        private static bool TestTable(OdbcConnection conn, string tableName)
        {
            try
            {
                string sql = $"SELECT FIRST 1 * FROM {tableName}";
                using (var cmd = new OdbcCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // ดึงข้อมูลลูกค้าทั้งหมดที่จะต้องใช้ในการทำ Pipeline ขึ้นมาบน Memory ทีเดียวเพื่อตัดปัญหา ODBC Driver แครช
        public static List<CustomerPipelineData> LoadAllPipelineDataFromSba(List<string>? specificAccounts = null)
        {
            var pipelineList = new List<CustomerPipelineData>();
            try
            {
                _keepAliveConn = new OdbcConnection(ConnectionString);
                _keepAliveConn.Open();
                var conn = _keepAliveConn;
                
                ResolveTableNames(conn);

                    // 1. ดึงข้อมูลดิบบัญชี tacc
                    var sbaAccounts = new List<SbaAccountRaw>();
                    string accountCol = _taccColumns.Contains("account") ? "account" : "";
                    string custacctCol = _taccColumns.Contains("custacct") ? "custacct" : "";
                    string branchCol = _taccColumns.Contains("branch") ? "branch" : (_taccColumns.Contains("branchcode") ? "branchcode" : "");
                    string cardidCol = _taccColumns.Contains("cardid") ? "cardid" : "";
                    string frontaccountCol = _taccColumns.Contains("frontaccount") ? "frontaccount" : "";

                    if (string.IsNullOrEmpty(accountCol) || string.IsNullOrEmpty(custacctCol))
                    {
                        throw new Exception("ไม่พบโครงสร้างตารางบัญชีใน SBA");
                    }

                    var selectAccFields = new List<string> { accountCol, custacctCol };
                    if (!string.IsNullOrEmpty(branchCol)) selectAccFields.Add(branchCol);
                    if (!string.IsNullOrEmpty(cardidCol)) selectAccFields.Add(cardidCol);
                    if (!string.IsNullOrEmpty(frontaccountCol)) selectAccFields.Add(frontaccountCol);

                    string sqlAcc = $"SELECT {string.Join(", ", selectAccFields)} FROM {_taccTable}";
                    if (specificAccounts != null && specificAccounts.Count > 0)
                    {
                        var formattedSpec = specificAccounts.Select(a => $"'{a.Trim()}'");
                        sqlAcc += $" WHERE TRIM({accountCol}) IN ({string.Join(", ", formattedSpec)})";
                    }
                    else
                    {
                        sqlAcc += $" WHERE TRIM({custacctCol}) = '7'";
                    }

                    using (var cmd = new OdbcCommand(sqlAcc, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var acc = new SbaAccountRaw
                            {
                                Account = reader[accountCol]?.ToString()?.Trim() ?? "",
                                CardId = !string.IsNullOrEmpty(cardidCol) ? (reader[cardidCol]?.ToString()?.Trim() ?? "") : "",
                                BranchCode = !string.IsNullOrEmpty(branchCol) ? (reader[branchCol]?.ToString()?.Trim() ?? "") : "",
                                FrontAccount = !string.IsNullOrEmpty(frontaccountCol) ? (reader[frontaccountCol]?.ToString()?.Trim() ?? "") : ""
                            };
                            if (!string.IsNullOrEmpty(acc.Account)) sbaAccounts.Add(acc);
                        }
                    }

                    Log.Information($"[DB] โหลดข้อมูลบัญชีสำเร็จ {sbaAccounts.Count} บัญชี. กำลังโหลดข้อมูลตารางอื่นๆ...");

                    // 2. ดึงตารางที่เกี่ยวข้องมาทำ Cache บนแรม (Dictionary)
                    // 2.1 ตารางลูกค้า (tcust)
                    var custDict = new Dictionary<string, SbaCustRaw>();
                    if (_tcustColumns.Contains("cardid"))
                    {
                        string titleCol = _tcustColumns.Contains("ttitle") ? "ttitle" : (_tcustColumns.Contains("title") ? "title" : "");
                        string nameCol = _tcustColumns.Contains("tname") ? "tname" : (_tcustColumns.Contains("name") ? "name" : "");
                        string surnameCol = _tcustColumns.Contains("tsurname") ? "tsurname" : (_tcustColumns.Contains("surname") ? "surname" : "");

                        var cols = new List<string> { "cardid" };
                        if (!string.IsNullOrEmpty(titleCol)) cols.Add(titleCol);
                        if (!string.IsNullOrEmpty(nameCol)) cols.Add(nameCol);
                        if (!string.IsNullOrEmpty(surnameCol)) cols.Add(surnameCol);

                        string sqlCust = $"SELECT {string.Join(", ", cols)} FROM {_tcustTable}";
                        using (var cmd = new OdbcCommand(sqlCust, conn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string cid = reader["cardid"]?.ToString()?.Trim() ?? "";
                                if (!string.IsNullOrEmpty(cid) && !custDict.ContainsKey(cid))
                                {
                                    custDict[cid] = new SbaCustRaw
                                    {
                                        Title = !string.IsNullOrEmpty(titleCol) ? (reader[titleCol]?.ToString()?.Trim() ?? "") : "",
                                        Name = !string.IsNullOrEmpty(nameCol) ? (reader[nameCol]?.ToString()?.Trim() ?? "") : "",
                                        Surname = !string.IsNullOrEmpty(surnameCol) ? (reader[surnameCol]?.ToString()?.Trim() ?? "") : ""
                                    };
                                }
                            }
                        }
                    }

                    // 2.2 ตารางที่อยู่ (tcustaddr)
                    var addrDict = new Dictionary<string, SbaAddrRaw>();
                    string addressLinkCol = _taddressColumns.Contains("custcode") ? "custcode" : (_taddressColumns.Contains("cardid") ? "cardid" : "");
                    if (!string.IsNullOrEmpty(addressLinkCol))
                    {
                        string houseCol = _taddressColumns.Contains("homeno") ? "homeno" : (_taddressColumns.Contains("houseno") ? "houseno" : "");
                        string soiCol = _taddressColumns.Contains("soi") ? "soi" : "";
                        string roadCol = _taddressColumns.Contains("road") ? "road" : "";
                        string tambonCol = _taddressColumns.Contains("tambon") ? "tambon" : (_taddressColumns.Contains("subdistrict") ? "subdistrict" : "");
                        string amphurCol = _taddressColumns.Contains("amphur") ? "amphur" : (_taddressColumns.Contains("district") ? "district" : "");
                        string provinceCol = _taddressColumns.Contains("provincedesc") ? "provincedesc" : (_taddressColumns.Contains("province") ? "province" : "");
                        string zipcodeCol = _taddressColumns.Contains("zipcode") ? "zipcode" : "";

                        var selectFields = new List<string> { addressLinkCol };
                        if (!string.IsNullOrEmpty(houseCol)) selectFields.Add(houseCol);
                        if (!string.IsNullOrEmpty(soiCol)) selectFields.Add(soiCol);
                        if (!string.IsNullOrEmpty(roadCol)) selectFields.Add(roadCol);
                        if (!string.IsNullOrEmpty(tambonCol)) selectFields.Add(tambonCol);
                        if (!string.IsNullOrEmpty(amphurCol)) selectFields.Add(amphurCol);
                        if (!string.IsNullOrEmpty(provinceCol)) selectFields.Add(provinceCol);
                        if (!string.IsNullOrEmpty(zipcodeCol)) selectFields.Add(zipcodeCol);
                        if (_taddressColumns.Contains("addrtype")) selectFields.Add("addrtype");

                        string sqlAddr = $"SELECT {string.Join(", ", selectFields)} FROM {_taddressTable}";

                        using (var cmd = new OdbcCommand(sqlAddr, conn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string linkVal = reader[addressLinkCol]?.ToString()?.Trim() ?? "";
                                string currentAddrType = _taddressColumns.Contains("addrtype") ? (reader["addrtype"]?.ToString()?.Trim() ?? "") : "";

                                // คัดกรองเฉพาะที่อยู่ประเภทบ้าน (H หรือ 1) ในฝั่ง C#
                                if (string.IsNullOrEmpty(currentAddrType) || currentAddrType.Equals("H", StringComparison.OrdinalIgnoreCase) || currentAddrType == "1")
                                {
                                    if (!string.IsNullOrEmpty(linkVal))
                                    {
                                        var newAddr = new SbaAddrRaw
                                        {
                                            HouseNo = !string.IsNullOrEmpty(houseCol) ? (reader[houseCol]?.ToString()?.Trim() ?? "") : "",
                                            Soi = !string.IsNullOrEmpty(soiCol) ? (reader[soiCol]?.ToString()?.Trim() ?? "") : "",
                                            Road = !string.IsNullOrEmpty(roadCol) ? (reader[roadCol]?.ToString()?.Trim() ?? "") : "",
                                            Subdistrict = !string.IsNullOrEmpty(tambonCol) ? (reader[tambonCol]?.ToString()?.Trim() ?? "") : "",
                                            District = !string.IsNullOrEmpty(amphurCol) ? (reader[amphurCol]?.ToString()?.Trim() ?? "") : "",
                                            Province = !string.IsNullOrEmpty(provinceCol) ? (reader[provinceCol]?.ToString()?.Trim() ?? "") : "",
                                            Zipcode = !string.IsNullOrEmpty(zipcodeCol) ? (reader[zipcodeCol]?.ToString()?.Trim() ?? "") : "",
                                            AddrType = currentAddrType
                                        };

                                        if (!addrDict.ContainsKey(linkVal))
                                        {
                                            addrDict[linkVal] = newAddr;
                                        }
                                        else
                                        {
                                            // ถ้ามีอยู่แล้ว ให้ดูว่าตัวใหม่เป็น H หรือ 1 (ที่สำคัญกว่าค่าว่างเดิม) หรือไม่ ถ้าใช่ให้เขียนทับ
                                            string existingType = addrDict[linkVal].AddrType;
                                            bool existingIsHome = !string.IsNullOrEmpty(existingType) && (existingType.Equals("H", StringComparison.OrdinalIgnoreCase) || existingType == "1");
                                            bool newIsHome = currentAddrType.Equals("H", StringComparison.OrdinalIgnoreCase) || currentAddrType == "1";

                                            if (!existingIsHome && newIsHome)
                                            {
                                                addrDict[linkVal] = newAddr;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 2.3 ตารางพนักงานขาย (tuser)
                    var userDict = new Dictionary<string, SbaUserRaw>();
                    string userKeyCol = "";
                    if (_tuserColumns.Contains("username")) userKeyCol = "username";
                    else if (_tuserColumns.Contains("userid")) userKeyCol = "userid";
                    else if (_tuserColumns.Contains("usercode")) userKeyCol = "usercode";
                    else if (_tuserColumns.Contains("user_id")) userKeyCol = "user_id";

                    if (!string.IsNullOrEmpty(userKeyCol))
                    {
                        string aeNameCol = _tuserColumns.Contains("usertname") ? "usertname" : (_tuserColumns.Contains("tname") ? "tname" : (_tuserColumns.Contains("name") ? "name" : ""));
                        string aeSurnameCol = _tuserColumns.Contains("usertsurname") ? "usertsurname" : (_tuserColumns.Contains("tsurname") ? "tsurname" : (_tuserColumns.Contains("surname") ? "surname" : ""));

                        var userCols = new List<string> { userKeyCol };
                        if (!string.IsNullOrEmpty(aeNameCol)) userCols.Add(aeNameCol);
                        if (!string.IsNullOrEmpty(aeSurnameCol)) userCols.Add(aeSurnameCol);

                        string sqlUser = $"SELECT {string.Join(", ", userCols)} FROM {_tuserTable}";
                        using (var cmd = new OdbcCommand(sqlUser, conn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string ukey = reader[userKeyCol]?.ToString()?.Trim() ?? "";
                                if (!string.IsNullOrEmpty(ukey) && !userDict.ContainsKey(ukey))
                                {
                                    userDict[ukey] = new SbaUserRaw
                                    {
                                        Name = !string.IsNullOrEmpty(aeNameCol) ? (reader[aeNameCol]?.ToString()?.Trim() ?? "") : "",
                                        Surname = !string.IsNullOrEmpty(aeSurnameCol) ? (reader[aeSurnameCol]?.ToString()?.Trim() ?? "") : ""
                                    };
                                }
                            }
                        }
                    }

                    // 3. ประกอบข้อมูลทั้งหมดเข้าด้วยกันบนแรม (In-Memory mapping)
                    foreach (var sbaAcc in sbaAccounts)
                    {
                        var data = new CustomerData();
                        
                        // กำหนดสาขา
                        if (!string.IsNullOrEmpty(sbaAcc.BranchCode))
                        {
                            data.BranchName = (sbaAcc.BranchCode == "99" || sbaAcc.BranchCode == "00111" || sbaAcc.BranchCode == "01" || sbaAcc.BranchCode == "1") ? "สำนักงานใหญ่" : "สุรวงศ์";
                        }
                        data.FrontAccount = sbaAcc.FrontAccount;

                        // ดึงพนักงานขาย AE
                        if (!string.IsNullOrEmpty(sbaAcc.FrontAccount) && userDict.TryGetValue(sbaAcc.FrontAccount, out var userRaw))
                        {
                            data.AEName = $"{userRaw.Name} {userRaw.Surname}".Replace("  ", " ").Trim();
                        }

                        // ดึงชื่อลูกค้า
                        if (!string.IsNullOrEmpty(sbaAcc.CardId) && custDict.TryGetValue(sbaAcc.CardId, out var custRaw))
                        {
                            data.CustomerName = $"{custRaw.Title}{custRaw.Name} {custRaw.Surname}".Replace("  ", " ").Trim();
                        }

                        // ดึงที่อยู่
                        string addressKey = addressLinkCol == "custcode" ? sbaAcc.Account : sbaAcc.CardId;
                        if (addressLinkCol == "custcode" && !string.IsNullOrEmpty(addressKey))
                        {
                            int dashIdx = addressKey.IndexOf('-');
                            if (dashIdx >= 0) addressKey = addressKey.Substring(0, dashIdx);
                        }

                        if (!string.IsNullOrEmpty(addressKey) && addrDict.TryGetValue(addressKey, out var addrRaw))
                        {
                            data.HouseNo = addrRaw.HouseNo;
                            data.Soi = addrRaw.Soi;
                            data.Road = addrRaw.Road;
                            data.Subdistrict = addrRaw.Subdistrict;
                            data.District = addrRaw.District;
                            data.Province = addrRaw.Province;
                            data.Zipcode = addrRaw.Zipcode;
                        }

                        pipelineList.Add(new CustomerPipelineData
                        {
                            AccountNo = sbaAcc.Account,
                            DbAccount = sbaAcc.Account.IndexOf('-') >= 0 ? sbaAcc.Account.Substring(0, sbaAcc.Account.IndexOf('-')) : sbaAcc.Account,
                            Customer = data
                        });
                    }
                Log.Information($"[DB] ดึงข้อมูลดิบจาก SBA ขึ้นมาเก็บบน Memory สำเร็จ: {pipelineList.Count} รายการ (ทำการปิดฐานข้อมูล SBA เรียบร้อย)");
            }
            catch (Exception ex)
            {
                Log.Error($"[DB Error] ไม่สามารถโหลดข้อมูลขึ้น Memory ได้: {ex.Message}");
                throw;
            }
            return pipelineList;
        }
    }
}
