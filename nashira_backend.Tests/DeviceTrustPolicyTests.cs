using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Credential;
using nashira_backend.Data.DTos.Device;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Security;
using nashira_backend.Services.Ssh;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;

namespace nashira_backend.Tests;

// Device SSH sends the device's linked credential to the device's address. Whoever
// can change the link, the address or the host-key pin decides where stored secret
// material goes, so those are Admin-only, like resolving a stored secret. An SSH step
// naming a credential by name is the same thing again and gets the same rule.
//
// The delegation (phase B): a credential an Admin scoped and made assignable may be
// linked, named and moved within its scope by an Operator; every path that sends a
// device credential refuses one whose scope does not cover the device's address.
public class DeviceTrustPolicyTests
{
    private const string AdminRequired = DeviceTrustPolicy.AdminRequiredCode;

    private static readonly HardeningTestUser Admin = new("admin");
    private static readonly HardeningTestUser Operator = new("operator");

    // ─── the pure policy ──────────────────────────────────────────────────

    [Fact]
    public void Admin_is_decided_exactly_like_stored_secret_access()
    {
        Assert.True(DeviceTrustPolicy.IsAdmin(new HardeningTestUser("admin")));
        Assert.False(DeviceTrustPolicy.IsAdmin(new HardeningTestUser("operator")));
        Assert.False(DeviceTrustPolicy.IsAdmin(new HardeningTestUser("Admin")));
        Assert.False(DeviceTrustPolicy.IsAdmin(new HardeningTestUser("admin", authenticated: false)));
        Assert.False(DeviceTrustPolicy.IsAdmin(new HardeningTestUser(null)));
    }

