using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace FundBalanceDataPipeline.Services
{
    public class AutoPipelineScheduler : BackgroundService
    {
        private readonly Func<string?, string?, Task<object>> _pipelineExecutor;
        private int _lastRunMonth = -1; // บันทึกสถานะเพื่อป้องกันการรันซ้ำซ้อนในเดือนเดียวกัน

        public AutoPipelineScheduler(Func<string?, string?, Task<object>> pipelineExecutor)
        {
            _pipelineExecutor = pipelineExecutor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information(" [Auto Run Scheduler] ระบบบริการรัน Pipeline อัตโนมัติเริ่มต้นทำงานแล้ว...");

            while (!stoppingToken.IsCancellationRequested)
            {
                DateTime now = DateTime.Now;

                // ตรวจสอบความถูกต้องของวันและเวลา:
                // 1. รันในเวลาตี 2 (02:00)
                // 2. วันนี้ต้องเป็นวันทำการวันแรกของเดือน (First Business Day) และไม่ตรงกับวันหยุดเสาร์-อาทิตย์
                // 3. ยังไม่ได้รันประมวลผลในเดือนนี้
                if (now.Hour == 2 && IsFirstBusinessDayOfMonth(now) && _lastRunMonth != now.Month)
                {
                    Log.Information($" [Auto Run Scheduler] ตรวจพบวันรันต้นเดือนตามเงื่อนไข (วันที่: {now:yyyy-MM-dd}): เริ่มการประมวลผล Data Pipeline อัตโนมัติ...");
                    
                    try
                    {
                        // รันดึงบัญชีทั้งหมด (ALL) และใช้วันทำการสิ้นเดือนของเดือนก่อนหน้าโดยอัตโนมัติ
                        var result = await _pipelineExecutor("ALL", null);
                        _lastRunMonth = now.Month; // บันทึกสถานะว่าเดือนนี้รันเสร็จแล้ว
                        Log.Information($" [Auto Run Scheduler] รัน Pipeline ประจำเดือนอัตโนมัติสำเร็จแล้ว!");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, " [Auto Run Scheduler] เกิดข้อผิดพลาดในระหว่างรันระบบอัตโนมัติประจำเดือน");
                    }
                }

                // หน่วงเวลาตรวจสอบทุกๆ 1 ชั่วโมง เพื่อไม่ให้เปลืองทรัพยากรระบบ
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        // ฟังก์ชันตรวจสอบว่าวันที่กำหนดเป็น "วันทำการวันแรกของเดือน" หรือไม่
        private bool IsFirstBusinessDayOfMonth(DateTime date)
        {
            // วันเสาร์-อาทิตย์ ไม่ใช่วันทำการ
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
            {
                return false;
            }

            // ค้นหาย้อนกลับไปหาวันทำการก่อนหน้านี้ในเดือนเดียวกัน
            for (int day = date.Day - 1; day >= 1; day--)
            {
                DateTime prevDate = new DateTime(date.Year, date.Month, day);
                // หากมีวันจันทร์-ศุกร์ วันอื่นในเดือนนี้ก่อนหน้านี้ แสดงว่าวันนี้ไม่ใช่วันทำการวันแรกของเดือน
                if (prevDate.DayOfWeek != DayOfWeek.Saturday && prevDate.DayOfWeek != DayOfWeek.Sunday)
                {
                    return false;
                }
            }

            return true; // วันนี้เป็นวันทำการวันแรกของเดือน
        }
    }
}
