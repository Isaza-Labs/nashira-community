using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Security;

// The one definition of who may change what a device's SSH trust rests on.
//
// Device SSH decrypts the credential linked to a device and sends it to the device's
// address. Whoever controls the link, the address or the host-key pin therefore
// controls where stored secret material goes, so those three are held to the house
// rule for stored secrets: only an Admin (decided exactly as SecretAccessPolicy
// decides it, so a run lowered by RunAuthority is lowered here too).
//
// The one delegation: an Admin can give a credential a scope (CredentialScope) and
// mark it OperatorAssignable. A scope bounds where the secret may ever be sent, so a
// non-Admin choosing a scoped credential, or moving a device within that scope,
// cannot send it anywhere the Admin did not allow. Runtime enforcement at send time
// (the ssh step, device_connect, ansible) backs every rule here up.
//
//   R1  link a credential      Admin; or the credential is assignable, scoped and
//                              covers the device's final address.
//   R2  host-key pin           Admin only.
//   R3  re-address a device    Admin; or the device's (final) linked credential is
//       with a credential      scoped and covers the new address.
//   R4  SSH step `credential`  Admin; or the named credential is assignable and
//                              scoped (the send-time scope check then applies).
//
// Each check returns a refusal message, or null when the change is allowed. Sending
// the value the row already holds is not a change: forms resend the whole object. A
// credential that does not exist and one the caller may not use get the SAME refusal,
// so a refused caller learns nothing about which credentials exist.
public static class DeviceTrustPolicy
{
    public const string AdminRequiredCode = "device_trust_admin_required";

    public const string CredentialLinkMessage =
        "Only an Admin may link this credential to a device; Operators may link only credentials an Admin "
        + "made assignable, to devices whose address is within that credential's allowed hosts";

    public const string HostKeyPinMessage =
        "Only an Admin may set or change a device's expected SSH host-key fingerprint";

    public const string AddressMessage =
        "This device has a credential linked; only an Admin may change its IP address to one outside "
        + "that credential's allowed hosts";

    public const string StepCredentialMessage =
        "Only an Admin may choose a stored credential by name in an SSH step (`credential`), unless an Admin "
        + "made that credential assignable by Operators; without it the step uses the device's own credential";

    public static bool IsAdmin(ICurrentUser user) => SecretAccessPolicy.MayResolveStored(user);

    /// <summary>A non-Admin may use this credential at all: assignable and scoped.</summary>
    public static bool IsDelegable(Credential? credential) =>
        credential is { IsActive: true, OperatorAssignable: true } && CredentialScope.IsScoped(credential);

    // R1. `requested` null means "not sent" (update) or "none" (create): no change.
    // `requestedRow` is the active credential with that id, or null when there is
    // none; `finalAddress` the device's address once this same request is applied.
    public static string? CredentialLinkRefusal(
        ICurrentUser user, Guid? current, Guid? requested, Credential? requestedRow, string? finalAddress)
    {
        if (requested is null || requested == current) return null;
        if (IsAdmin(user)) return null;
        if (requestedRow is null || requestedRow.CredentialId != requested || !IsDelegable(requestedRow))
            return CredentialLinkMessage;
        return CredentialScope.Permits(requestedRow, finalAddress) ? null : CredentialLinkMessage;
    }

    // R2. `requested` null means "not sent"; empty and null both mean "no pin".
    public static string? HostKeyPinRefusal(ICurrentUser user, string? current, string? requested)
    {
        if (requested is null) return null;
        if (string.Equals(NoPinAsNull(current), NoPinAsNull(requested), StringComparison.Ordinal)) return null;
        return IsAdmin(user) ? null : HostKeyPinMessage;
    }

    // R3. Only a device with a credential linked is protected: re-addressing it would
    // send that credential to the new address. Values compare trimmed. `linkedRow` is
    // the credential the device will have linked once this request is applied (null
    // when it cannot be found, which a non-Admin is refused on).
    public static string? AddressRefusal(
        ICurrentUser user, Guid? linkedCredential, Credential? linkedRow, string? current, string? requested)
    {
        if (requested is null || linkedCredential is null) return null;
        if (string.Equals(current?.Trim(), requested.Trim(), StringComparison.Ordinal)) return null;
        if (IsAdmin(user)) return null;
        if (linkedRow is null || linkedRow.CredentialId != linkedCredential || !CredentialScope.IsScoped(linkedRow))
            return AddressMessage;
        return CredentialScope.Permits(linkedRow, requested.Trim()) ? null : AddressMessage;
    }

    // R4. An SSH step naming a stored credential, instead of using the device's own.
    // `namedRow` is the active credential with that name, or null.
    public static string? StepCredentialRefusal(ICurrentUser user, string? credentialName, Credential? namedRow)
    {
        if (string.IsNullOrWhiteSpace(credentialName)) return null;
        if (IsAdmin(user)) return null;
        return IsDelegable(namedRow) ? null : StepCredentialMessage;
    }

    // For everyone, Admins included: a device whose linked credential's scope does not
    // cover its address can never connect, so making it so (by linking, or by
    // re-addressing) is a validation error rather than a run-time surprise. Only
    // checked when the link or the address actually changes, so a device left outside
    // a scope an Admin later narrowed can still be edited otherwise.
    public static string? ScopeMismatch(Credential? finalCredential, string? finalAddress)
    {
        if (finalCredential is null || CredentialScope.Permits(finalCredential, finalAddress)) return null;
        return $"credential '{finalCredential.Name}' is not allowed for address '{finalAddress?.Trim()}': "
               + "it is outside that credential's allowed hosts, so the device could never connect with it";
    }

    private static string? NoPinAsNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
