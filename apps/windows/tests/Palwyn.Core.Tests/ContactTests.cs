using System.Text.Json.Nodes;
using Palwyn.Core;
using Palwyn.Core.Protocol;

public class ContactTests
{
    static readonly PhoneContact Ana = new("7", "Ana Cruz",
        [new("+63 917 123 4567", "mobile", "Mobile"), new("8123", "other", "Gym")], ["ana@example.com"]);

    [Fact]
    public void Search_matches_name_email_and_number_digits()
    {
        Assert.True(Ana.Matches("cruz"));
        Assert.True(Ana.Matches("EXAMPLE"));
        Assert.True(Ana.Matches("0917 12")); // national 0 dropped for a +63 number
        Assert.False(PhoneContact.NumberMatches("0917", "09639179018")); // but not inside a national one
        Assert.True(PhoneContact.NumberMatches("0917", "09171234567"));
        Assert.True(Ana.Matches(""));
        Assert.False(Ana.Matches("12")); // too few digits to mean a number
        Assert.False(Ana.Matches("bob"));
    }

    [Fact]
    public void Same_number_across_formats()
    {
        Assert.True(PhoneContact.SameNumber("09171234567", "+63 917 123 4567"));
        Assert.False(PhoneContact.SameNumber("09171234567", "09171234568"));
        Assert.True(PhoneContact.SameNumber("8123", "8123"));
        Assert.False(PhoneContact.SameNumber("8123", "18123"));
        Assert.True(Ana.HasNumber("639171234567"));
    }

    [Fact]
    public void Save_payload_round_trips_and_passes_the_catalog()
    {
        var p = Ana.ToSavePayload();
        Assert.True(Catalog.Types["CONTACT_SAVE"].PayloadValid(p));
        Assert.Equal("7", p["id"]!.GetValue<string>());
        Assert.Null(p["numbers"]![0]!["label"]); // labels only for custom "other"
        Assert.Equal("Gym", p["numbers"]![1]!["label"]!.GetValue<string>());
        Assert.Null((Ana with { Id = "" }).ToSavePayload()["id"]);

        var page = new JsonObject { ["more"] = true, ["contacts"] = new JsonArray(p.DeepClone()) };
        var (list, more) = PhoneContact.PageFrom(page);
        Assert.True(more);
        Assert.Equal("Ana Cruz", list[0].Name);
        Assert.Equal("Gym", list[0].Numbers[1].Label);
    }
}
