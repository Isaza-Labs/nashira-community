using System.Text.Json;
using nashira_backend.Data.Models;
using nashira_backend.Services.Notifications;

namespace nashira_backend.Tests;

// Each service wants its own envelope for the same line of text. Getting one wrong
// is a 400 from the far end that reads like a broken webhook.
public class NotificationPayloadTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Slack_gets_a_bare_text_field()
    {
        var payload = Parse(NotificationDispatcher.BuildPayload(NotificationChannel.KindSlack, "hello"));
        Assert.Equal("hello", payload.GetProperty("text").GetString());
    }

    // The type/context pair is what makes Teams treat the body as a card rather
    // than rejecting it.
    [Fact]
    public void Teams_gets_the_message_card_envelope()
    {
        var payload = Parse(NotificationDispatcher.BuildPayload(NotificationChannel.KindTeams, "hello"));

        Assert.Equal("MessageCard", payload.GetProperty("@type").GetString());
        Assert.Equal("https://schema.org/extensions", payload.GetProperty("@context").GetString());
        Assert.Equal("hello", payload.GetProperty("text").GetString());
    }

    [Fact]
    public void A_generic_webhook_gets_the_slack_shape()
    {
        // Matching Slack means an endpoint written for one usually works for both.
        var payload = Parse(NotificationDispatcher.BuildPayload(NotificationChannel.KindWebhook, "hello"));
        Assert.Equal("hello", payload.GetProperty("text").GetString());
    }

    [Fact]
    public void An_unknown_kind_falls_back_rather_than_throwing()
    {
        var payload = Parse(NotificationDispatcher.BuildPayload("carrier_pigeon", "hello"));
        Assert.Equal("hello", payload.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("quotes \" and backslashes \\")]
    [InlineData("unicode ✓ and emoji 🚨")]
    [InlineData("")]
    public void Text_survives_serialisation_intact(string text)
    {
        // An alert whose body is a device name with a quote in it must not become a
        // malformed request.
        foreach (var kind in NotificationChannel.Kinds)
        {
            var payload = Parse(NotificationDispatcher.BuildPayload(kind, text));
            Assert.Equal(text, payload.GetProperty("text").GetString());
        }
    }
}
