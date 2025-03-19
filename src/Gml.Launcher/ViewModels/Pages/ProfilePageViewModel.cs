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

namespace Gml.Launcher.ViewModels.Pages;

public class ProfilePageViewModel : PageViewModelBase
{
    private readonly IGmlClientManager _manager;

    [Reactive] public string TextureUrl { get; set; }
    [Reactive] public IUser User { get; set; }

    // Новые свойства
    [Reactive] public string CpuInfo { get; set; }
    [Reactive] public string GpuInfo { get; set; }
    [Reactive] public string RamInfo { get; set; }

    internal ProfilePageViewModel(
        IScreen screen,
        IUser user,
        IGmlClientManager manager,
        ILocalizationService? localizationService = null) : base(screen, localizationService)
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
                CpuInfo = $"ЦП: {item["Name"]}\n" +
                          $"    Ядер: {item["NumberOfCores"]}\n" +
                          $"    Потоков: {item["ThreadCount"]}";
                break;
            }
        }

        // GPU (ищем первую дискретную, если нет - берём любую)
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
                        break; // Нашли дискретную, можно выходить
                    }

                    if (integratedGpu == null)
                    {
                        integratedGpu = $"Видеокарта: {gpuName}\n    Память: {Math.Round(gpuMemory)} ГБ";
                    }
                }
            }

            GpuInfo = discreteGpu ?? integratedGpu ?? "Видеокарта: Не найдена";
        }

        // RAM (округляем до целого числа)
        using (var searcher = new ManagementObjectSearcher("select * from Win32_ComputerSystem"))
        {
            foreach (var item in searcher.Get())
            {
                RamInfo = $"RAM: {Math.Round(Convert.ToDouble(item["TotalPhysicalMemory"]) / 1024 / 1024 / 1024)} ГБ";
                break;
            }
        }
    }
    catch (Exception e)
    {
        Debug.WriteLine($"Ошибка получения информации о системе: {e.Message}");
    }
}

}
