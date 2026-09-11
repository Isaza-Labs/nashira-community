using System.Text;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Inventory;

namespace nashira_backend.Tests;

// InventoryTokenRef is the write-time gate that keeps raw NetBox tokens out of the
// inventory_sources table: a bare value must name a stored secret, and a full
// ${secret:...} reference must point at something that exists. These tests pin both
// rules plus the masking that keeps legacy literals from leaving the API.
public class InventoryTokenRefTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"inventory-token-ref-{Guid.NewGuid()}")
        .Options);

    private static Secret NamedSecret(string name) => new()
    {
        SecretId = Guid.NewGuid(),
        Name = name,
        EncryptedValue = Encoding.UTF8.GetBytes("cipher"),
    };

    [Fact]
    public async Task Empty_or_whitespace_clears_the_ref()
    {
        await using var db = Db();
        Assert.Null(await InventoryTokenRef.NormalizeAsync(db, "", default));
        Assert.Null(await InventoryTokenRef.NormalizeAsync(db, "   ", default));
    }

    [Fact]
    public async Task Bare_secret_name_becomes_a_full_reference()
    {
        await using var db = Db();
        db.Secrets.Add(NamedSecret("netbox-token"));
        await db.SaveChangesAsync();

        Assert.Equal(
            "${secret:secret:netbox-token:value}",
            await InventoryTokenRef.NormalizeAsync(db, "netbox-token", default));
    }

    [Fact]
    public async Task Full_secret_reference_passes_when_the_secret_exists()
    {
        await using var db = Db();
        db.Secrets.Add(NamedSecret("netbox-token"));
        await db.SaveChangesAsync();

        Assert.Equal(
            "${secret:secret:netbox-token:value}",
            await InventoryTokenRef.NormalizeAsync(db, "${secret:secret:netbox-token:value}", default));
    }

    [Fact]
    public async Task Reference_to_a_missing_secret_is_rejected()
    {
        await using var db = Db();
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            InventoryTokenRef.NormalizeAsync(db, "${secret:secret:nope:value}", default));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public async Task Credential_reference_resolves_against_credentials()
    {
        await using var db = Db();
        db.Credentials.Add(new Credential { CredentialId = Guid.NewGuid(), Name = "nb-cred" });
        await db.SaveChangesAsync();

        Assert.Equal(
            "${secret:credential:nb-cred:token}",
            await InventoryTokenRef.NormalizeAsync(db, "${secret:credential:nb-cred:token}", default));
        await Assert.ThrowsAsync<ValidationException>(() =>
            InventoryTokenRef.NormalizeAsync(db, "${secret:credential:missing:token}", default));
    }

    [Fact]
    public async Task Other_reference_sources_are_left_to_the_resolver()
    {
        await using var db = Db();
        Assert.Equal(
            "${secret:integration:netbox:token}",
            await InventoryTokenRef.NormalizeAsync(db, "${secret:integration:netbox:token}", default));
    }

    [Fact]
    public async Task A_raw_token_is_rejected_without_echoing_it()
    {
        await using var db = Db();
        const string token = "0123456789abcdef0123456789abcdef01234567";
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            InventoryTokenRef.NormalizeAsync(db, token, default));
        Assert.DoesNotContain(token, ex.Message);
    }

    [Fact]
    public async Task A_name_that_matches_no_secret_is_rejected()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<ValidationException>(() =>
            InventoryTokenRef.NormalizeAsync(db, "unknown-name", default));
    }

    [Fact]
    public void Mask_hides_literals_and_passes_references_through()
    {
        Assert.Null(InventoryTokenRef.Mask(null));
        Assert.Equal("", InventoryTokenRef.Mask(""));
        Assert.Equal(
            "${secret:secret:netbox-token:value}",
            InventoryTokenRef.Mask("${secret:secret:netbox-token:value}"));
        Assert.Equal("***", InventoryTokenRef.Mask("a-raw-token-value"));
    }
}
