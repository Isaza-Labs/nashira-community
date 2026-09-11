using nashira_backend.Configuration.Modules;

namespace nashira_backend.Services.Email;

// Emails a newly created user their credentials. The password travels in the
// message by necessity — creation is the one moment the system still holds it in
// clear — so the message states it is temporary and must be changed at first
// sign-in.
//
// The deployment relay (Smtp:*) is preferred; when it is not configured the mail
// goes through the fallback email channel instead, so an install whose only SMTP
// setup lives in /admin/email still delivers credentials.
//
// Creating users is core; delivering mail is communications. This is the one place the
// two meet, and the direction matters: core may offer the effect, never require it. With
// communications disabled the account is still created and the admin is told, in the
// same response, that the password has to travel some other way.
//
// Never throws: creating the user must not fail because mail did. Returns null
// when the email went out; otherwise a warning for the admin explaining that
// the credentials were NOT emailed and must reach the user another way.
public interface IUserCredentialsNotifier
{
    Task<string?> SendCredentialsAsync(string toAddress, string username, string password, CancellationToken ct);
}

public sealed class UserCredentialsNotifier : IUserCredentialsNotifier
{
    private readonly IEmailService _email;
    private readonly IEmailChannelFallback _channelFallback;
    private readonly IEmailChannelSender _channelSender;
    private readonly ModuleSelection _modules;
    private readonly ILogger<UserCredentialsNotifier> _logger;

    public UserCredentialsNotifier(
        IEmailService email, IEmailChannelFallback channelFallback, IEmailChannelSender channelSender,
        ModuleSelection modules, ILogger<UserCredentialsNotifier> logger)
    {
        _email = email;
        _channelFallback = channelFallback;
        _channelSender = channelSender;
        _modules = modules;
        _logger = logger;
    }

    public async Task<string?> SendCredentialsAsync(
        string toAddress, string username, string password, CancellationToken ct)
    {
        // Before anything is composed: a deployment without communications has no
        // relay to prefer and no channel to fall back to, and saying so plainly beats
        // reporting it as an unconfigured email service the admin would go looking for.
        if (!_modules.IsEnabled(ModuleId.Communications))
        {
            _logger.LogInformation(
                "user.credentials_email.skipped user={Username} reason=module_disabled", username);
            return "Communications is disabled in this deployment, so the credentials email was not sent. " +
                   "Deliver the username and temporary password to the user through another channel.";
        }

        const string subject = "Your account credentials";
        var body =
            $"Hello {username},\n\n" +
            "An account has been created for you.\n\n" +
            $"Username: {username}\n" +
            $"Temporary password: {password}\n\n" +
            "This password is temporary. Please sign in and change it as soon as possible.\n";

        try
        {
            if (_email.IsConfigured)
            {
                await _email.SendAsync(
                    [toAddress], subject, body, isHtml: false, cc: null, attachment: null, ct);
                return null;
            }

            var channel = await _channelFallback.FindAsync(ct);
            if (channel is null)
            {
                _logger.LogWarning(
                    "user.credentials_email.skipped user={Username} reason=email_not_configured", username);
                return "The email service is not configured (no Smtp:* relay and no enabled email channel), " +
                       "so the credentials email was not sent. " +
                       "Deliver the username and temporary password to the user through another channel.";
            }

            await _channelSender.SendAsync(channel, [toAddress], subject, body, ct);
            _logger.LogInformation(
                "user.credentials_email.sent_via_channel user={Username} channel={Channel}",
                username, channel.Name);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The password never reaches the log — only the fact that the mail failed.
            _logger.LogWarning(ex, "user.credentials_email.failed user={Username}", username);
            return $"The credentials email could not be sent ({ex.Message}). " +
                   "Deliver the username and temporary password to the user through another channel.";
        }
    }
}
