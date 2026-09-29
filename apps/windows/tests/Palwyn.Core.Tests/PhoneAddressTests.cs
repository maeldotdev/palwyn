using Palwyn.Core.Link;

public class PhoneAddressTests
{
    [Fact]
    public void Addresses_parse_with_or_without_a_port()
    {
        Assert.Equal(("100.64.1.2", 47800), PairedPhone.ParseAddress(" 100.64.1.2 ", 47800));
        Assert.Equal(("phone.tailnet", 5000), PairedPhone.ParseAddress("phone.tailnet:5000", 47800));
        Assert.Equal(("fd7a:115c::1", 47800), PairedPhone.ParseAddress("fd7a:115c::1", 47800));
        Assert.Equal(("fd7a:115c::1", 9000), PairedPhone.ParseAddress("[fd7a:115c::1]:9000", 47800));
        Assert.Null(PairedPhone.ParseAddress("phone:0", 47800));
        Assert.Null(PairedPhone.ParseAddress("phone:abc", 47800));
        Assert.Null(PairedPhone.ParseAddress("my phone", 47800));
        Assert.Null(PairedPhone.ParseAddress("", 47800));

        var phone = new PairedPhone("id", "Phone", "00", "192.168.1.5", 47800, DateTimeOffset.Now);
        Assert.Equal(("192.168.1.5", 47800), phone.Endpoint);
        Assert.Equal(("100.64.1.2", 47800), (phone with { Address = "100.64.1.2" }).Endpoint);
    }
}
