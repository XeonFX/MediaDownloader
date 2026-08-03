using System.Net;
using FluentAssertions;
using MediaDownloader.Services.Api;

namespace MediaDownloader.Tests.Services.Api;

public class AgentApiAuthTests
{
    private const string Token = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";
    private static readonly AgentAccess.Snapshot Enabled = new(true, false, Token);
    private static readonly AgentAccess.Snapshot RemoteEnabled = new(true, true, Token);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_IsAllowed_WithoutToken(string address)
    {
        Evaluate(Enabled, address).Should().Be(AgentAuthResult.Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong")]
    [InlineData("Basic 012345")]
    public void Remote_IsUnauthorized_WithoutMatchingBearerToken(string? authorization)
    {
        Evaluate(RemoteEnabled, "192.168.1.20", authorization: authorization)
            .Should().Be(AgentAuthResult.Unauthorized);
    }

    [Fact]
    public void Remote_IsAllowed_WithMatchingBearerToken()
    {
        Evaluate(RemoteEnabled, "192.168.1.20", authorization: $"Bearer {Token}")
            .Should().Be(AgentAuthResult.Allowed);
    }

    [Fact]
    public void Remote_IsHidden_WhenRemoteAccessIsOff()
    {
        Evaluate(Enabled, "192.168.1.20", authorization: $"Bearer {Token}")
            .Should().Be(AgentAuthResult.RemoteDisabled);
    }

    [Fact]
    public void Remote_PlainHttp_IsRejected_BeforeCheckingToken()
    {
        Evaluate(RemoteEnabled, "192.168.1.20", isHttps: false, authorization: $"Bearer {Token}")
            .Should().Be(AgentAuthResult.InsecureTransport);
    }

    [Theory]
    [InlineData("http://evil.example", "localhost:47820")]
    [InlineData("http://evil.example", "evil.example")]
    [InlineData("null", "localhost:47820")]
    [InlineData("http://localhost:9999", "localhost:47820")]
    public void CrossOrigin_IsForbidden_EvenForLoopbackAndValidToken(string origin, string host)
    {
        Evaluate(Enabled, "127.0.0.1", origin, host, $"Bearer {Token}")
            .Should().Be(AgentAuthResult.ForbiddenOrigin);
    }

    [Fact]
    public void SameOrigin_IsAllowed_ForLoopback()
    {
        Evaluate(Enabled, "127.0.0.1", "http://localhost:47820", "localhost:47820")
            .Should().Be(AgentAuthResult.Allowed);
    }

    [Fact]
    public void Disabled_Feature_ReturnsDisabled_BeforeAnyOtherPolicy()
    {
        var disabled = new AgentAccess.Snapshot(false, true, Token);
        Evaluate(disabled, "127.0.0.1", "http://evil.example", "localhost:47820", $"Bearer {Token}")
            .Should().Be(AgentAuthResult.Disabled);
    }

    [Fact]
    public void UnknownRemoteAddress_DoesNotReceiveLoopbackExemption()
    {
        AgentApiAuth.Evaluate(RemoteEnabled, null, true, null, "localhost:47820", null)
            .Should().Be(AgentAuthResult.Unauthorized);
    }

    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/downloads", true)]
    [InlineData("/mcp", true)]
    [InlineData("/openapi/v1.json", true)]
    [InlineData("/mcpx", false)]
    [InlineData("/settings", false)]
    public void GuardsOnlyAgentPaths(string path, bool expected)
    {
        AgentApiAuth.GuardsPath(path).Should().Be(expected);
    }

    private static AgentAuthResult Evaluate(
        AgentAccess.Snapshot access,
        string address,
        string? origin = null,
        string host = "localhost:47820",
        string? authorization = null,
        bool isHttps = true) =>
        AgentApiAuth.Evaluate(access, IPAddress.Parse(address), isHttps, origin, host, authorization);
}
