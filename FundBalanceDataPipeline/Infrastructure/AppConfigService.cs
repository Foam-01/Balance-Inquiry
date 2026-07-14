using System;
using System.IO;
using System.Xml;
using Serilog;

namespace FundBalanceDataPipeline.Infrastructure
{
    public static class AppConfigService
    {
        private static string GetConfigPath()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string assemblyName = assembly.GetName().Name ?? "";
            
            if (!string.IsNullOrEmpty(assemblyName))
            {
                string exeConfig = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.exe.config");
                if (File.Exists(exeConfig)) return exeConfig;

                string dllConfig = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll.config");
                if (File.Exists(dllConfig)) return dllConfig;
            }

            string baseAppConfig = Path.Combine(AppContext.BaseDirectory, "App.config");
            if (File.Exists(baseAppConfig)) return baseAppConfig;

            string currentAppConfig = "App.config";
            if (File.Exists(currentAppConfig)) return currentAppConfig;

            return "";
        }

        public static string GetConnectionString(string name)
        {
            try
            {
                string configPath = GetConfigPath();
                if (string.IsNullOrEmpty(configPath)) return "";

                var doc = new XmlDocument();
                doc.Load(configPath);
                var node = doc.SelectSingleNode($"//connectionStrings/add[@name='{name}']");
                return node?.Attributes?["connectionString"]?.Value ?? "";
            }
            catch (Exception ex)
            {
                Log.Warning($"[Config Warning] ไม่สามารถอ่าน ConnectionString '{name}' ได้: {ex.Message}");
                return "";
            }
        }

        public static string GetAppSetting(string key)
        {
            try
            {
                string configPath = GetConfigPath();
                if (string.IsNullOrEmpty(configPath)) return "";

                var doc = new XmlDocument();
                doc.Load(configPath);
                var node = doc.SelectSingleNode($"//appSettings/add[@key='{key}']");
                return node?.Attributes?["value"]?.Value ?? "";
            }
            catch (Exception ex)
            {
                Log.Warning($"[Config Warning] ไม่สามารถอ่าน AppSetting '{key}' ได้: {ex.Message}");
                return "";
            }
        }
    }
}
