using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq; // Для усреднения результатов
using Gml.Client;
using Gml.Client.Models;
using Gml.Launcher.Assets;
using Gml.Launcher.Core.Services;
using Gml.Launcher.ViewModels.Base;
using GmlCore.Interfaces;
using Newtonsoft.Json;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Sentry;

namespace Gml.Launcher.ViewModels.Pages
{
    public class ProfilePageViewModel : PageViewModelBase
    {
        private readonly IGmlClientManager _manager;
        private readonly string _speedFilePath = "internet_speed.json";

        [Reactive] public string TextureUrl { get; set; }
        [Reactive] public IUser User { get; set; }

        // Свойства для характеристик ПК
        [Reactive] public string CpuInfo { get; set; }
        [Reactive] public string GpuInfo { get; set; }
        [Reactive] public string RamInfo { get; set; }
        [Reactive] public string DiskInfo { get; set; }
        [Reactive] public string FreeDiskSpace { get; set; }

        // Свойства для результата проверки совместимости
        [Reactive] public string CompatibilityMessage { get; set; }
        [Reactive] public string CompatibilityColor { get; set; }

        // Свойство для скорости интернета
        [Reactive] public string InternetSpeed { get; set; }

        // Индикатор выполнения теста
        [Reactive] public bool IsCheckingSpeed { get; set; }

        // Команда для проверки скорости интернета
        public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CheckInternetSpeedCommand { get; }

        internal ProfilePageViewModel(
            IScreen screen,
            IUser user,
            IGmlClientManager manager,
            ILocalizationService? localizationService = null)
            : base(screen, localizationService)
        {
            User = user ?? throw new ArgumentNullException(nameof(user));
            _manager = manager;

            // Инициализация команды для проверки скорости интернета
            CheckInternetSpeedCommand = ReactiveCommand.CreateFromTask(CheckInternetSpeedAsync);

            RxApp.MainThreadScheduler.Schedule(LoadData);
            LoadHardwareInfo();
            LoadInternetSpeed();
        }

        public new string Title => LocalizationService.GetString(ResourceKeysDictionary.MainPageTitle);

