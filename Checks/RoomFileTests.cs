using TohotopiaShared;
using Xunit;

public class RoomFileTests
{
    [Fact]
    public void Atomic_status_replacement_preserves_readers()
    {
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-roomfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string name = "status-write-fixture.json";
            string path = Path.Combine(root, name);
            RoomFiles.Write(root, name, new RoomStatus("old", "old"));
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                RoomFiles.Write(root, name, new RoomStatus("new", "new"));
                Assert.True(RoomFiles.Read<RoomStatus>(root, name)?.State == "new", "status replacement publishes the new JSON while a reader is open");
                using var oldReader = new StreamReader(reader);
                Assert.True(oldReader.ReadToEnd().Contains("old"), "status replacement preserves the snapshot held by an existing reader");
            }

            bool failed = false;
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { RoomFiles.Write(root, name, new RoomStatus("blocked", "blocked")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
                Assert.True(failed && RoomFiles.Read<RoomStatus>(root, name)?.State == "new", "a failed replacement must preserve the previous complete status");
            }
            Assert.True(Directory.GetFiles(root, name + ".*.tmp").Length == 0, "failed status replacement leaks no temporary files");
            RoomFiles.Write(root, name, new RoomStatus("recovered", "recovered"));
            Assert.True(RoomFiles.Read<RoomStatus>(root, name)?.State == "recovered", "status replacement recovers after the blocker closes");
        }
        finally { Directory.Delete(root, true); }
    }
}
