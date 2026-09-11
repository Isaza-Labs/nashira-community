using System.Text.Json;
using nashira_backend.Services.Inventory;

namespace nashira_backend.Tests;

// NetBox device JSON -> Device field mapping (defensive nested access, ip prefix
// stripping, role/device_role + status string/object handling).
public class NetBoxMappingTests
{
    [Fact]
    public void MapDevice_maps_core_fields_and_strips_ip_prefix()
    {
        const string json = """
            {"name":"edge-01",
             "primary_ip4":{"address":"10.1.2.3/24"},
             "platform":{"slug":"cisco_ios","name":"Cisco IOS"},
             "device_type":{"manufacturer":{"name":"Cisco"}},
             "site":{"name":"HQ","slug":"hq"},
             "role":{"name":"edge"},
             "status":{"value":"active","label":"Active"}}
            """;
        using var doc = JsonDocument.Parse(json);

        var m = NetBoxSyncService.MapDevice(doc.RootElement);

        Assert.NotNull(m);
        Assert.Equal("edge-01", m!.Name);
        Assert.Equal("10.1.2.3", m.Ip);
        Assert.Equal("cisco_ios", m.Platform);
        Assert.Equal("Cisco", m.Vendor);
        Assert.Equal("HQ", m.Site);
        Assert.Equal("edge", m.Role);
        Assert.Equal("active", m.Status);
    }

    [Fact]
    public void MapDevice_falls_back_to_device_role_and_string_status()
    {
        using var doc = JsonDocument.Parse("""{"name":"sw1","device_role":{"name":"access"},"status":"offline"}""");

        var m = NetBoxSyncService.MapDevice(doc.RootElement);

        Assert.NotNull(m);
        Assert.Equal("access", m!.Role);
        Assert.Equal("offline", m.Status);
        Assert.Equal(string.Empty, m.Ip);
        Assert.Equal(string.Empty, m.Platform);
    }

    [Fact]
    public void MapDevice_returns_null_when_name_is_missing()
    {
        using var doc = JsonDocument.Parse("""{"status":"active"}""");
        Assert.Null(NetBoxSyncService.MapDevice(doc.RootElement));
    }

    // The external id is what makes a re-sync idempotent across a rename, so it has to
    // survive NetBox reporting it as a number.
    [Fact]
    public void MapDevice_stringifies_the_numeric_netbox_id()
    {
        using var doc = JsonDocument.Parse("""{"name":"edge-01","id":4271}""");

        var m = NetBoxSyncService.MapDevice(doc.RootElement);

        Assert.Equal("4271", m!.ExternalId);
    }

    [Fact]
    public void MapDevice_leaves_the_external_id_null_when_netbox_omits_it()
    {
        using var doc = JsonDocument.Parse("""{"name":"edge-01"}""");

        // Null means "match by name instead", which is the pre-provenance behaviour.
        Assert.Null(NetBoxSyncService.MapDevice(doc.RootElement)!.ExternalId);
    }

    [Fact]
    public void MapDevice_carries_custom_fields_tags_and_serial_into_properties()
    {
        const string json = """
            {"name":"edge-01",
             "serial":"FTX1234",
             "custom_fields":{"maintenance_window":"sun-0200"},
             "tags":[{"name":"core"},{"slug":"managed"}],
             "tenant":{"name":"acme"},
             "device_type":{"model":"ISR4331"}}
            """;
        using var doc = JsonDocument.Parse(json);

        var props = NetBoxSyncService.MapDevice(doc.RootElement)!.Properties;

        Assert.Equal("FTX1234", props.GetProperty("serial").GetString());
        Assert.Equal("acme", props.GetProperty("tenant").GetString());
        Assert.Equal("ISR4331", props.GetProperty("device_type").GetString());
        Assert.Equal("sun-0200",
            props.GetProperty("custom_fields").GetProperty("maintenance_window").GetString());
        var tags = props.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(["core", "managed"], tags);
    }

    // Properties is a NOT NULL jsonb column, so a device with none of these attributes
    // still has to produce a writable value rather than an undefined JsonElement.
    [Fact]
    public void MapDevice_produces_an_empty_object_when_there_is_nothing_extra()
    {
        using var doc = JsonDocument.Parse("""{"name":"edge-01"}""");

        var props = NetBoxSyncService.MapDevice(doc.RootElement)!.Properties;

        Assert.Equal(JsonValueKind.Object, props.ValueKind);
        Assert.Empty(props.EnumerateObject());
    }
}
