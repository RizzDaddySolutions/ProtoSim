using ProtoSim.Protocols.Rest;

namespace ProtoSim.Rest.Tests;

public sealed class RestClientTests
{
    private const string Body = """{"site":"A","sensors":[{"id":1,"value":21.5,"ok":true},{"id":2,"value":19}],"count":2}""";

    [Fact]
    public void Flatten_GivesOneRowPerValue()
    {
        var rows = RestApiClient.Rows(Body, "");
        Assert.Contains(rows, r => r.Address == "site" && (string?)r.Typed == "A");
        Assert.Contains(rows, r => r.Address == "sensors[0].value" && r.Typed is double d && d == 21.5);
        Assert.Contains(rows, r => r.Address == "sensors[0].ok" && r.Typed is true);
        Assert.Contains(rows, r => r.Address == "count" && r.Typed is long l && l == 2);
    }

    [Fact]
    public void Extract_FollowsPaths()
    {
        var rows = RestApiClient.Rows(Body, "sensors[1].value, $.site, missing.path");
        Assert.Equal(3, rows.Count);
        Assert.Equal(19L, rows[0].Typed);
        Assert.Equal("A", rows[1].Typed);
        Assert.Equal("missing", rows[2].Raw);
    }

    [Fact]
    public void PlainText_IsOneRow()
    {
        var row = Assert.Single(RestApiClient.Rows("42", "")); // valid JSON number
        Assert.Equal(42L, row.Typed);
        row = Assert.Single(RestApiClient.Rows("OK ready", ""));
        Assert.Equal("body", row.Address);
    }
}
