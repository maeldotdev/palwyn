using Palwyn.Core;

public class DropFolderTests
{
    [Fact]
    public void Files_keep_their_folders_under_the_sent_folder()
    {
        // Native paths ("C:\Pics\Trip" on Windows, "/tmp/Pics/Trip" on Linux).
        var trip = Path.Combine(Path.GetTempPath(), "Pics", "Trip");
        Assert.Equal("Trip", DropFolders.RelativeFolder(trip, Path.Combine(trip, "a.jpg")));
        Assert.Equal("Trip/Day 1", DropFolders.RelativeFolder(trip + Path.DirectorySeparatorChar, Path.Combine(trip, "Day 1", "b.jpg")));
        Assert.Equal("Trip/Day 1/raw", DropFolders.RelativeFolder(trip, Path.Combine(trip, "Day 1", "raw", "c.dng")));
    }
}
