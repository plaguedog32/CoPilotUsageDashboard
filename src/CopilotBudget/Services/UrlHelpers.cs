namespace CopilotBudget.Services;

public static class UrlHelpers
{
    /// <summary>Only allow redirects back into this site, never to another host.</summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}
