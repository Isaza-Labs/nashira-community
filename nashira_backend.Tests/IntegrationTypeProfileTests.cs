using nashira_backend.Services.Integration;

namespace nashira_backend.Tests;

// The knowledge that turns "403 authentication credentials were not provided" back into
// "the auth method is wrong".
//
// A NetBox integration was configured with `bearer`. The token was correct and stored
// correctly, but NetBox's Django REST Framework only recognises the scheme word
// `Token` — an unrecognised scheme reads to it as no credential at all, so it answered
// as though nothing had been sent. Nothing caught it: the health probe hit the base URL,
// which is NetBox's web UI and answers 200 to anonymous callers, so the integration sat
// there reporting healthy while every API call it made was refused.
public class IntegrationTypeProfileTests
{
    [Fact]
    public void Bearer_on_netbox_is_reported_as_a_mismatch()
    {
        var hint = IntegrationTypeProfile.AuthMismatch("netbox", "bearer");

        Assert.NotNull(hint);
        // The message has to name the scheme that works, not just say "wrong".
        Assert.Contains("Token", hint);
    }

    [Fact]
    public void The_type_is_matched_regardless_of_case_and_padding()
    {
        Assert.NotNull(IntegrationTypeProfile.AuthMismatch("  NetBox ", "bearer"));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("Token")]
    public void The_correct_method_is_not_a_mismatch(string method)
    {
        Assert.Null(IntegrationTypeProfile.AuthMismatch("netbox", method));
    }

    // `none` is somebody deliberately sending nothing, and an api key lives in its own
    // header rather than in Authorization. Neither is the mistake this catches, and
    // contradicting them would be this rule overreaching.
    [Theory]
    [InlineData("none")]
    [InlineData("api_key")]
    public void Deliberate_shapes_this_profile_has_no_opinion_about_are_left_alone(string method)
    {
        Assert.Null(IntegrationTypeProfile.AuthMismatch("netbox", method));
    }

    // An empty method already resolves to `token` when a token is present, so there is
    // nothing to correct — and correcting it would fight the auto-detection.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_undeclared_method_is_left_to_auto_detection(string? method)
    {
        Assert.Null(IntegrationTypeProfile.AuthMismatch("netbox", method));
    }

    // `Type` is free-form on purpose. An unknown one gets no profile and behaves
    // exactly as it did before this existed.
    [Theory]
    [InlineData("infoblox")]
    [InlineData("rest")]
    [InlineData(null)]
    public void An_unknown_type_has_no_opinion(string? type)
    {
        Assert.Null(IntegrationTypeProfile.AuthMismatch(type, "bearer"));
        Assert.Null(IntegrationTypeProfile.DefaultHealthPath(type));
    }

    [Fact]
    public void Netbox_probes_a_path_that_requires_authentication()
    {
        var path = IntegrationTypeProfile.DefaultHealthPath("netbox");

        Assert.NotNull(path);
        // The point of the fallback: not the root, which NetBox serves anonymously.
        Assert.NotEqual("/", path);
        Assert.StartsWith("/api/", path);
    }

    // ServiceNow's root is its login page and answers 200 to anonymous callers, so the
    // base URL as a probe is the NetBox trap again: healthy on the strength of a login
    // form, while every API call is refused. Worse here, because instances created
    // since 2026 enforce Basic Auth Restriction — correct credentials get a 401 and the
    // UI login still works, so nothing else points at the auth either.
    [Fact]
    public void Servicenow_probes_a_path_that_requires_authentication()
    {
        var path = IntegrationTypeProfile.DefaultHealthPath("servicenow");

        Assert.NotNull(path);
        Assert.StartsWith("/api/now/", path);
        // One row. The probe runs on a schedule and this table is not small.
        Assert.Contains("sysparm_limit=1", path);
    }

    [Fact]
    public void With_no_path_servicenow_probes_its_authenticated_endpoint()
    {
        var url = IntegrationHealthChecker.BuildProbeUrl(
            "https://dev1.service-now.com", null, "servicenow");

        Assert.Equal("https://dev1.service-now.com/api/now/table/sys_user?sysparm_limit=1", url);
    }

    // ServiceNow takes `Bearer` for an OAuth access token and Basic for a password.
    // Neither is a mistake, so this profile exists for the health path and must not
    // contradict a scheme that works — flagging `bearer` would send an operator who
    // configured OAuth correctly to go and break it.
    [Theory]
    [InlineData("bearer")]
    [InlineData("basic")]
    [InlineData("oauth2_client_credentials")]
    public void Servicenow_contradicts_no_scheme(string method)
    {
        Assert.Null(IntegrationTypeProfile.AuthMismatch("servicenow", method));
    }

    [Fact]
    public void An_explicit_health_path_still_wins_over_the_profile()
    {
        var url = IntegrationHealthChecker.BuildProbeUrl(
            "http://netbox.example", "/api/dcim/sites/", "netbox");

        Assert.Equal("http://netbox.example/api/dcim/sites/", url);
    }

    [Fact]
    public void With_no_path_a_known_type_probes_its_authenticated_endpoint()
    {
        var url = IntegrationHealthChecker.BuildProbeUrl("http://netbox.example", null, "netbox");

        Assert.Equal("http://netbox.example/api/status/", url);
    }

    // Unknown types keep the old behaviour exactly: probe the base URL.
    [Fact]
    public void With_no_path_and_no_profile_the_base_url_is_still_the_probe()
    {
        var url = IntegrationHealthChecker.BuildProbeUrl("http://thing.example", null, "rest");

        Assert.Equal("http://thing.example", url);
    }

    [Fact]
    public void A_refusal_after_sending_a_header_is_flagged_as_a_scheme_problem()
    {
        Assert.True(IntegrationTypeProfile.LooksLikeUnrecognisedScheme(
            403, sentAuthorization: true,
            """{"detail":"Authentication credentials were not provided."}"""));
    }

    // Without a header of our own, "not provided" is simply true, and saying anything
    // else would be noise on every anonymous call.
    [Fact]
    public void The_same_body_without_a_header_of_our_own_is_not_flagged()
    {
        Assert.False(IntegrationTypeProfile.LooksLikeUnrecognisedScheme(
            403, sentAuthorization: false,
            """{"detail":"Authentication credentials were not provided."}"""));
    }

    // A rejected secret is a different fault with a different fix. Flagging it as a
    // scheme problem would send somebody to the dropdown when the token is the issue.
    [Fact]
    public void An_invalid_token_is_not_mistaken_for_a_scheme_problem()
    {
        Assert.False(IntegrationTypeProfile.LooksLikeUnrecognisedScheme(
            403, sentAuthorization: true, """{"detail":"Invalid token."}"""));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(500)]
    public void Only_auth_refusals_are_considered(int status)
    {
        Assert.False(IntegrationTypeProfile.LooksLikeUnrecognisedScheme(
            status, sentAuthorization: true,
            """{"detail":"Authentication credentials were not provided."}"""));
    }
}
