using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace scarpa_connection_manager_win.Services;

public class UnpackagedSettings
{
    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scarpa", "global_defaults.json");

    public UnpackagedSettingsValues Values { get; } = new UnpackagedSettingsValues();

    public class UnpackagedSettingsValues
    {
        private Dictionary<string, dynamic> _data = new();

        public UnpackagedSettingsValues()
        {
            if (File.Exists(SettingsFile))
            {
                try
                {
                    var json = File.ReadAllText(SettingsFile);
                    var temp = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (temp != null)
                    {
                        foreach (var kvp in temp)
                        {
                            if (kvp.Value.ValueKind == JsonValueKind.Number)
                            {
                                if (kvp.Value.TryGetInt32(out int i)) _data[kvp.Key] = i;
                                else if (kvp.Value.TryGetDouble(out double d)) _data[kvp.Key] = d;
                            }
                            else if (kvp.Value.ValueKind == JsonValueKind.String)
                            {
                                _data[kvp.Key] = kvp.Value.GetString() ?? "";
                            }
                            else if (kvp.Value.ValueKind == JsonValueKind.True) _data[kvp.Key] = true;
                            else if (kvp.Value.ValueKind == JsonValueKind.False) _data[kvp.Key] = false;
                        }
                    }
                }
                catch { }
            }
        }

        public dynamic? this[string key]
        {
            get => _data.TryGetValue(key, out var val) ? val : null;
            set
            {
                if (value == null) _data.Remove(key);
                else _data[key] = value;

                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}
