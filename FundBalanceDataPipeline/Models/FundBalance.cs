using System;
using System.Collections.Generic;

namespace FundBalanceDataPipeline.Models
{
    // 1. กล่องเล็ก: สำหรับเก็บรายละเอียดของแต่ละกองทุนที่ลูกค้าถืออยู่
    public class FundBalanceDto
    {
        public string UnitholderId { get; set; }
        public string FundCode { get; set; }
        
        // มาตรฐานการเงิน: ใช้ประเภทข้อมูล 'decimal' เท่านั้น ห้ามใช้ float/double เด็ดขาด เพื่อป้องกันทศนิยมเพี้ยน
        public decimal Unit { get; set; }
        public decimal Amount { get; set; }
        public decimal RemainUnit { get; set; }
        public decimal RemainAmount { get; set; }
        public decimal AvgCost { get; set; }
        public decimal Nav { get; set; }
        public string NavDate { get; set; }
        public decimal CostAmount { get; set; }

        // เงื่อนไขพี่เลี้ยง: คำนวณ Gain/Loss อัตโนมัติ (Amount - CostAmount)
        public decimal UnrealizedGainLoss => Amount - CostAmount;
    }

    // 2. กล่องใหญ่: สำหรับรองรับโครงสร้าง JSON ทั้งก้อนที่ได้กลับมาจาก FundConnext API
    public class BalanceInquiryResponse
    {
        // ใน JSON จะมี Array ที่ชื่อว่า "result" บรรจุกล่องเล็กๆ (กองทุน) อยู่ข้างใน
        public List<FundBalanceDto> Result { get; set; }
    }
}