        private async void LoadData()
        {
            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss:fff}] Loading texture data...]");
                var userTextureInfo = await _manager.GetTexturesByName(User.Name);

                if (userTextureInfo is null)
                    return;

                TextureUrl = userTextureInfo.FullSkinUrl ?? string.Empty;
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss:fff}] Textures updated: {TextureUrl}");
            }
            catch (Exception exception)
            {
                SentrySdk.CaptureException(exception);
            }
        }

        private void LoadHardwareInfo()
        {
            try
            {
                // CPU
                using (var searcher = new ManagementObjectSearcher("select * from Win32_Processor"))
                {
                    foreach (var item in searcher.Get())
                    {
                        double clockSpeed = Convert.ToDouble(item["MaxClockSpeed"]) / 1000.0; // ГГц
                        CpuInfo = $"ЦП: {item["Name"]}\n" +
                                  $"    Ядер: {item["NumberOfCores"]}\n" +
                                  $"    Потоков: {item["ThreadCount"]}\n" +
                                  $"    Частота: {clockSpeed} ГГц";
                        break;
                    }
                }

                // GPU
                using (var searcher = new ManagementObjectSearcher("select * from Win32_VideoController"))
                {
                    string? discreteGpu = null;
                    string? integratedGpu = null;

                    foreach (var item in searcher.Get())
                    {
                        string gpuName = item["Name"].ToString();
                        double gpuMemory = Convert.ToDouble(item["AdapterRAM"]) / 1024 / 1024 / 1024;

                        if (gpuMemory > 0) // Игнорируем виртуальные GPU
                        {
                            if (gpuName.Contains("NVIDIA") || gpuName.Contains("AMD") || gpuName.Contains("GeForce") || gpuName.Contains("Radeon"))
                            {
                                discreteGpu = $"Видеокарта: {gpuName}\n    Память: {Math.Round(gpuMemory)} ГБ";
                                break;
                            }

                            if (integratedGpu == null)
                            {
                                integratedGpu = $"Видеокарта: {gpuName}\n    Память: {Math.Round(gpuMemory)} ГБ";
                            }
                        }
                    }

                    GpuInfo = discreteGpu ?? integratedGpu ?? "Видеокарта: Не найдена";
                }

                // RAM (объём и частота)
                using (var searcher = new ManagementObjectSearcher("select * from Win32_PhysicalMemory"))
                {
                    double totalRam = 0;
                    int ramSpeed = 0;
                    string ramType = "Неизвестно";

                    foreach (var item in searcher.Get())
                    {
                        totalRam += Convert.ToDouble(item["Capacity"]) / 1024 / 1024 / 1024; // В ГБ
                        ramSpeed = Convert.ToInt32(item["Speed"]); // Частота в MT/s
                        ramType = GetRamType(Convert.ToInt32(item["SMBIOSMemoryType"]));
                    }

                    RamInfo = $"RAM: {Math.Round(totalRam)} ГБ ({ramSpeed} MT/s, {ramType})";
                }

                // Диски (определение NVMe, SATA, HDD)
                using (var searcher = new ManagementObjectSearcher("SELECT Model, MediaType, Size, InterfaceType FROM Win32_DiskDrive"))
                {
                    foreach (var item in searcher.Get())
                    {
                        string diskModel = item["Model"].ToString();
                        string mediaType = item["MediaType"]?.ToString() ?? "Неизвестно";
                        string interfaceType = item["InterfaceType"]?.ToString() ?? "Неизвестно";
                        double diskSize = Convert.ToDouble(item["Size"]) / 1024 / 1024 / 1024; // В ГБ

                        if (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                        {
                            DiskInfo = $"Диск: {diskModel}\n    Тип: SSD\n    Объём: {Math.Round(diskSize)} ГБ";
                            break;
                        }

                        string diskType = interfaceType switch
                        {
                            "NVMe" => "NVMe SSD",
                            "SATA" => "SATA SSD",
                            "SCSI" => "SAS HDD",
                            "IDE" => "HDD",
                            _ => "Неизвестно"
                        };

                        DiskInfo = $"Диск: {diskModel}\n    Тип: {diskType}\n    Объём: {Math.Round(diskSize)} ГБ";
                        break;
                    }
                }

                // Свободное место на диске C
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.Name == "C:\\")
                    {
                        double freeSpace = drive.TotalFreeSpace / 1024.0 / 1024 / 1024; // В ГБ
                        FreeDiskSpace = $"Свободно на C: {Math.Round(freeSpace)} ГБ";
                        break;
                    }
                }

                // Проверка совместимости после загрузки данных
                CheckCompatibility();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Ошибка получения информации о системе: {e.Message}");
            }
        }

        private string GetRamType(int type)
        {
            return type switch
            {
                26 => "DDR4",
                34 => "DDR5",
                24 => "DDR3",
                21 => "DDR2",
                20 => "DDR",
                _ => "Неизвестно"
            };
        }

        private void CheckCompatibility()
        {
            const int minCpuCores = 2;
            const double minCpuFrequency = 2.0;
            const double minRam = 4.0;
            const double minFreeSpace = 1.0;
            const double minGpuMemory = 0.0;

            const int recCpuCores = 4;
            const double recCpuFrequency = 3.0;
            const double recRam = 8.0;
            const double recFreeSpace = 4.0;
            const double recGpuMemory = 2.0;

            var (cpuCores, cpuFrequency) = ParseCpuInfo(CpuInfo ?? string.Empty);
            double ram = ParseRamInfo(RamInfo ?? "RAM: 0 ГБ");
            var (isDiscreteGpu, gpuMemory) = ParseGpuInfo(GpuInfo ?? "Видеокарта: Не найдена");
            double freeSpace = ParseFreeDiskSpace(FreeDiskSpace ?? "Свободно на C: 0 ГБ");

            bool meetsMinCpu = cpuCores >= minCpuCores && cpuFrequency >= minCpuFrequency;
            bool meetsMinRam = ram >= minRam;
            bool meetsMinGpu = isDiscreteGpu || gpuMemory >= minGpuMemory;
            bool meetsMinFreeSpace = freeSpace >= minFreeSpace;

            bool meetsMinRequirements = meetsMinCpu && meetsMinRam && meetsMinGpu && meetsMinFreeSpace;

            bool meetsRecCpu = cpuCores >= recCpuCores && cpuFrequency >= recCpuFrequency;
            bool meetsRecRam = ram >= recRam;
            bool meetsRecGpu = isDiscreteGpu && gpuMemory >= recGpuMemory;
            bool meetsRecFreeSpace = freeSpace >= recFreeSpace;

            bool meetsRecRequirements = meetsRecCpu && meetsRecRam && meetsRecGpu && meetsRecFreeSpace;

            if (!meetsMinRequirements)
            {
                CompatibilityMessage = "ТРЕБУЕТСЯ ЗАМЕНА КОМПЛЕКТУЮЩИХ";
                CompatibilityColor = "Red";
            }
            else if (!meetsRecRequirements)
            {
                CompatibilityMessage = "ВОЗМОЖНЫ ПРОБЛЕМЫ";
                CompatibilityColor = "Yellow";
            }
            else
            {
                CompatibilityMessage = "ВСЁ ОТЛИЧНО!";
                CompatibilityColor = "Green";
            }
        }

        private (int cores, double frequency) ParseCpuInfo(string cpuInfo)
        {
            int cores = 0;
            double frequency = 0.0;

            var lines = cpuInfo.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("Ядер:"))
                {
                    cores = int.Parse(line.Split(':')[1].Trim());
                }
                else if (line.Contains("Частота:"))
                {
                    frequency = double.Parse(line.Split(':')[1].Trim().Replace(" ГГц", "").Replace(',', '.'));
                }
            }

            return (cores, frequency);
        }

        private double ParseRamInfo(string ramInfo)
        {
            var ramStr = ramInfo.Split(':')[1].Trim().Split(' ')[0];
            return double.Parse(ramStr);
        }

        private (bool isDiscrete, double memory) ParseGpuInfo(string gpuInfo)
        {
            if (gpuInfo.Contains("Не найдена"))
                return (false, 0.0);

            var memoryStr = gpuInfo.Split('\n')[1].Split(':')[1].Trim().Split(' ')[0];
            double memory = double.Parse(memoryStr);

            bool isDiscrete = gpuInfo.Contains("NVIDIA") || gpuInfo.Contains("AMD") ||
                              gpuInfo.Contains("GeForce") || gpuInfo.Contains("Radeon");

            return (isDiscrete, memory);
        }

        private double ParseFreeDiskSpace(string freeDiskSpace)
        {
            var spaceStr = freeDiskSpace.Split(':')[1].Trim().Split(' ')[0];
            return double.Parse(spaceStr);
        }

        private void LoadInternetSpeed()
        {
            try
            {
                if (File.Exists(_speedFilePath))
                {
                    var json = File.ReadAllText(_speedFilePath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    InternetSpeed = data["InternetSpeed"];
                }
                else
                {
                    InternetSpeed = "Нажмите, чтобы измерить скорость";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки скорости интернета: {ex.Message}");
                InternetSpeed = "Ошибка загрузки данных";
            }
        }

        private async Task CheckInternetSpeedAsync()
        {
            try
            {
                IsCheckingSpeed = true;
                InternetSpeed = "Проверка...";

                const string testUrl = "http://speedtest.tele2.net/10MB.zip"; // Тестовый файл
                const long fileSizeBytes = 10 * 1024 * 1024; // 10 МБ
                const int iterations = 3; // Количество итераций для усреднения

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                // Список для хранения результатов скорости
                var speeds = new List<double>();

                for (int i = 0; i < iterations; i++)
                {
                    var stopwatch = Stopwatch.StartNew();

                    var response = await httpClient.GetAsync(testUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        InternetSpeed = $"Ошибка: Сервер отклонил запрос (код {response.StatusCode})";
                        return;
                    }

                    var content = await response.Content.ReadAsByteArrayAsync();
                    stopwatch.Stop();

                    double timeSeconds = stopwatch.Elapsed.TotalSeconds;
                    if (timeSeconds == 0) // Избегаем деления на ноль
                    {
                        InternetSpeed = "Ошибка: Время загрузки равно нулю";
                        return;
                    }

                    double speedMbps = (fileSizeBytes * 8) / (timeSeconds * 1024 * 1024); // Скорость в Мбит/с
                    speeds.Add(speedMbps);

                    // Небольшая пауза между итерациями, чтобы сервер не блокировал запросы
                    await Task.Delay(500);
                }

                // Усредняем скорость, исключая первый результат (он может быть искажён из-за начальных задержек)
                double averageSpeed = speeds.Skip(1).Any() ? speeds.Skip(1).Average() : speeds.Average();
                InternetSpeed = $"Скорость интернета: {averageSpeed:F2} Мбит/с";
                SaveInternetSpeed(InternetSpeed);
            }
            catch (HttpRequestException ex)
            {
                InternetSpeed = "Ошибка: Не удалось подключиться к серверу";
                Debug.WriteLine($"Ошибка измерения скорости: {ex.Message}");
            }
            catch (Exception ex)
            {
                InternetSpeed = "Ошибка измерения скорости";
                Debug.WriteLine($"Ошибка измерения скорости: {ex.Message}");
            }
            finally
            {
                IsCheckingSpeed = false;
            }
        }

        private void SaveInternetSpeed(string speed)
        {
            try
            {
                var data = new Dictionary<string, string>();
                data.Add("InternetSpeed", speed);
                var json = JsonConvert.SerializeObject(data);
                File.WriteAllText(_speedFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения скорости интернета: {ex.Message}");
            }
        }
    }
}
