using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

public record EmailAttachment(string FileName, string ContentType, byte[] Content);

public record EmailMessage(IReadOnlyList<string> To, string Subject, string Body, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>
/// Hook for sending email. Register a real implementation (Azure Communication Services, SendGrid, SMTP, Microsoft
/// Graph...) in Program.cs in place of <see cref="NotImplementedEmailSender"/>.
/// </summary>
public interface IEmailSender
{
    /// <summary>True once a real sender is plugged in. The UI says email is off while this is false.</summary>
    bool IsConfigured { get; }

    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Placeholder: sending email isn't implemented. Messages are logged and dropped.</summary>
public class NotImplementedEmailSender(ILogger<NotImplementedEmailSender> logger) : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        // TODO: send the email. Until then, log what would have gone out.
        logger.LogInformation("Email not sent (sending isn't implemented): \"{Subject}\" to {To}{Attachments}",
            message.Subject, string.Join(", ", message.To),
            message.Attachments is { Count: > 0 } a ? $" with {string.Join(", ", a.Select(x => x.FileName))}" : "");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Decides who hears about what, and hands the messages to <see cref="IEmailSender"/>. A failure to send never
/// fails the action that triggered it.
/// </summary>
public class Notifications(
    IEmailSender email,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<AppOptions> appOptions,
    ILogger<Notifications> logger)
{
    private string Unit => appOptions.Value.UnitLabel;

    public async Task CreditsSentAsync(int fromUserId, int toUserId, decimal amount, bool held)
    {
        var (from, to) = (await UserAsync(fromUserId), await UserAsync(toUserId));
        if (from is null || to is null) return;
        if (held)
        {
            await SendAsync(await ApproverEmailsAsync(fromUserId, toUserId), "A transfer is waiting for your approval",
                $"{from.DisplayName} wants to send {Format.Amount(amount)} {Unit} to {to.DisplayName}. Approve or reject it on the Approvals page.");
            return;
        }
        await SendAsync([to.Email], $"{from.DisplayName} sent you {Format.Amount(amount)} {Unit}",
            $"{from.DisplayName} sent you {Format.Amount(amount)} Copilot {Unit}. They're yours until the end of the month.");
    }

    public async Task ForcedTransferAsync(AppUser from, AppUser to, decimal amount, string reason)
    {
        await SendAsync([from.Email], $"An admin moved {Format.Amount(amount)} of your {Unit}",
            $"An admin moved {Format.Amount(amount)} {Unit} from your budget to {to.DisplayName}. Reason: {reason}");
        await SendAsync([to.Email], $"You received {Format.Amount(amount)} {Unit}",
            $"An admin moved {Format.Amount(amount)} {Unit} to you from {from.DisplayName}. Reason: {reason}");
    }

    public async Task TransferDecidedAsync(PendingTransfer t, bool approved)
    {
        var (from, to) = (await UserAsync(t.FromUserId), await UserAsync(t.ToUserId));
        if (from is null || to is null) return;
        var subject = approved ? $"Transfer approved: {Format.Amount(t.Amount)} {Unit}" : $"Transfer not approved: {Format.Amount(t.Amount)} {Unit}";
        var body = approved
            ? $"{from.DisplayName}'s transfer of {Format.Amount(t.Amount)} {Unit} to {to.DisplayName} was approved."
            : $"{from.DisplayName}'s transfer of {Format.Amount(t.Amount)} {Unit} to {to.DisplayName} wasn't approved. The credits stayed with {from.DisplayName}.";
        await SendAsync([from.Email, to.Email], subject, body);
    }

    public async Task RequestFilledAsync(BudgetRequest r, int filledById)
    {
        var (requester, filler) = (await UserAsync(r.RequesterId), await UserAsync(filledById));
        if (requester is null || filler is null) return;
        await SendAsync([requester.Email], "Your request was filled",
            $"{filler.DisplayName} filled your request for {Format.Amount(r.Amount)} {Unit}.");
    }

    public async Task ProposalCreatedAsync(BudgetProposal p)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var authorities = (await db.Users.AsNoTracking().ToListAsync())
            .Where(u => u.IsBudgetAuthority && !u.IsDisabled && u.Id != p.ProposedById).Select(u => u.Email).ToList();
        await SendAsync(authorities, "A budget increase needs your approval",
            $"{p.Summary}. This adds {Format.Signed(p.SpendChange)} {Unit} to this month's total. Review it on the Budgets page.");
    }

    public async Task ProposalDecidedAsync(BudgetProposal p)
    {
        var proposer = await UserAsync(p.ProposedById);
        await SendAsync([proposer?.Email], $"Budget change {(p.Status == ProposalStatus.Applied ? "approved" : "not approved")}",
            $"{p.Summary}: {(p.Status == ProposalStatus.Applied ? "approved and applied" : "not approved")}" +
            (string.IsNullOrEmpty(p.DecisionNote) ? "." : $" ({p.DecisionNote})."));
    }

    public async Task ReconciliationRequiredAsync(IReadOnlyCollection<string> logins)
    {
        if (logins.Count == 0) return;
        await SendAsync(await AdminEmailsAsync(), "GitHub budgets need reconciling",
            $"These people's GitHub budgets don't match the app and weren't changed: {string.Join(", ", logins)}. Review them on the GitHub sync page.");
    }

    public async Task MonthEndReportAsync(IReadOnlyCollection<string> recipients, string period, string summary, byte[] csv)
    {
        await SendAsync(recipients, $"Copilot credits month-end close: {Period.Display(period)}", summary,
            [new EmailAttachment($"copilot-credits-{period}.csv", "text/csv", csv)]);
    }

    public async Task AlertRaisedAsync(Alert alert)
    {
        // Credits about to expire are a budget matter; everything else is about the connections.
        var roles = alert.Kind == "expiry" ? AppRole.Admin | AppRole.BudgetAuthority : AppRole.Admin | AppRole.IntegrationAdmin;
        await SendAsync(await RoleEmailsAsync(roles), $"[{alert.Severity}] {alert.Title}",
            $"{alert.Title}\n\n{alert.Detail}\n\nSee the Alerts page for details.");
    }

    public async Task PersonLeftAsync(AppUser user, decimal creditsLeft, string cleanup)
    {
        await SendAsync(await AdminEmailsAsync(), $"{user.DisplayName} has left: credits to reassign",
            $"{user.DisplayName} ({user.Login}) was marked as left: {user.DisabledReason}.\n{cleanup}\n" +
            $"They have {Format.Amount(creditsLeft)} {Unit} left this month. Reassign them on the People & access page.");
    }

    /// <summary>Everyone with any of the roles (including people listed in App:Admins for Admin).</summary>
    public async Task<List<string?>> RoleEmailsAsync(AppRole roles)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var users = await db.Users.AsNoTracking().Where(u => u.Email != null && !u.IsDisabled).ToListAsync();
        return users.Where(u => (u.Roles & roles) != 0
                                || (roles.HasFlag(AppRole.Admin) && (appOptions.Value.IsAdmin(u.Login) || appOptions.Value.IsAdmin(u.Email))))
            .Select(u => u.Email).ToList();
    }

    public async Task<List<string?>> AdminEmailsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var users = await db.Users.AsNoTracking().Where(u => u.Email != null && !u.IsDisabled).ToListAsync();
        return users.Where(u => u.IsAdmin || appOptions.Value.IsAdmin(u.Login) || appOptions.Value.IsAdmin(u.Email))
            .Select(u => u.Email).ToList();
    }

    private async Task<List<string?>> ApproverEmailsAsync(params int[] excluding)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var approvers = (await db.Users.AsNoTracking().ToListAsync())
            .Where(u => u.IsApprover && !u.IsDisabled && !excluding.Contains(u.Id)).Select(u => u.Email).ToList();
        return approvers.Count > 0 ? approvers : await AdminEmailsAsync();
    }

    private async Task<AppUser?> UserAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    private async Task SendAsync(IEnumerable<string?> to, string subject, string body, IReadOnlyList<EmailAttachment>? attachments = null)
    {
        var recipients = to.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recipients.Count == 0) return;
        try
        {
            await email.SendAsync(new EmailMessage(recipients, subject, body, attachments));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't send email \"{Subject}\"", subject);
        }
    }
}
