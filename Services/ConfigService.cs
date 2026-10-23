using System;
using System.IO;
using System.Text.Json;
using PassFlowTotem.Models;

namespace PassFlowTotem.Services
{
    public class ConfigService
    {
        private readonly string _configFilePath;

        public ConfigService()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var localPath = Path.Combine(appDir, "kiosk_config.json");

            try
            {
                // Tenta gravar teste no diretório do app
                var testFile = Path.Combine(appDir, ".test_write");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                _configFilePath = localPath;
            }
            catch
            {
                // Fallback para AppData se estiver rodando em Program Files
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var folder = Path.Combine(appData, "PassFlowTotem");
                Directory.CreateDirectory(folder);
                _configFilePath = Path.Combine(folder, "kiosk_config.json");
            }
        }

        public DeviceConfig Load()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<DeviceConfig>(json);
                    if (config != null) return config;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao carregar configuração: {ex.Message}");
            }

            return new DeviceConfig();
        }

        public void Save(DeviceConfig config)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(_configFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao salvar configuração: {ex.Message}");
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    File.Delete(_configFilePath);
                }
            }
            catch { }
        }
    }
}
