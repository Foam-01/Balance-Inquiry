using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Serilog;

namespace SbaExporter
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .CreateLogger();

            string? accountNo = args.Length > 0 ? args[0] : null;

            try
            {
                Log.Information(" [SbaExporter] เริ่มต้นดึงข้อมูลจาก SBA...");
                
                // อ่านค่า Connection String ที่ส่งต่อมาจากโปรเซสแม่ผ่าน Environment Variable (หากมี)
                string? envConn = Environment.GetEnvironmentVariable("SBA_CONNECTION_STRING");
                if (!string.IsNullOrEmpty(envConn))
                {
                    SbaDatabaseService.ConnectionString = envConn;
                    Log.Information(" [SbaExporter] ใช้การเชื่อมต่อฐานข้อมูลตามที่ส่งมาจากโปรเซสแม่");
                }
                
                // ปิดการใช้งาน ODBC Connection Pooling เฉพาะในโปรเซสย่อย
                SbaDatabaseService.DisableOdbcPooling();

                bool isAllAccounts = string.IsNullOrWhiteSpace(accountNo) || accountNo.Equals("ALL", StringComparison.OrdinalIgnoreCase);
                List<string>? specificAccounts = isAllAccounts ? null : accountNo!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

                var dataList = SbaDatabaseService.LoadAllPipelineDataFromSba(specificAccounts);
                
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(dataList);
                string tempFilePath = Path.Combine(AppContext.BaseDirectory, "sba_temp_data.json");
                File.WriteAllText(tempFilePath, json, Encoding.UTF8);
                
                Log.Information($" [SbaExporter] ส่งออกข้อมูลลูกค้าจำนวน {dataList.Count} รายการ สำเร็จเรียบร้อย");
                Log.CloseAndFlush();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, " [SbaExporter] เกิดข้อผิดพลาดร้ายแรงในระหว่างส่งออกข้อมูล");
                Log.CloseAndFlush();
                Environment.Exit(1);
            }
        }
    }
}
