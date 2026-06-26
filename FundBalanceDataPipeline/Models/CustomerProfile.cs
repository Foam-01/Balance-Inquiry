using System;

namespace FundBalanceDataPipeline.Models
{
    public class CustomerProfile
    {
        // ข้อมูลที่ดึงมาจาก Smart DB (จะตรงกับฟอร์แมตไฟล์ FSTKH)
        public string Branch { get; set; }
        public string AccountNo { get; set; } // เลขบัญชีดิบจาก DB เช่น "99901-7"
        public string FullName { get; set; }
        public string HouseNo { get; set; }
        public string Soi { get; set; }
        public string Road { get; set; }
        public string SubDistrict { get; set; }
        public string District { get; set; }
        public string Province { get; set; }
        public string ZipCode { get; set; }

        // Enterprise Touch: เขียน Property พิเศษให้มันแปลงฟอร์แมตเลขบัญชีเองอัตโนมัติ
        // เรียกใช้ปุ๊บ จะเติม 00 นำหน้าให้ทันทีตามเงื่อนไขพี่เลี้ยง (เช่น จาก "99901-7" เป็น "0099901-7")
        public string FormattedAccountNo => "00" + AccountNo;
    }
}