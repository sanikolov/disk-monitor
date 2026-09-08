using System.Text.Json;
using System.Text.Json.Serialization;
using DiskMonitor.Models;

namespace DiskMonitor.Infrastructure;

internal interface IMonitorStateStore
{
    MonitorState Load();
    void Save(MonitorState state);
}

internal sealed class JsonStateStore : IMonitorStateStore
{
    private readonly MonitorLogger _logger;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonStateStore(MonitorLogger logger)
    {
        _logger = logger;
    }

    public MonitorState Load()
    {
        try
        {
            if (!File.Exists(AppPaths.StateFile))
            {
                return new MonitorState();
            }

            var json = File.ReadAllText(AppPaths.StateFile);
            return JsonSerializer.Deserialize<MonitorState>(json, _options) ?? new MonitorState();
        }
        catch (Exception ex)
        {
            _logger.Warn("Unable to load saved monitor state; starting with an empty state.", ex);
            return new MonitorState();
        }
    }

    public void Save(MonitorState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temporary = AppPaths.StateFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, _options));
            File.Move(temporary, AppPaths.StateFile, true);
        }
        catch (Exception ex)
        {
            _logger.Warn("Unable to save monitor state.", ex);
        }
    }
}
