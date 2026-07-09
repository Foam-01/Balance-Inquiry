using System;
using System.IO;
using System.Xml;
using Serilog;

namespace FundBalanceDataPipeline.Infrastructure
{
    public static class AppConfigService
    {
        public static string GetConnectionString(string name)
        {
            try
            {
                string configPath = Path.Combine(AppContext.BaseDirectory, "App.config");
                if (!File.Exists(configPath))
                {
                    configPath = "App.config";
                }
                if (!File.Exists(configPath)) return "";

                var doc = new XmlDocument();
                doc.Load(configPath);
                var node = doc.SelectSingleNode($"//connectionStrings/add[@name='{name}']");
                return node?.Attributes["connectionString"]?.Value ?? "";
            }
            catch (Exception ex)
            {
                Log.Warning($"[Config Warning] ไม่สามารถอ่าน ConnectionString '{name}' จาก App.config ได้: {ex.Message}");
                return "";
            }
        }

        public static string GetAppSetting(string key)
        {
            try
            {
                string configPath = Path.Combine(AppContext.BaseDirectory, "App.config");
                if (!File.Exists(configPath))
                {
                    configPath = "App.config";
                }
                if (!File.Exists(configPath)) return "";

                var doc = new XmlDocument();
                doc.Load(configPath);
                var node = doc.SelectSingleNode($"//appSettings/add[@key='{key}']");
                return node?.Attributes["value"]?.Value ?? "";
            }
            catch (Exception ex)
            {
                Log.Warning($"[Config Warning] ไม่สามารถอ่าน AppSetting '{key}' จาก App.config ได้: {ex.Message}");
                return "";
            }
        }
    }
}
