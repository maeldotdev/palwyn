using System.Text;
using System.Text.Json.Nodes;
using Palwyn.Core;
using Palwyn.Core.Protocol;

public class VectorTests
{
    static JsonObject Load(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", file)))!.AsObject();

    public static TheoryData<string> ProtocolVectors()
    {
        var data = new TheoryData<string>();
        foreach (var v in Load("test-vectors.json")["vectors"]!.AsArray()) data.Add(v!["name"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(ProtocolVectors))]
    public void Protocol_vector(string name)
    {
        var doc = Load("test-vectors.json");
        var v = doc["vectors"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!.AsObject();
        var caps = (v["capabilities"] ?? doc["defaults"]!["capabilities"])!.AsArray()
            .Select(c => c!.GetValue<string>()).ToHashSet();
        var receiver = v["receiver"]!.GetValue<string>() == "phone" ? Side.Phone : Side.Pc;
        var expect = v["expect"]!.GetValue<string>();

        string actual;
        if (v["lengthPrefix"] is { } len)
            actual = Envelope.LengthOk(len.GetValue<int>()) ? "accept" : "close";
        else
        {
            var (verdict, _) = Envelope.Validate(Encoding.UTF8.GetBytes(v["frame"]!.GetValue<string>()), receiver, caps);
            actual = verdict.Kind switch
            {
                VerdictKind.Accept => "accept",
                VerdictKind.Close => "close",
                _ => $"error:{verdict.Code}",
            };
        }
        Assert.Equal(expect, actual);
    }

    [Fact]
    public void Pairing_vectors()
    {
        var doc = Load("pairing-vectors.json");
        byte[] In(string k) => Hex.Parse(doc["inputs"]![k]!.GetValue<string>());
        string Out(string k) => doc["expected"]![k]!.GetValue<string>();

        Assert.Equal(Out("proof"), Hex.Of(PairingCrypto.Proof(In("secret"), In("fpPc"), In("fpPhone"))));
        Assert.Equal(Out("commit"), Hex.Of(PairingCrypto.Commit(In("nPc"))));
        Assert.Equal(Out("code"), PairingCrypto.Code(In("nPc"), In("nPhone"), In("fpPc"), In("fpPhone")));

        var invite = new PairingInvite(In("fpPc"), In("secret"), doc["inputs"]!["pcName"]!.GetValue<string>());
        Assert.Equal(Out("uri"), invite.ToUri());
        var parsed = PairingInvite.Parse(Out("uri"))!;
        Assert.Equal(invite.PcFingerprint, parsed.PcFingerprint);
        Assert.Equal(invite.Secret, parsed.Secret);
        Assert.Equal("DESKTOP 1", parsed.PcName);
    }

    [Theory]
    [InlineData("https://example.com/pair?v=1")]
    [InlineData("palwyn://pair?v=2&fp=oaGhoaGhoaGhoaGhoaGhoaGhoaGhoaGhoaGhoaGhoaE&s=ABEiM0RVZneImaq7zN3u_w&n=x")]
    [InlineData("palwyn://pair?v=1&fp=AAAA&s=ABEiM0RVZneImaq7zN3u_w&n=x")]
    [InlineData("palwyn://pair?v=1&s=ABEiM0RVZneImaq7zN3u_w")]
    public void Bad_invites_are_rejected(string uri) => Assert.Null(PairingInvite.Parse(uri));
}
