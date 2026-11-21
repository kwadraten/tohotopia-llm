using System;
using System.IO;
using System.Text;
#if TH1_LOADER
using TohotopiaMod.Json;
#else
using System.Text.Json;
#endif
namespace TohotopiaShared;

public record AgentEvent(DateTimeOffset Time, string Source, string Stage, string Message, string Level = "info", uint MapId = 0, uint PlayerId = 0, uint Turn = 0, int AiSlot = 0, string Provider = "", string Model = "", string RequestId = "", string CandidateId = "", int Sequence = 0);
public static class AgentEvents
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new(RoomFiles.Json) { WriteIndented = false };
    public static void Write(string root, AgentEvent entry)
    {
        // Diagnostics must never interrupt gameplay or authentication.
        try
        {
            lock (Gate)
            {
                string dir = Path.Combine(root, "logs");
                Directory.CreateDirectory(dir);
                string source = entry.Source == "game" ? "game" : "host";
                string path = Path.Combine(dir, $"agents-{source}-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
#if AGENT_HOST
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Tohotopia.HostJson.Type<AgentEvent>(Json)) + "\n");
#elif SETTINGS_APP
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Settings.SettingsJson.Type<AgentEvent>(Json)) + "\n");
#else
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
#endif
                stream.Write(data, 0, data.Length);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
