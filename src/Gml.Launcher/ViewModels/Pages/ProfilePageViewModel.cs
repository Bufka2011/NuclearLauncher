using System;
using System.Diagnostics;
using System.Management;
using System.Reactive.Concurrency;
using Gml.Client;
using Gml.Client.Models;
using Gml.Launcher.Assets;
using Gml.Launcher.Core.Services;
using Gml.Launcher.ViewModels.Base;
using GmlCore.Interfaces;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Sentry;
using System.IO;

namespace Gml.Launcher.ViewModels.Pages
{
    public class ProfilePageViewModel : PageViewModelBase
    {
        private readonly IGmlClientManager _manager;

        [Reactive] public string TextureUrl { get; set; }
        [Reactive] public IUser User { get; set; }

        // Новые свойства для характеристик ПК
        [Reactive] public string CpuInfo { get; set; }
        [Reactive] public string GpuInfo { get; set; }
        [Reactive] public string RamInfo { get; set; }
        [Reactive] public string DiskInfo { get; set; }  // Информация о накопителе
        [Reactive] public string FreeDiskSpace { get; set; }  // Свободное место на диске C

        internal ProfilePageViewModel(
            IScreen screen,
            IUser user,
            IGmlClientManager manager,
            ILocalizationService? localizationService = null)
            : base(screen, localizationService)
        {
            User = user ?? throw new ArgumentNullException(nameof(user));
            _manager = manager;

            RxApp.MainThreadScheduler.Schedule(LoadData);
            LoadHardwareInfo(); // Загрузка характеристик ПК
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
                using (var searcher = new ManagementObjectSearcher("SELECT Model, MediaType, Size, InterfaceType FROM Win32_DiskDrive"))
                {
                    foreach (var item in searcher.Get())
                    {
                        string diskModel = item["Model"].ToString();
                        string mediaType = item["MediaType"]?.ToString() ?? "Неизвестно";
                        string interfaceType = item["InterfaceType"]?.ToString() ?? "Неизвестно";
                        double diskSize = Convert.ToDouble(item["Size"]) / 1024 / 1024 / 1024; // В ГБ

                        // Проверяем MediaType (работает в Windows 10+)
                        if (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                        {
                            DiskInfo = $"Диск: {diskModel}\n    Тип: SSD\n    Объём: {Math.Round(diskSize)} ГБ";
                            break;
                        }

                        // Проверяем InterfaceType (если MediaType пустой)
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


                // Диски (определение NVMe, SATA, HDD)
                using (var searcher = new ManagementObjectSearcher("select * from Win32_DiskDrive"))
                {
                    foreach (var item in searcher.Get())
                    {
                        string diskModel = item["Model"].ToString();
                        string interfaceType = item["InterfaceType"]?.ToString() ?? "Неизвестно";
                        double diskSize = Convert.ToDouble(item["Size"]) / 1024 / 1024 / 1024; // В ГБ

                        string diskType = interfaceType switch
                        {
                            "NVMe" => "NVMe SSD",
                            "SATA" => "SATA SSD",
                            "SCSI" => "SAS HDD",
                            "IDE" => "HDD",
                            _ => "Неизвестно"
                        };

                        DiskInfo = $"Диск: {diskModel}\n    Тип: {diskType}\n    Объём: {Math.Round(diskSize)} ГБ";
                        break; // Берём первый найденный диск
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
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Ошибка получения информации о системе: {e.Message}");
            }
        }

        // Метод для определения типа RAM
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
    }
}