    [Fact]
    public void Credential_link_changes_are_admin_only_and_the_same_value_is_a_no_op()
    {
        var a = Unscoped();
        var b = Unscoped();

        Assert.Null(DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, null, null, "10.0.0.1"));
        Assert.Null(DeviceTrustPolicy.CredentialLinkRefusal(Operator, a.CredentialId, null, a, "10.0.0.1"));
        Assert.Null(DeviceTrustPolicy.CredentialLinkRefusal(Operator, a.CredentialId, a.CredentialId, a, "10.0.0.1"));
        Assert.NotNull(DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, a.CredentialId, a, "10.0.0.1"));
        Assert.NotNull(DeviceTrustPolicy.CredentialLinkRefusal(Operator, a.CredentialId, b.CredentialId, b, "10.0.0.1"));
        Assert.Null(DeviceTrustPolicy.CredentialLinkRefusal(Admin, a.CredentialId, b.CredentialId, b, "10.0.0.1"));
        Assert.Null(DeviceTrustPolicy.CredentialLinkRefusal(Admin, null, a.CredentialId, a, "10.0.0.1"));
    }

    [Fact]
    public void An_operator_may_link_an_assignable_scoped_credential_only_to_an_address_it_covers()
    {
        var ops = Scoped("10.0.0.0/24", assignable: true);

        Assert.Null(DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, ops.CredentialId, ops, "10.0.0.7"));
        Assert.Equal(DeviceTrustPolicy.CredentialLinkMessage,
            DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, ops.CredentialId, ops, "10.0.1.7"));
    }

    [Fact]
    public void A_missing_a_non_assignable_and_an_unscoped_credential_get_the_same_refusal()
    {
        var id = Guid.NewGuid();
        var notAssignable = Scoped("10.0.0.0/24", assignable: false, id: id);
        var unscopedButFlagged = Unscoped(id);
        unscopedButFlagged.OperatorAssignable = true; // not reachable through the API; still refused
        var inactive = Scoped("10.0.0.0/24", assignable: true, id: id);
        inactive.IsActive = false;

        var missing = DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, id, null, "10.0.0.7");
        Assert.Equal(DeviceTrustPolicy.CredentialLinkMessage, missing);
        Assert.Equal(missing, DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, id, notAssignable, "10.0.0.7"));
        Assert.Equal(missing, DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, id, unscopedButFlagged, "10.0.0.7"));
        Assert.Equal(missing, DeviceTrustPolicy.CredentialLinkRefusal(Operator, null, id, inactive, "10.0.0.7"));
    }

    [Fact]
    public void Host_key_pin_changes_are_admin_only_and_the_same_value_is_a_no_op()
    {
        Assert.Null(DeviceTrustPolicy.HostKeyPinRefusal(Operator, "SHA256:x", null));
        Assert.Null(DeviceTrustPolicy.HostKeyPinRefusal(Operator, "SHA256:x", "SHA256:x"));
        Assert.Null(DeviceTrustPolicy.HostKeyPinRefusal(Operator, null, ""));
        Assert.NotNull(DeviceTrustPolicy.HostKeyPinRefusal(Operator, null, "SHA256:x"));
        Assert.NotNull(DeviceTrustPolicy.HostKeyPinRefusal(Operator, "SHA256:x", "SHA256:y"));
        Assert.NotNull(DeviceTrustPolicy.HostKeyPinRefusal(Operator, "SHA256:x", ""));
        Assert.Null(DeviceTrustPolicy.HostKeyPinRefusal(Admin, "SHA256:x", "SHA256:y"));
    }

    [Fact]
    public void Address_changes_need_admin_only_while_an_unscoped_credential_is_linked()
    {
        var cred = Unscoped();
        var id = cred.CredentialId;

        Assert.Null(DeviceTrustPolicy.AddressRefusal(Operator, null, null, "10.0.0.1", "10.0.0.2"));
        Assert.Null(DeviceTrustPolicy.AddressRefusal(Operator, id, cred, "10.0.0.1", null));
        Assert.Null(DeviceTrustPolicy.AddressRefusal(Operator, id, cred, "10.0.0.1", " 10.0.0.1 "));
        Assert.NotNull(DeviceTrustPolicy.AddressRefusal(Operator, id, cred, "10.0.0.1", "10.0.0.2"));
        Assert.NotNull(DeviceTrustPolicy.AddressRefusal(Operator, id, null, "10.0.0.1", "10.0.0.2"));
        Assert.Null(DeviceTrustPolicy.AddressRefusal(Admin, id, cred, "10.0.0.1", "10.0.0.2"));
    }

    [Fact]
    public void An_operator_may_move_a_device_within_its_linked_credentials_scope()
    {
        var scoped = Scoped("10.0.0.0/24", assignable: false);
        var id = scoped.CredentialId;

        // R3 asks only for a scope, not for assignability: the Admin who set the scope
        // already said where this secret may go.
        Assert.Null(DeviceTrustPolicy.AddressRefusal(Operator, id, scoped, "10.0.0.1", "10.0.0.200"));
        Assert.Equal(DeviceTrustPolicy.AddressMessage,
            DeviceTrustPolicy.AddressRefusal(Operator, id, scoped, "10.0.0.1", "10.0.1.1"));
    }

    [Fact]
    public void A_step_credential_name_needs_admin_or_an_assignable_scoped_credential()
    {
        var ops = Scoped("10.0.0.0/24", assignable: true);
        var adminOnly = Scoped("10.0.0.0/24", assignable: false);

        Assert.Null(DeviceTrustPolicy.StepCredentialRefusal(Operator, null, null));
        Assert.Null(DeviceTrustPolicy.StepCredentialRefusal(Operator, "  ", null));
        Assert.NotNull(DeviceTrustPolicy.StepCredentialRefusal(Operator, "break-glass", Unscoped()));
        Assert.Null(DeviceTrustPolicy.StepCredentialRefusal(Admin, "break-glass", Unscoped()));
        Assert.Null(DeviceTrustPolicy.StepCredentialRefusal(Admin, "missing", null));
        Assert.Null(DeviceTrustPolicy.StepCredentialRefusal(Operator, "ops", ops));
        Assert.Equal(
            DeviceTrustPolicy.StepCredentialRefusal(Operator, "missing", null),
            DeviceTrustPolicy.StepCredentialRefusal(Operator, "lab", adminOnly));
    }

    [Fact]
    public void A_scope_mismatch_is_reported_for_scoped_credentials_only()
    {
        Assert.Null(DeviceTrustPolicy.ScopeMismatch(null, "10.0.0.1"));
        Assert.Null(DeviceTrustPolicy.ScopeMismatch(Unscoped(), "203.0.113.1"));
        Assert.Null(DeviceTrustPolicy.ScopeMismatch(Scoped("10.0.0.0/24", false), "10.0.0.1"));
        Assert.NotNull(DeviceTrustPolicy.ScopeMismatch(Scoped("10.0.0.0/24", false), "10.0.1.1"));
    }

    private static Credential Unscoped(Guid? id = null) => new()
    {
        CredentialId = id ?? Guid.NewGuid(), Name = "c", IsActive = true,
    };

    private static Credential Scoped(string hosts, bool assignable, Guid? id = null) => new()
    {
        CredentialId = id ?? Guid.NewGuid(), Name = "c", IsActive = true,
        AllowedHosts = CredentialScope.Normalize(hosts), OperatorAssignable = assignable,
    };

    // ─── DeviceController ────────────────────────────────────────────────

    [Fact]
    public async Task An_operator_cannot_create_a_device_with_a_credential_or_a_pin()
    {
        using var w = new World();

        var link = await Assert.ThrowsAsync<ForbiddenException>(() => w.Controller(Operator).Post(
            w.NewDevice(credentialId: w.CredentialId), default));
        var pin = await Assert.ThrowsAsync<ForbiddenException>(() => w.Controller(Operator).Post(
            w.NewDevice(fingerprint: "SHA256:attacker"), default));

        Assert.Equal(AdminRequired, link.Code);
        Assert.Equal(AdminRequired, pin.Code);
        using var check = w.Db();
        Assert.Equal(World.InitialDevices, await check.Devices.CountAsync());
    }

    [Fact]
    public async Task An_operator_can_still_create_a_device_without_a_credential_or_a_pin()
    {
        using var w = new World();

        await w.Controller(Operator).Post(w.NewDevice(), default);

        using var check = w.Db();
        Assert.Equal(World.InitialDevices + 1, await check.Devices.CountAsync());
    }

    [Fact]
    public async Task An_admin_can_create_a_device_with_a_credential_and_a_pin()
    {
        using var w = new World();

        await w.Controller(Admin).Post(w.NewDevice(credentialId: w.CredentialId, fingerprint: "SHA256:pin"), default);

        using var check = w.Db();
        var created = await check.Devices.SingleAsync(d => d.DeviceName == "new-box");
        Assert.Equal(w.CredentialId, created.CredentialId);
        Assert.Equal("SHA256:pin", created.ExpectedSshHostKeyFingerprint);
    }

    [Fact]
    public async Task An_operator_cannot_relink_a_device_and_nothing_changes()
    {
        using var w = new World();
        using var db = w.Db();
        var controller = new DeviceController(db, Operator);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => controller.Update(w.DeviceId, new UpdateDevice
        {
            DeviceName = "renamed",
            Site = "elsewhere",
            CredentialId = w.OtherCredentialId,
        }, default));

        Assert.Equal(AdminRequired, ex.Code);
        Assert.False(db.ChangeTracker.HasChanges());
        await w.AssertUntouchedAsync();
    }

    [Fact]
    public async Task An_operator_cannot_set_or_change_the_pin_and_nothing_changes()
    {
        using var w = new World();
        using var db = w.Db();
        var controller = new DeviceController(db, Operator);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => controller.Update(w.DeviceId, new UpdateDevice
        {
            Site = "elsewhere",
            ExpectedSshHostKeyFingerprint = "SHA256:attacker",
        }, default));

        Assert.Equal(AdminRequired, ex.Code);
        Assert.False(db.ChangeTracker.HasChanges());
        await w.AssertUntouchedAsync();
    }

    [Fact]
    public async Task An_operator_cannot_readdress_a_device_that_has_a_credential_and_nothing_changes()
    {
        using var w = new World();
        using var db = w.Db();
        var controller = new DeviceController(db, Operator);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => controller.Update(w.DeviceId, new UpdateDevice
        {
            DeviceName = "renamed",
            IpAddress = "203.0.113.66",
        }, default));

        Assert.Equal(AdminRequired, ex.Code);
        Assert.False(db.ChangeTracker.HasChanges());
        await w.AssertUntouchedAsync();
    }

    [Fact]
    public async Task An_operator_resending_the_same_link_pin_and_address_edits_other_fields()
    {
        using var w = new World();

        // What a form does: the whole object back, one field edited.
        await w.Controller(Operator).Update(w.DeviceId, new UpdateDevice
        {
            DeviceName = "core-sw",
            IpAddress = " 10.0.0.9 ",
            Site = "lab",
            CredentialId = w.CredentialId,
            ExpectedSshHostKeyFingerprint = World.Pin,
        }, default);

        using var check = w.Db();
        var row = await check.Devices.SingleAsync(d => d.DeviceId == w.DeviceId);
        Assert.Equal("lab", row.Site);
        Assert.Equal("10.0.0.9", row.IpAddress);
        Assert.Equal(w.CredentialId, row.CredentialId);
        Assert.Equal(World.Pin, row.ExpectedSshHostKeyFingerprint);
    }

    [Fact]
    public async Task An_operator_can_readdress_a_device_with_no_credential()
    {
        using var w = new World();

        await w.Controller(Operator).Update(w.BareDeviceId, new UpdateDevice { IpAddress = "10.0.0.77" }, default);

        using var check = w.Db();
        Assert.Equal("10.0.0.77", (await check.Devices.SingleAsync(d => d.DeviceId == w.BareDeviceId)).IpAddress);
    }

    [Fact]
    public async Task An_admin_can_relink_repin_and_readdress()
    {
        using var w = new World();

        await w.Controller(Admin).Update(w.DeviceId, new UpdateDevice
        {
            IpAddress = "10.0.0.10",
            CredentialId = w.OtherCredentialId,
            ExpectedSshHostKeyFingerprint = "SHA256:new",
        }, default);

        using var check = w.Db();
        var row = await check.Devices.SingleAsync(d => d.DeviceId == w.DeviceId);
        Assert.Equal("10.0.0.10", row.IpAddress);
        Assert.Equal(w.OtherCredentialId, row.CredentialId);
        Assert.Equal("SHA256:new", row.ExpectedSshHostKeyFingerprint);
    }

    // ─── update_device tool ──────────────────────────────────────────────

    [Fact]
    public async Task The_update_device_tool_refuses_an_operator_readdressing_a_credentialed_device()
    {
        using var w = new World();
        using var db = w.Db();
        var tool = new UpdateDeviceHandler(db, Operator);

        var result = await tool.ExecuteAsync(Args("""
            {"device_name":"core-sw","new_name":"renamed","ip_address":"203.0.113.66","site":"x"}
            """), default);

        var error = result.GetProperty("error").GetString();
        Assert.Contains("Admin", error);
        Assert.False(db.ChangeTracker.HasChanges());
        await w.AssertUntouchedAsync();
    }

    [Fact]
    public async Task The_update_device_tool_lets_an_operator_readdress_a_device_with_no_credential()
    {
        using var w = new World();
        using var db = w.Db();

        var result = await new UpdateDeviceHandler(db, Operator).ExecuteAsync(
            Args("""{"device_name":"bare-box","ip_address":"10.0.0.78"}"""), default);

        Assert.True(result.GetProperty("updated").GetBoolean());
        using var check = w.Db();
        Assert.Equal("10.0.0.78", (await check.Devices.SingleAsync(d => d.DeviceId == w.BareDeviceId)).IpAddress);
    }

    [Fact]
    public async Task The_update_device_tool_lets_an_admin_readdress_and_an_operator_resend_the_same_address()
    {
        using var w = new World();
        using var db = w.Db();

        var same = await new UpdateDeviceHandler(db, Operator).ExecuteAsync(
            Args("""{"device_name":"core-sw","ip_address":"10.0.0.9","site":"lab"}"""), default);
        Assert.True(same.GetProperty("updated").GetBoolean());

        var moved = await new UpdateDeviceHandler(db, Admin).ExecuteAsync(
            Args("""{"device_name":"core-sw","ip_address":"10.0.0.11"}"""), default);
        Assert.True(moved.GetProperty("updated").GetBoolean());

        using var check = w.Db();
        var row = await check.Devices.SingleAsync(d => d.DeviceId == w.DeviceId);
        Assert.Equal("10.0.0.11", row.IpAddress);
        Assert.Equal("lab", row.Site);
    }

    // ─── ssh step `credential` override ──────────────────────────────────

    [Fact]
    public async Task An_operator_step_naming_a_credential_is_refused_before_anything_runs()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Operator);

        var existing = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"break-glass"}"""), default);
        var missing = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"no-such-name"}"""), default);

        Assert.False(existing.Success);
        Assert.Equal(SecretResolver.AccessDeniedCode, existing.ErrorCode);
        // Same answer whether the name exists or not.
        Assert.Equal(existing.ErrorCode, missing.ErrorCode);
        Assert.Equal(existing.Error, missing.Error);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task An_admin_step_naming_a_credential_uses_it()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Admin);

        var result = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"break-glass"}"""), default);

        Assert.True(result.Success);
        Assert.Equal(1, runner.Calls);
        Assert.Equal("root", runner.Last!.Username);
        Assert.Equal("break-glass-pw", runner.Last!.Password);
    }

    [Fact]
    public async Task An_operator_step_still_uses_the_devices_own_linked_credential()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Operator);

        var result = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version"}"""), default);

        Assert.True(result.Success);
        Assert.Equal(1, runner.Calls);
        Assert.Equal("netops", runner.Last!.Username);
        Assert.Equal("device-pw", runner.Last!.Password);
        Assert.Equal("10.0.0.9", runner.Last!.Host);
    }

    // ─── delegation: DeviceController (R1/R3 with scope) ─────────────────

    [Fact]
    public async Task An_operator_can_create_a_device_with_an_assignable_credential_inside_its_scope()
    {
        using var w = new World();

        await w.Controller(Operator).Post(w.NewDevice(credentialId: w.OpsId, ip: "10.0.0.33"), default);

        using var check = w.Db();
        Assert.Equal(w.OpsId, (await check.Devices.SingleAsync(d => d.DeviceName == "new-box")).CredentialId);
    }

    [Fact]
    public async Task An_operator_gets_one_refusal_for_out_of_scope_non_assignable_and_missing_credentials()
    {
        using var w = new World();

        var outOfScope = await Assert.ThrowsAsync<ForbiddenException>(() => w.Controller(Operator).Post(
            w.NewDevice(credentialId: w.OpsId, ip: "203.0.113.10"), default));
        var notAssignable = await Assert.ThrowsAsync<ForbiddenException>(() => w.Controller(Operator).Post(
            w.NewDevice(credentialId: w.LabAdminId, ip: "10.0.0.33"), default));
        var missing = await Assert.ThrowsAsync<ForbiddenException>(() => w.Controller(Operator).Post(
            w.NewDevice(credentialId: Guid.NewGuid(), ip: "10.0.0.33"), default));

        Assert.Equal(AdminRequired, missing.Code);
        Assert.Equal(missing.Code, notAssignable.Code);
        Assert.Equal(missing.Message, notAssignable.Message);
        Assert.Equal(missing.Code, outOfScope.Code);
        using var check = w.Db();
        Assert.Equal(World.InitialDevices, await check.Devices.CountAsync());
    }

    [Fact]
    public async Task An_admin_cannot_link_a_scoped_credential_to_an_address_outside_it()
    {
        using var w = new World();

        await Assert.ThrowsAsync<ValidationException>(() => w.Controller(Admin).Post(
            w.NewDevice(credentialId: w.OpsId, ip: "203.0.113.10"), default));
        await Assert.ThrowsAsync<ValidationException>(() => w.Controller(Admin).Update(
            w.DeviceId, new UpdateDevice { CredentialId = w.DmzId }, default));
        // An unknown id is still a validation error for an Admin, as before.
        await Assert.ThrowsAsync<ValidationException>(() => w.Controller(Admin).Post(
            w.NewDevice(credentialId: Guid.NewGuid()), default));

        await w.AssertUntouchedAsync();
    }

    [Fact]
    public async Task An_operator_can_relink_to_an_assignable_credential_and_move_within_its_scope_in_one_request()
    {
        using var w = new World();

        // The final address decides: 10.0.0.30 is inside ops-ssh's scope.
        await w.Controller(Operator).Update(w.DeviceId, new UpdateDevice
        {
            CredentialId = w.OpsId,
            IpAddress = "10.0.0.30",
        }, default);

        using var check = w.Db();
        var row = await check.Devices.SingleAsync(d => d.DeviceId == w.DeviceId);
        Assert.Equal(w.OpsId, row.CredentialId);
        Assert.Equal("10.0.0.30", row.IpAddress);
    }

    [Fact]
    public async Task An_operator_cannot_relink_when_the_final_address_is_outside_the_scope()
    {
        using var w = new World();
        using var db = w.Db();

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => new DeviceController(db, Operator).Update(
            w.DeviceId, new UpdateDevice { CredentialId = w.OpsId, IpAddress = "203.0.113.5" }, default));

        Assert.Equal(AdminRequired, ex.Code);
        Assert.False(db.ChangeTracker.HasChanges());
        await w.AssertUntouchedAsync();
    }

    [Fact]
    public async Task An_operator_can_readdress_a_scoped_device_only_within_the_scope()
    {
        using var w = new World();

        await w.Controller(Operator).Update(w.ScopedDeviceId, new UpdateDevice { IpAddress = "10.0.0.21" }, default);
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => w.Controller(Operator).Update(
            w.ScopedDeviceId, new UpdateDevice { IpAddress = "192.0.2.5" }, default));

        Assert.Equal(AdminRequired, ex.Code);
        using var check = w.Db();
        Assert.Equal("10.0.0.21", (await check.Devices.SingleAsync(d => d.DeviceId == w.ScopedDeviceId)).IpAddress);
    }

    [Fact]
    public async Task An_admin_cannot_readdress_a_scoped_device_outside_the_scope()
    {
        using var w = new World();

        await Assert.ThrowsAsync<ValidationException>(() => w.Controller(Admin).Update(
            w.ScopedDeviceId, new UpdateDevice { IpAddress = "192.0.2.5" }, default));

        using var check = w.Db();
        Assert.Equal("10.0.0.20", (await check.Devices.SingleAsync(d => d.DeviceId == w.ScopedDeviceId)).IpAddress);
    }

    [Fact]
    public async Task A_device_already_outside_its_credentials_scope_can_still_be_edited_otherwise()
    {
        using var w = new World();

        // moved-box sits outside ops-ssh's scope (an Admin narrowed it, or a sync moved
        // the box). Nothing about the link or address changes, so nothing is refused.
        await w.Controller(Operator).Update(w.MovedDeviceId, new UpdateDevice
        {
            Site = "lab",
            IpAddress = "203.0.113.7",
            CredentialId = w.OpsId,
        }, default);

        using var check = w.Db();
        Assert.Equal("lab", (await check.Devices.SingleAsync(d => d.DeviceId == w.MovedDeviceId)).Site);
    }

    [Fact]
    public async Task The_update_device_tool_lets_an_operator_move_a_scoped_device_only_within_the_scope()
    {
        using var w = new World();
        using var db = w.Db();
        var tool = new UpdateDeviceHandler(db, Operator);

        var inside = await tool.ExecuteAsync(Args("""{"device_name":"scoped-box","ip_address":"10.0.0.22"}"""), default);
        Assert.True(inside.GetProperty("updated").GetBoolean());

        var outside = await tool.ExecuteAsync(Args("""{"device_name":"scoped-box","ip_address":"192.0.2.9"}"""), default);
        Assert.Contains("Admin", outside.GetProperty("error").GetString());

        var adminOutside = await new UpdateDeviceHandler(db, Admin).ExecuteAsync(
            Args("""{"device_name":"scoped-box","ip_address":"192.0.2.9"}"""), default);
        Assert.Contains("allowed hosts", adminOutside.GetProperty("error").GetString());

        using var check = w.Db();
        Assert.Equal("10.0.0.22", (await check.Devices.SingleAsync(d => d.DeviceId == w.ScopedDeviceId)).IpAddress);
    }

    // ─── delegation: ssh step `credential` (R4) and send-time scope ───────

    [Fact]
    public async Task An_operator_step_may_name_an_assignable_scoped_credential_in_scope()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Operator);

        var result = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"ops-ssh"}"""), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("ops", runner.Last!.Username);
        Assert.Equal("ops-pw", runner.Last!.Password);
    }

    [Fact]
    public async Task An_operator_step_naming_a_scoped_but_not_assignable_credential_is_refused_like_a_missing_one()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Operator);

        var notAssignable = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"lab-admin"}"""), default);
        var missing = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"no-such-name"}"""), default);

        Assert.Equal(SecretResolver.AccessDeniedCode, notAssignable.ErrorCode);
        Assert.Equal(missing.ErrorCode, notAssignable.ErrorCode);
        Assert.Equal(missing.Error, notAssignable.Error);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task An_admin_step_naming_a_missing_credential_is_still_not_found()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Admin);

        var result = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"no-such-name"}"""), default);

        Assert.Equal("not_found", result.ErrorCode);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task A_named_credential_out_of_scope_is_refused_at_send_time_for_everyone(string role)
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(new HardeningTestUser(role));

        var result = await handler.ExecuteAsync(
            Step("""{"device":"core-sw","command":"show version","credential":"dmz-ssh"}"""), default);

        Assert.False(result.Success);
        Assert.Equal(CredentialScope.OutOfScopeCode, result.ErrorCode);
        Assert.Contains("dmz-ssh", result.Error);
        Assert.Contains("core-sw", result.Error);
        Assert.DoesNotContain("192.0.2", result.Error);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, w.Protector.Decrypts);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task A_devices_own_credential_out_of_scope_is_refused_at_send_time(string role)
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(new HardeningTestUser(role));

        var result = await handler.ExecuteAsync(
            Step("""{"device":"moved-box","command":"show version"}"""), default);

        Assert.Equal(CredentialScope.OutOfScopeCode, result.ErrorCode);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, w.Protector.Decrypts);
    }

    [Fact]
    public async Task A_devices_own_credential_in_scope_is_sent()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Operator);

        var result = await handler.ExecuteAsync(
            Step("""{"device":"scoped-box","command":"show version"}"""), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("10.0.0.20", runner.Last!.Host);
        Assert.Equal("ops-pw", runner.Last!.Password);
    }

    [Fact]
    public async Task Payload_material_needs_no_credential_row_and_no_scope()
    {
        using var w = new World();
        var (handler, runner) = w.Ssh(Operator);

        var result = await handler.ExecuteAsync(Step("""
            {"device":"moved-box","command":"show version","username":"local",
             "password":"${secret:secret:box-pw:value}"}
            """), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("local", runner.Last!.Username);
        Assert.Equal("payload-secret", runner.Last!.Password);
    }

    // ─── send-time scope: device_connect ─────────────────────────────────

    [Fact]
    public async Task Device_connect_refuses_a_credential_out_of_scope_before_decrypting()
    {
        using var w = new World();
        var (tool, runner) = w.DeviceConnect(Admin);

        var result = await tool.ExecuteAsync(
            Args("""{"device":"moved-box","commands":["show version"]}"""), default);

        Assert.Equal(CredentialScope.OutOfScopeCode, result.GetProperty("error_kind").GetString());
        Assert.Contains("moved-box", result.GetProperty("error").GetString());
        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, w.Protector.Decrypts);
    }

    [Fact]
    public async Task Device_connect_sends_a_credential_in_scope()
    {
        using var w = new World();
        var (tool, runner) = w.DeviceConnect(Operator);

        var result = await tool.ExecuteAsync(
            Args("""{"device":"scoped-box","commands":["show version"]}"""), default);

        Assert.False(result.TryGetProperty("error", out _));
        Assert.Equal(1, runner.Calls);
        Assert.Equal("10.0.0.20", runner.Last!.Host);
    }

    // ─── send-time scope: ansible_playbook ───────────────────────────────

    [Fact]
    public async Task Ansible_fails_the_step_for_a_credential_out_of_scope_instead_of_running_without_it()
    {
        using var w = new World();
        var handler = w.Ansible();

        var (_, failure) = await handler.ResolveTargetsAsync(
            Args("""{"hosts":["scoped-box","moved-box"]}"""), "draft", default);

        Assert.NotNull(failure);
        Assert.Equal(CredentialScope.OutOfScopeCode, failure!.ErrorCode);
        Assert.Contains("moved-box", failure.Error);
        // scoped-box came first and was decrypted; moved-box never was.
        Assert.Equal(1, w.Protector.Decrypts);

        // On a Linux worker the whole step stops there, before ansible-playbook starts.
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux))
        {
            var step = await handler.ExecuteAsync(new SnippetRequest
            {
                NodeId = "n1", WorkflowId = Guid.Empty, SnippetId = Guid.NewGuid(),
                SnippetType = "ansible_playbook", Code = "---",
                Input = Args("""{"hosts":["moved-box"]}"""), Environment = "draft",
            }, default);
            Assert.Equal(CredentialScope.OutOfScopeCode, step.ErrorCode);
        }
    }

    [Fact]
    public async Task Ansible_uses_a_credential_in_scope()
    {
        using var w = new World();

        var (targets, failure) = await w.Ansible().ResolveTargetsAsync(
            Args("""{"hosts":["scoped-box"]}"""), "draft", default);

        Assert.Null(failure);
        var t = Assert.Single(targets);
        Assert.Equal("10.0.0.20", t.Host);
        Assert.Equal("ops-pw", t.Password);
    }

    [Fact]
    public void Ansible_never_shares_a_device_password_with_a_host_that_has_none()
    {
        var device = new AnsiblePlaybookSnippetHandler.Target("10.0.0.20", "ops", "ops-pw");
        var literal = new AnsiblePlaybookSnippetHandler.Target("198.51.100.1", null, null);

        Assert.Equal("ops-pw", AnsiblePlaybookSnippetHandler.SharedPassword([device, device with { Host = "10.0.0.21" }]));
        Assert.Null(AnsiblePlaybookSnippetHandler.SharedPassword([device, literal]));
        Assert.Null(AnsiblePlaybookSnippetHandler.SharedPassword([literal, device]));
    }

    // ─── R5: an Ansible step hands the device password to the playbook ───

    private static Credential Pw(string? username = "u", string auth = Credential.AuthMethodPassword) => new()
    {
        CredentialId = Guid.NewGuid(), Name = "c", IsActive = true, AuthMethod = auth, Username = username,
    };

    [Fact]
    public void Playbook_credential_rule_applies_only_to_a_password_the_play_would_receive()
    {
        var password = Pw();
        var assignable = Pw();
        assignable.AllowedHosts = CredentialScope.Normalize("10.0.0.0/24");
        assignable.OperatorAssignable = true;
        var key = Pw(auth: Credential.AuthMethodKey);
        var noUser = Pw(username: " ");
        var inactive = Pw();
        inactive.IsActive = false;

        Assert.Contains("'box'", DeviceTrustPolicy.PlaybookCredentialRefusal(Operator, password, "box"));
        // No delegation: assignable and scoped is still refused.
        Assert.NotNull(DeviceTrustPolicy.PlaybookCredentialRefusal(Operator, assignable, "box"));
        Assert.Null(DeviceTrustPolicy.PlaybookCredentialRefusal(Operator, null, "box"));
        Assert.Null(DeviceTrustPolicy.PlaybookCredentialRefusal(Operator, key, "box"));
        Assert.Null(DeviceTrustPolicy.PlaybookCredentialRefusal(Operator, noUser, "box"));
        Assert.Null(DeviceTrustPolicy.PlaybookCredentialRefusal(Operator, inactive, "box"));
        Assert.Null(DeviceTrustPolicy.PlaybookCredentialRefusal(Admin, password, "box"));
        Assert.Null(DeviceTrustPolicy.PlaybookCredentialRefusal(Admin, assignable, "box"));
    }

    [Fact]
    public async Task Ansible_refuses_an_operator_a_device_password_before_decrypting_it()
    {
        using var w = new World();

        var (_, failure) = await w.Ansible(Operator).ResolveTargetsAsync(
            Args("""{"hosts":["core-sw"]}"""), "draft", default);

        Assert.NotNull(failure);
        Assert.Equal(SecretResolver.AccessDeniedCode, failure!.ErrorCode);
        Assert.Contains("core-sw", failure.Error);
        Assert.DoesNotContain("device-default", failure.Error);
        Assert.Equal(0, w.Protector.Decrypts);
    }

    [Fact]
    public async Task Ansible_refuses_an_operator_even_an_assignable_scoped_credential()
    {
        using var w = new World();

        // bare-box first, so the refusal comes after a target was already accepted.
        var (_, failure) = await w.Ansible(Operator).ResolveTargetsAsync(
            Args("""{"hosts":["bare-box","scoped-box"]}"""), "draft", default);

        Assert.Equal(SecretResolver.AccessDeniedCode, failure!.ErrorCode);
        Assert.Contains("scoped-box", failure.Error);
        Assert.DoesNotContain("ops-ssh", failure.Error);
        Assert.Equal(0, w.Protector.Decrypts);
    }

    [Fact]
    public async Task Ansible_checks_admin_before_the_scope()
    {
        using var w = new World();

        // moved-box is outside its credential's scope: an Operator is told only that it
        // needs an Admin, an Admin that it is out of scope.
        var (_, asOperator) = await w.Ansible(Operator).ResolveTargetsAsync(
            Args("""{"hosts":["moved-box"]}"""), "draft", default);
        var (_, asAdmin) = await w.Ansible(Admin).ResolveTargetsAsync(
            Args("""{"hosts":["moved-box"]}"""), "draft", default);

        Assert.Equal(SecretResolver.AccessDeniedCode, asOperator!.ErrorCode);
        Assert.DoesNotContain("allowed", asOperator.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ops-ssh", asOperator.Error);
        Assert.Equal(CredentialScope.OutOfScopeCode, asAdmin!.ErrorCode);
        Assert.Equal(0, w.Protector.Decrypts);
    }

    [Fact]
    public async Task Ansible_lets_an_operator_target_devices_without_a_usable_credential_and_literal_hosts()
    {
        using var w = new World();
        using (var db = w.Db())
        {
            var keyId = Guid.NewGuid();
            var noUserId = Guid.NewGuid();
            db.Credentials.AddRange(
                new Credential
                {
                    CredentialId = keyId, Name = "key-cred", AuthMethod = Credential.AuthMethodKey,
                    Username = "k", IsActive = true,
                },
                new Credential
                {
                    CredentialId = noUserId, Name = "no-user", AuthMethod = Credential.AuthMethodPassword,
                    EncryptedPassword = w.Protector.Encrypt("nouser-pw"), IsActive = true,
                });
            db.Devices.AddRange(
                new Device
                {
                    DeviceId = Guid.NewGuid(), DeviceName = "key-box", IpAddress = "10.0.0.60",
                    CredentialId = keyId, AllowDraft = true, IsActive = true,
                },
                new Device
                {
                    DeviceId = Guid.NewGuid(), DeviceName = "nouser-box", IpAddress = "10.0.0.61",
                    CredentialId = noUserId, AllowDraft = true, IsActive = true,
                });
            await db.SaveChangesAsync();
        }

        var (targets, failure) = await w.Ansible(Operator).ResolveTargetsAsync(
            Args("""{"hosts":["bare-box","key-box","nouser-box"],"host":"198.51.100.1"}"""), "draft", default);

        Assert.Null(failure);
        Assert.Equal(
            new[] { "10.0.0.50", "10.0.0.60", "10.0.0.61", "198.51.100.1" },
            targets.Select(t => t.Host).ToArray());
        Assert.All(targets, t => Assert.Null(t.Password));
        Assert.Equal(0, w.Protector.Decrypts);

        var (literalOnly, literalFailure) = await w.Ansible(Operator).ResolveTargetsAsync(
            Args("""{"host":"198.51.100.1"}"""), "draft", default);
        Assert.Null(literalFailure);
        Assert.Equal("198.51.100.1", Assert.Single(literalOnly).Host);
    }

    [Fact]
    public async Task Ansible_hands_an_admin_the_device_password()
    {
        using var w = new World();

        var (targets, failure) = await w.Ansible(Admin).ResolveTargetsAsync(
            Args("""{"hosts":["core-sw","scoped-box"]}"""), "draft", default);

        Assert.Null(failure);
        Assert.Equal(new[] { "device-pw", "ops-pw" }, targets.Select(t => t.Password).ToArray());
        Assert.Equal(2, w.Protector.Decrypts);
    }

    [Fact]
    public void Ansible_redacts_every_password_of_the_run_from_its_output()
    {
        const string output = "ok: pw=device-pw other=ops-pw-long again device-pw short=abc";

        Assert.Equal(
            "ok: pw=[redacted] other=[redacted] again [redacted] short=abc",
            AnsiblePlaybookSnippetHandler.RedactPasswords(
                output, ["device-pw", "ops-pw-long", "device-pw", "abc", null, ""]));
        // Longest first: a password that contains another is removed whole.
        Assert.Equal("x [redacted] y",
            AnsiblePlaybookSnippetHandler.RedactPasswords("x ops-pw-long y", ["ops-pw", "ops-pw-long"]));
        Assert.Equal(output, AnsiblePlaybookSnippetHandler.RedactPasswords(output, []));
        Assert.Equal(output, AnsiblePlaybookSnippetHandler.RedactPasswords(output, [null]));
        Assert.Equal(string.Empty, AnsiblePlaybookSnippetHandler.RedactPasswords(null, ["device-pw"]));
    }

    // ─── CredentialController: scope fields ──────────────────────────────

    [Fact]
    public async Task Operator_assignable_without_a_scope_is_refused_on_create_and_update()
    {
        using var w = new World();

        await Assert.ThrowsAsync<ValidationException>(() => w.Credentials().Post(new CreateCredential
        {
            Name = "flagged", OperatorAssignable = true,
        }, default));
        await Assert.ThrowsAsync<ValidationException>(() => w.Credentials().Post(new CreateCredential
        {
            Name = "flagged", OperatorAssignable = true, AllowedHosts = "  ",
        }, default));
        // Clearing the scope of an assignable credential without un-flagging it.
        await Assert.ThrowsAsync<ValidationException>(() => w.Credentials().Update(w.OpsId, new UpdateCredential
        {
            AllowedHosts = "",
        }, default));
        // Flagging a credential that has no scope.
        await Assert.ThrowsAsync<ValidationException>(() => w.Credentials().Update(w.CredentialId, new UpdateCredential
        {
            OperatorAssignable = true,
        }, default));

        using var check = w.Db();
        Assert.Equal("10.0.0.0/24", (await check.Credentials.SingleAsync(c => c.CredentialId == w.OpsId)).AllowedHosts);
        Assert.False(await check.Credentials.AnyAsync(c => c.Name == "flagged"));
    }

    [Fact]
    public async Task A_bad_scope_entry_is_a_validation_error_naming_it()
    {
        using var w = new World();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => w.Credentials().Post(new CreateCredential
        {
            Name = "bad", AllowedHosts = "10.0.0.0/24, 0.0.0.0/0",
        }, default));

        Assert.Contains("0.0.0.0/0", ex.Message);
    }

    [Fact]
    public async Task The_scope_is_stored_canonical_returned_and_audited()
    {
        using var w = new World();
        var audit = new CapturingAudit();

        var created = await w.Credentials(audit).Post(new CreateCredential
        {
            Name = "new-ops", Username = "u", Password = "p",
            AllowedHosts = "10.0.5.9/24, Edge-1.Example", OperatorAssignable = true,
        }, default);

        var body = (CredentialResponse)((Microsoft.AspNetCore.Mvc.ObjectResult)created.Result!).Value!;
        Assert.Equal("10.0.5.0/24\nedge-1.example", body.AllowedHosts);
        Assert.True(body.OperatorAssignable);
        var after = JsonSerializer.SerializeToElement(audit.After);
        Assert.Equal("10.0.5.0/24\nedge-1.example", after.GetProperty("allowed_hosts").GetString());
        Assert.True(after.GetProperty("operator_assignable").GetBoolean());
    }

    [Fact]
    public async Task A_scope_can_be_cleared_together_with_the_flag_and_narrowed_past_linked_devices()
    {
        using var w = new World();

        // scoped-box (10.0.0.20) is linked to ops-ssh; narrowing past it is not blocked —
        // the send-time check refuses it from now on.
        var narrowed = await w.Credentials().Update(w.OpsId, new UpdateCredential { AllowedHosts = "10.0.0.99" }, default);
        Assert.Equal("10.0.0.99", narrowed.Value!.AllowedHosts);

        var cleared = await w.Credentials().Update(w.OpsId, new UpdateCredential
        {
            AllowedHosts = "", OperatorAssignable = false,
        }, default);
        Assert.Null(cleared.Value!.AllowedHosts);
        Assert.False(cleared.Value!.OperatorAssignable);

        // Not sending the field keeps it.
        await w.Credentials().Update(w.LabAdminId, new UpdateCredential { Name = "lab-admin-2" }, default);
        using var check = w.Db();
        Assert.Equal("10.0.0.0/24", (await check.Credentials.SingleAsync(c => c.CredentialId == w.LabAdminId)).AllowedHosts);
    }

    // ─── the assignable endpoint ─────────────────────────────────────────

    [Fact]
    public async Task The_assignable_list_has_only_active_assignable_scoped_credentials_and_no_secret_fields()
    {
        using var w = new World();

        var result = await new AssignableCredentialController(w.Db()).Assignable(default);

        var rows = result.Value!;
        Assert.Equal(new[] { "dmz-ssh", "ops-ssh" }, rows.Select(r => r.Name).ToArray());
        var json = JsonSerializer.SerializeToElement(rows);
        foreach (var row in json.EnumerateArray())
        {
            Assert.Equal(
                new[] { "allowed_hosts", "auth_method", "credential_id", "name" },
                row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void Only_the_assignable_endpoint_is_open_to_operators()
    {
        static IEnumerable<string> Policies(System.Reflection.MemberInfo m) =>
            m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(a => a.Policy ?? "");

        // The whole credential CRUD stays Admin, with no action loosening it.
        Assert.Equal(new[] { "Admin" }, Policies(typeof(CredentialController)).ToArray());
        foreach (var action in typeof(CredentialController).GetMethods(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                     | System.Reflection.BindingFlags.DeclaredOnly))
        {
            Assert.Empty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
            Assert.All(Policies(action), p => Assert.Equal("Admin", p));
        }

        // The Operator controller exposes exactly one action.
        Assert.Equal(new[] { "Operator" }, Policies(typeof(AssignableCredentialController)).ToArray());
        var actions = typeof(AssignableCredentialController).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.Equal(new[] { nameof(AssignableCredentialController.Assignable) }, actions.Select(a => a.Name).ToArray());
    }

    // ─── fixtures ────────────────────────────────────────────────────────

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static SnippetRequest Step(string json) => new()
    {
        NodeId = "n1",
        WorkflowId = Guid.Empty,
        SnippetId = Guid.NewGuid(),
        SnippetType = "ssh",
        Input = JsonDocument.Parse(json).RootElement.Clone(),
        Environment = "draft",
    };

    private sealed class World : IDisposable
    {
        public const string Pin = "SHA256:original";
        public const int InitialDevices = 4;

        private readonly string _name = $"device-trust-{Guid.NewGuid()}";
        private readonly CountingProtector _protector = new();
        private readonly List<AppDbContext> _opened = [];

        public CountingProtector Protector => _protector;
        public Guid CredentialId { get; } = Guid.NewGuid();
        public Guid OtherCredentialId { get; } = Guid.NewGuid();
        // Scoped to 10.0.0.0/24 and assignable by Operators.
        public Guid OpsId { get; } = Guid.NewGuid();
        // Scoped to 10.0.0.0/24, Admin-only.
        public Guid LabAdminId { get; } = Guid.NewGuid();
        // Scoped to 192.0.2.0/24 and assignable.
        public Guid DmzId { get; } = Guid.NewGuid();
        public Guid DeviceId { get; } = Guid.NewGuid();
        public Guid BareDeviceId { get; } = Guid.NewGuid();
        // 10.0.0.20, linked to ops-ssh: inside its scope.
        public Guid ScopedDeviceId { get; } = Guid.NewGuid();
        // 203.0.113.7, linked to ops-ssh: outside its scope (narrowed, or moved by a sync).
        public Guid MovedDeviceId { get; } = Guid.NewGuid();

        public World()
        {
            using var db = Db();
            db.Credentials.AddRange(
                new Credential
                {
                    CredentialId = CredentialId, Name = "device-default", AuthMethod = Credential.AuthMethodPassword,
                    Username = "netops", EncryptedPassword = _protector.Encrypt("device-pw"), IsActive = true,
                },
                new Credential
                {
                    CredentialId = OtherCredentialId, Name = "break-glass", AuthMethod = Credential.AuthMethodPassword,
                    Username = "root", EncryptedPassword = _protector.Encrypt("break-glass-pw"), IsActive = true,
                },
                new Credential
                {
                    CredentialId = OpsId, Name = "ops-ssh", AuthMethod = Credential.AuthMethodPassword,
                    Username = "ops", EncryptedPassword = _protector.Encrypt("ops-pw"), IsActive = true,
                    AllowedHosts = "10.0.0.0/24", OperatorAssignable = true,
                },
                new Credential
                {
                    CredentialId = LabAdminId, Name = "lab-admin", AuthMethod = Credential.AuthMethodPassword,
                    Username = "lab", EncryptedPassword = _protector.Encrypt("lab-pw"), IsActive = true,
                    AllowedHosts = "10.0.0.0/24", OperatorAssignable = false,
                },
                new Credential
                {
                    CredentialId = DmzId, Name = "dmz-ssh", AuthMethod = Credential.AuthMethodPassword,
                    Username = "dmz", EncryptedPassword = _protector.Encrypt("dmz-pw"), IsActive = true,
                    AllowedHosts = "192.0.2.0/24", OperatorAssignable = true,
                },
                new Credential
                {
                    // Deleted: never listed, never assignable.
                    CredentialId = Guid.NewGuid(), Name = "retired-ops", AuthMethod = Credential.AuthMethodPassword,
                    Username = "old", IsActive = false, AllowedHosts = "10.0.0.0/24", OperatorAssignable = true,
                },
                new Credential
                {
                    // Flagged without a scope (only reachable by writing the row directly).
                    CredentialId = Guid.NewGuid(), Name = "flag-only", AuthMethod = Credential.AuthMethodPassword,
                    Username = "x", IsActive = true, OperatorAssignable = true,
                });
            db.Devices.AddRange(
                new Device
                {
                    DeviceId = DeviceId, DeviceName = "core-sw", IpAddress = "10.0.0.9", Platform = "cisco_ios",
                    Site = "dc1", CredentialId = CredentialId, ExpectedSshHostKeyFingerprint = Pin,
                    AllowDraft = true, IsActive = true,
                },
                new Device
                {
                    DeviceId = BareDeviceId, DeviceName = "bare-box", IpAddress = "10.0.0.50",
                    AllowDraft = true, IsActive = true,
                },
                new Device
                {
                    DeviceId = ScopedDeviceId, DeviceName = "scoped-box", IpAddress = "10.0.0.20",
                    CredentialId = OpsId, AllowDraft = true, IsActive = true,
                },
                new Device
                {
                    DeviceId = MovedDeviceId, DeviceName = "moved-box", IpAddress = "203.0.113.7",
                    CredentialId = OpsId, AllowDraft = true, IsActive = true,
                });
            db.SaveChanges();
        }

        public AppDbContext Db()
        {
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(_name).Options);
            _opened.Add(db);
            return db;
        }

        public DeviceController Controller(HardeningTestUser user) => new(Db(), user);

        public CreateDevice NewDevice(Guid? credentialId = null, string? fingerprint = null, string ip = "203.0.113.10") => new()
        {
            DeviceName = "new-box",
            IpAddress = ip,
            CredentialId = credentialId,
            ExpectedSshHostKeyFingerprint = fingerprint,
        };

        public async Task AssertUntouchedAsync()
        {
            using var check = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(_name).Options);
            var row = await check.Devices.SingleAsync(d => d.DeviceId == DeviceId);
            Assert.Equal("core-sw", row.DeviceName);
            Assert.Equal("10.0.0.9", row.IpAddress);
            Assert.Equal("dc1", row.Site);
            Assert.Equal(CredentialId, row.CredentialId);
            Assert.Equal(Pin, row.ExpectedSshHostKeyFingerprint);
        }

        public (SshSnippetHandler Handler, CountingRunner Runner) Ssh(HardeningTestUser user)
        {
            var runner = new CountingRunner();
            var handler = new SshSnippetHandler(Db(), _protector, new NoSecrets(), runner, new AllowAll(),
                user, NullLogger<SshSnippetHandler>.Instance);
            return (handler, runner);
        }

        public (DeviceConnectHandler Tool, CountingRunner Runner) DeviceConnect(HardeningTestUser user)
        {
            var runner = new CountingRunner();
            var tool = new DeviceConnectHandler(Db(), user, _protector, runner, new AllowAll(),
                new nashira_backend.Services.Workflow.WorkflowExecutionScope(),
                NullLogger<DeviceConnectHandler>.Instance);
            return (tool, runner);
        }

        // Admin unless stated: the scope tests are about the scope, not R5.
        public AnsiblePlaybookSnippetHandler Ansible(HardeningTestUser? user = null) =>
            new(Db(), _protector, user ?? new HardeningTestUser("admin"),
                NullLogger<AnsiblePlaybookSnippetHandler>.Instance);

        public CredentialController Credentials(CapturingAudit? audit = null) =>
            new(Db(), _protector, new HardeningTestUser("admin"), audit ?? new CapturingAudit());

        public void Dispose()
        {
            foreach (var db in _opened) db.Dispose();
        }
    }

    private sealed class CountingRunner : ISshCommandRunner
    {
        public int Calls;
        public SshRunRequest? Last;

        public Task<SshRunOutcome> RunAsync(SshRunRequest request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return Task.FromResult(SshRunOutcome.Success(new SshRunResult
            {
                DeviceType = "cisco_ios",
                Results = [new SshCommandResult { Command = "show version", Output = "IOS", Ok = true, ElapsedMs = 1 }],
            }));
        }
    }

    private sealed class AllowAll : ISshCommandPolicy
    {
        public string Classify(string command) => "read";
        public CommandPolicyDecision Evaluate(IReadOnlyList<string> commands) => new(true, [], []);
    }

    // Counts decryptions: a refused send must not have decrypted anything.
    private sealed class CountingProtector : ISecretProtector
    {
        private readonly BundleTestKit.FakeProtector _inner = new();
        public int Decrypts;

        public byte[]? Encrypt(string? plaintext) => _inner.Encrypt(plaintext);

        public string? Decrypt(byte[]? ciphertext)
        {
            Decrypts++;
            return _inner.Decrypt(ciphertext);
        }
    }

    private sealed class CapturingAudit : nashira_backend.Services.Audit.IAuditLogger
    {
        public object? After;

        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            After = after;
            return Task.CompletedTask;
        }
    }

    // Resolves any ${secret:...} reference to "payload-secret"; leaves other text alone.
    private sealed class NoSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string> SubstituteAsync(string template, CancellationToken ct) =>
            Task.FromResult(template.Contains("${secret:", StringComparison.Ordinal) ? "payload-secret" : template);

        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct) =>
            SubstituteAsync(template, ct);
    }
}
