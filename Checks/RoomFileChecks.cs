using TohotopiaShared;

internal static class RoomFileChecks
{
    internal static void Run(string root)
    {
        string name = "status-write-fixture.json";
        string path = Path.Combine(root, name);
        RoomFiles.Write(root, name, new RoomStatus("old", "old"));
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            RoomFiles.Write(root, name, new RoomStatus("new", "new"));
            if (RoomFiles.Read<RoomStatus>(root, name)?.State != "new")
                throw new Exception("Status replacement did not publish the new JSON while a reader was open");
            using var oldReader = new StreamReader(reader);
            if (!oldReader.ReadToEnd().Contains("old"))
                throw new Exception("Status replacement changed the snapshot held by an existing reader");
        }
        Console.WriteLine("PASS status replacement succeeds with an open delete-sharing reader and preserves its snapshot");

        bool failed = false;
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { RoomFiles.Write(root, name, new RoomStatus("blocked", "blocked")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
            if (!failed || RoomFiles.Read<RoomStatus>(root, name)?.State != "new")
                throw new Exception("A failed replacement must preserve the previous complete status");
        }
        if (Directory.GetFiles(root, name + ".*.tmp").Length != 0)
            throw new Exception("Failed status replacement leaked temporary files");
        RoomFiles.Write(root, name, new RoomStatus("recovered", "recovered"));
        if (RoomFiles.Read<RoomStatus>(root, name)?.State != "recovered")
            throw new Exception("Status replacement did not recover after the blocker closed");
        Console.WriteLine("PASS failed status replacement preserves JSON, cleans up, and recovers after reader closes");
        File.Delete(path);
    }
}
