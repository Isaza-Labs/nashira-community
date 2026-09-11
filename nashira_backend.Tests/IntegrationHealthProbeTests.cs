using System.Text.Json;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Integration;
using nashira_backend.Services.Integration;

namespace nashira_backend.Tests;

// The URL an integration's health check actually probes. Getting this wrong is
// expensive out of proportion to the code: the check is what an operator trusts when
// deciding whether a system is down, and a bad URL reports "unreachable" for a system
// that is answering perfectly.
public class IntegrationHealthProbeTests
{
    private const string Base = "https://netbox.example.com/api";

    [Fact]
    public void A_relative_path_is_appended_to_the_base_url()
    {
        Assert.Equal($"{Base}/status/", IntegrationHealthChecker.BuildProbeUrl(Base, "/status/"));
        Assert.Equal($"{Base}/status/", IntegrationHealthChecker.BuildProbeUrl(Base, "status/"));
    }

    [Fact]
    public void An_empty_path_probes_the_base_url_itself()
    {
        Assert.Equal(Base, IntegrationHealthChecker.BuildProbeUrl(Base, null));
        Assert.Equal(Base, IntegrationHealthChecker.BuildProbeUrl(Base, "   "));
    }

    // Pasting the whole probe URL into health_check_path is the obvious thing to do,
    // and concatenating it produced `…/api/https://…/api/status/` — a 404 that read as
    // "NetBox is unreachable" while NetBox was up.
    [Fact]
    public void An_absolute_path_is_used_as_is()
    {
        Assert.Equal("https://netbox.example.com/api/status/",
            IntegrationHealthChecker.BuildProbeUrl(Base, "https://netbox.example.com/api/status/"));
        Assert.Equal("http://other.example.com/health",
            IntegrationHealthChecker.BuildProbeUrl(Base, "http://other.example.com/health"));
    }

    // Only http(s) counts as absolute here. Anything else is a path with a colon in it,
    // and treating it as a URL would hand file:// or a custom scheme to the client.
    [Fact]
    public void A_non_http_scheme_is_treated_as_a_path()
    {
        Assert.StartsWith(Base, IntegrationHealthChecker.BuildProbeUrl(Base, "file:///etc/passwd"));
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        Assert.Equal("https://netbox.example.com/api/status/",
            IntegrationHealthChecker.BuildProbeUrl("https://netbox.example.com/api/", "/status/"));
    }
}

// `type` is a free-text label an admin may leave blank. The agent asks the obvious
// question — type=netbox — and a filter that only matched that field answered count: 0
// for an integration plainly named NetBox, which sent it looking somewhere else.
public class IntegrationTypeFilterTests
{
    private static readonly IQueryable<nashira_backend.Data.Models.Integration> Rows = new[]
    {
        new nashira_backend.Data.Models.Integration { Name = "NetBox", Slug = "netbox", Type = "" },
        new nashira_backend.Data.Models.Integration { Name = "ServiceNow", Slug = "servicenow", Type = "servicenow" },
        new nashira_backend.Data.Models.Integration { Name = "NetBox Lab", Slug = "netbox-lab", Type = "netbox" },
    }.AsQueryable();

    private static List<string> Filter(string? type) =>
        IntegrationQuery.FilterByType(Rows, type).Select(i => i.Name).ToList();

    [Fact]
    public void An_absent_filter_returns_everything()
    {
        Assert.Equal(3, Filter(null).Count);
        Assert.Equal(3, Filter("  ").Count);
    }

    [Fact]
    public void The_type_field_still_matches()
    {
        Assert.Equal(["ServiceNow"], Filter("servicenow"));
    }

    [Fact]
    public void The_slug_matches_too_so_an_empty_type_is_not_a_dead_end()
    {
        Assert.Equal(["NetBox", "NetBox Lab"], Filter("netbox"));
    }

    [Fact]
    public void The_filter_is_case_insensitive_on_the_way_in()
    {
        Assert.Equal(["NetBox", "NetBox Lab"], Filter("  NetBox "));
    }
}

// auth_config is stored as a JSON string, but `{"auth_config": {"method":"token"}}` is
// what an operator writes. Both spellings have to reach the database identically, or an
// integration's credentials look unrepairable through the API.
public class AuthConfigBindingTests
{
    private static readonly JsonSerializerOptions Options = new();

    [Fact]
    public void The_object_form_is_accepted_and_stored_as_text()
    {
        var dto = JsonSerializer.Deserialize<UpdateIntegration>(
            """{"auth_config":{"method":"token","prefix":"Token"}}""", Options);

        Assert.NotNull(dto!.AuthConfig);
        var parsed = IntegrationAuthConfig.TryParse(dto.AuthConfig);
        Assert.Equal(IntegrationAuthConfig.MethodToken, parsed!.ResolvedMethod());
        Assert.Equal("Token", parsed.Prefix);
    }

    [Fact]
    public void The_string_form_still_works()
    {
        var dto = JsonSerializer.Deserialize<UpdateIntegration>(
            """{"auth_config":"{\"method\":\"bearer\",\"token\":\"t\"}"}""", Options);

        Assert.Equal(IntegrationAuthConfig.MethodBearer,
            IntegrationAuthConfig.TryParse(dto!.AuthConfig)!.ResolvedMethod());
    }

    // An explicit null still means "clear it" on create and "leave it alone" on update;
    // the converter must not turn that into a JSON "null" literal.
    [Fact]
    public void An_explicit_null_stays_null()
    {
        var dto = JsonSerializer.Deserialize<UpdateIntegration>("""{"auth_config":null}""", Options);
        Assert.Null(dto!.AuthConfig);
    }

    [Fact]
    public void A_number_is_neither_and_is_refused()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<UpdateIntegration>("""{"auth_config":42}""", Options));
    }

    // Inline material is what makes a linked credential a no-op, so the flag that
    // reports it has to track exactly that and not "some auth_config exists".
    [Theory]
    [InlineData("""{"method":"token","prefix":"Token"}""", false)]
    [InlineData("""{"method":"none"}""", false)]
    [InlineData("""{"method":"token","token":"nb-token"}""", true)]
    [InlineData("""{"username":"svc","password":"p"}""", true)]
    [InlineData("""{"method":"oauth2_client_credentials","client_secret":"s"}""", true)]
    public void Inline_material_is_reported_separately_from_the_shape(string json, bool expected)
    {
        Assert.Equal(expected, IntegrationAuthConfig.TryParse(json)!.HasInlineMaterial());
    }

    // Referenced by JsonStringOrObjectConverter's own doc comment: the converter is
    // reusable, so pin its contract independently of the DTO that motivated it.
    [Fact]
    public void The_converter_round_trips_an_object_into_its_raw_text()
    {
        var dto = JsonSerializer.Deserialize<CreateIntegration>(
            """{"name":"n","base_url":"https://x.invalid","auth_config":{"a":1}}""", Options);
        Assert.Equal("""{"a":1}""", dto!.AuthConfig);
    }
}
