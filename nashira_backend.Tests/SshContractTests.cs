using System.Text.Json;
using nashira_backend.Services.Ssh;

namespace nashira_backend.Tests;

// Locks the nashira <-> Python netmiko runner JSON contract: request serialization,
// result parsing, and exit-code taxonomy — no subprocess spawned.
public class SshContractTests
{
    [Fact]
    public void SerializeRequest_emits_runner_snake_case_and_omits_null_secrets()
    {
        var req = new SshRunRequest
        {
            Host = "10.0.0.1",
            Username = "netops",
            PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----",
            DeviceType = "cisco_ios",
            Commands = ["show version", "show ip int brief"],
            ExpectedFingerprint = "SHA256:abc",
            UseStructured = true,
        };

        using var doc = JsonDocument.Parse(SshCommandRunner.SerializeRequest(req));
        var root = doc.RootElement;

        Assert.Equal("10.0.0.1", root.GetProperty("host").GetString());
        Assert.Equal("netops", root.GetProperty("username").GetString());
        Assert.Equal("cisco_ios", root.GetProperty("device_type").GetString());
        Assert.Equal("SHA256:abc", root.GetProperty("expected_fingerprint").GetString());
        Assert.True(root.GetProperty("use_structured").GetBoolean());
        Assert.Equal(22, root.GetProperty("port").GetInt32());          // default
        Assert.True(root.GetProperty("stop_on_error").GetBoolean());    // default

        var cmds = root.GetProperty("commands");
        Assert.Equal(2, cmds.GetArrayLength());
        Assert.Equal("show version", cmds[0].GetString());

        // Unset secrets/knobs are omitted (WhenWritingNull), not sent as null.
        Assert.False(root.TryGetProperty("password", out _));
        Assert.False(root.TryGetProperty("enable_secret", out _));
        Assert.False(root.TryGetProperty("direct_exec", out _));
        Assert.StartsWith("-----BEGIN", root.GetProperty("private_key").GetString());
    }

    [Fact]
    public void ParseResult_maps_runner_success_output()
    {
        const string runnerJson = """
            {
              "results": [
                {"command":"show version","output":"Cisco IOS","output_raw":"Cisco IOS raw",
                 "parsed":[{"version":"17.3"}],"parser_used":"textfsm",
                 "template_name":"cisco_ios_show_version","elapsed_ms":412,"ok":true,"error":null}
              ],
              "device_type":"cisco_ios","device_type_resolved_from":"cisco","vendor":"cisco",
              "host_key_fingerprint":"SHA256:abc","device_type_fallback":null
            }
            """;

        var result = SshCommandRunner.ParseResult(runnerJson);

        Assert.NotNull(result);
        Assert.Equal("cisco_ios", result!.DeviceType);
        Assert.Equal("cisco", result.DeviceTypeResolvedFrom);
        Assert.Equal("SHA256:abc", result.HostKeyFingerprint);
        Assert.Null(result.DeviceTypeFallback);

        var r0 = Assert.Single(result.Results);
        Assert.Equal("show version", r0.Command);
        Assert.Equal("Cisco IOS", r0.Output);
        Assert.True(r0.Ok);
        Assert.Equal(412, r0.ElapsedMs);
        Assert.Equal("textfsm", r0.ParserUsed);
        Assert.NotNull(r0.Parsed);
        Assert.Equal(JsonValueKind.Array, r0.Parsed!.Value.ValueKind);
        Assert.Equal("17.3", r0.Parsed.Value[0].GetProperty("version").GetString());
    }

    [Fact]
    public void ClassifyExit_maps_runner_exit_code_taxonomy()
    {
        Assert.Equal("input_error", SshCommandRunner.ClassifyExit(1));
        Assert.Equal("auth_failed", SshCommandRunner.ClassifyExit(2));
        Assert.Equal("connect_timeout", SshCommandRunner.ClassifyExit(3));
        Assert.Equal("host_key_mismatch", SshCommandRunner.ClassifyExit(4));
        Assert.Equal("connect_failed", SshCommandRunner.ClassifyExit(5));
        Assert.Equal("runner_exception", SshCommandRunner.ClassifyExit(99));
        Assert.Equal("unknown", SshCommandRunner.ClassifyExit(7));
    }
}
