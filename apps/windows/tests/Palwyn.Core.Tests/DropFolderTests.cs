using Palwyn.Core;

public class DropFolderTests
{
    [Fact]
    public void Files_keep_their_folders_under_the_sent_folder()
    {
        Assert.Equal("Trip", DropFolders.RelativeFolder(@"C:\Pics\Trip", @"C:\Pics\Trip\a.jpg"));
        Assert.Equal("Trip/Day 1", DropFolders.RelativeFolder(@"C:\Pics\Trip\", @"C:\Pics\Trip\Day 1\b.jpg"));
        Assert.Equal("Trip/Day 1/raw", DropFolders.RelativeFolder(@"C:\Pics\Trip", @"C:\Pics\Trip\Day 1\raw\c.dng"));
    }
}
