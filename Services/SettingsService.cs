using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

public static class SettingsService
{
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch { /* corrupt settings must never block startup */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.SettingsFile,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}