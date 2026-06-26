using System;
using System.Collections.Generic;
using System.Data.Odbc;
using FundBalanceDataPipeline.Models;
using Serilog; // ใช้ Serilog ในการพิมพ์บันทึกข้อมูลการทำงานแทนการ Console.WriteLine แบบเดิมๆ


namespace FundBalanceDataPipeline.Infrastructure
{
    public class CustomerRepository
    {
        private readonly string _connectionString;

        // รับค่าสายเชื่อมต่อ (Connection String) มาจากภายนอกตอนเรียกใช้งาน
        public CustomerRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public List<CustomerProfile> GetActiveCustomers()
        {
            var customers = new List<CustomerProfile>();

            // SQL Query สำหรับดึงฟิลด์ที่จำเป็นต้องใช้พิมพ์ออกไฟล์ FSTKH
            string sqlQuery = @"
                SELECT branch, account, fullname, houseno, soi, road, subdistrict, district, province, zipcode 
                FROM smart_client_table"; 
                // หมายเหตุ: ตรงชื่อตาราง smart_client_table แนะนำให้ไปเปลี่ยนเป็นชื่อตารางจริงในบริษัทตอนรันเทสจริงนะครับ

            try
            {
                // เปิดประตูเชื่อมต่อฐานข้อมูลแบบปลอดภัยด้วยการครอบ using
                using (var connection = new OdbcConnection(_connectionString))
                {
                    using (var command = new OdbcCommand(sqlQuery, connection))
                    {
                        connection.Open();
                        Log.Information("🔌 [DB] เชื่อมต่อ Smart Database สำเร็จ กำลังเริ่มดึงรายชื่อลูกค้า...");

                        using (var reader = command.ExecuteReader())
                        {
                            // วนลูปอ่านข้อมูลจาก DB ทีละแถวทีละบรรทัด จนกว่าจะหมด
                            while (reader.Read())
                            {
                                var customer = new CustomerProfile
                                {
                                    Branch = reader["branch"]?.ToString().Trim(),
                                    AccountNo = reader["account"]?.ToString().Trim(),
                                    FullName = reader["fullname"]?.ToString().Trim(),
                                    HouseNo = reader["houseno"]?.ToString().Trim(),
                                    Soi = reader["soi"]?.ToString().Trim(),
                                    Road = reader["road"]?.ToString().Trim(),
                                    SubDistrict = reader["subdistrict"]?.ToString().Trim(),
                                    District = reader["district"]?.ToString().Trim(),
                                    Province = reader["province"]?.ToString().Trim(),
                                    ZipCode = reader["zipcode"]?.ToString().Trim()
                                };

                                customers.Add(customer);
                            }
                        }
                    }
                }
                Log.Information("✅ [DB] ดึงข้อมูลลูกค้าสำเร็จเรียบร้อยแล้ว ทั้งหมด {Count} ราย", customers.Count);
            }
            catch (Exception ex)
            {
                // หาก Database ล่ม หรือคำสั่ง SQL มีปัญหา ระบบจะบันทึก Log ความผิดพลาดตัวนี้ไว้ทันที
                Log.Error(ex, "❌ [DB] เกิดข้อผิดพลาดร้ายแรงระหว่างดึงข้อมูลจาก Smart DB");
                throw; // ส่ง Error ต่อไปบอกระบบหลัก
            }

            return customers;
        }
    }
}