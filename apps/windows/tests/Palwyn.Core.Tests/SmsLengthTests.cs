using Palwyn.Core;

public class SmsLengthTests
{
    [Fact]
    public void Plain_text_fits_160_per_text_and_emoji_switch_to_70()
    {
        Assert.Equal((0, false), SmsLength.Measure(""));
        Assert.Equal((1, false), SmsLength.Measure(new string('a', 160)));
        Assert.Equal((2, false), SmsLength.Measure(new string('a', 161)));
        Assert.Equal((2, false), SmsLength.Measure(new string('a', 159) + "€")); // € takes two septets
        Assert.Equal((1, true), SmsLength.Measure("See you later 😊"));
        Assert.Equal((1, true), SmsLength.Measure(new string('a', 68) + "😊")); // emoji = 2 UTF-16 units: 70
        Assert.Equal((2, true), SmsLength.Measure(new string('a', 69) + "😊"));
    }

    [Fact]
    public void Group_senders_and_mms_previews()
    {
        var group = new SmsThread("1", ["+639171234567", "09181112222"], ["Ana", ""], "", DateTimeOffset.Now, false);
        Assert.True(group.IsGroup);
        Assert.Null(group.ReplyAddress);
        Assert.Equal("Ana", group.NameFor("0917 123 4567"));
        Assert.Equal("09181112222", group.NameFor("+639181112222"));
        Assert.Equal("+15550100", group.NameFor("+15550100")); // not in the conversation

        SmsMessage Mms(string body, params string[] mimes) => new("mms-1", "1", "+15550100", null, body, DateTimeOffset.Now, false,
            Parts: mimes.Select((m, i) => new MmsPart($"{i}", m, null)).ToList());
        Assert.Equal("Hi", Mms("Hi", "image/jpeg").Preview);
        Assert.Equal("Picture", Mms("", "image/jpeg").Preview);
        Assert.Equal("2 pictures", Mms("", "image/jpeg", "image/png").Preview);
        Assert.Equal("Attachment", Mms("", "image/jpeg", "video/3gpp").Preview);
    }
}
