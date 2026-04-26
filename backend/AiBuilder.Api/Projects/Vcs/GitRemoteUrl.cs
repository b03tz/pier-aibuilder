using System.Text.RegularExpressions;

namespace AiBuilder.Api.Projects.Vcs;

// Validates and canonicalises the remote URL we persist on the Project.
// This is the gate between "patrick typed something in a box" and "the
// claude subprocess is about to run `git remote add origin <thing>`", so
// the check is strict — we reject anything with embedded credentials,
// anything with shell metacharacters, and anything that doesn't match
// one of the three forms git actually understands.
public static class GitRemoteUrl
{
    // Forms we accept:
    //   1. scp-ish ssh:    git@host:owner/repo(.git)?
    //   2. ssh:// URL:     ssh://user@host[:port]/path
    //   3. https URL:      https://host[:port]/path (NO userinfo)
    //   4. http URL:       same as https but allowed for intranet/dev
    //   5. git://          read-only protocol, rarely used but cheap to allow
    private static readonly Regex ScpLike = new(
        @"^[a-zA-Z0-9_.-]+@[a-zA-Z0-9.-]+:[a-zA-Z0-9._/~-]+$",
        RegexOptions.Compiled);

    // Characters that have no business in a URL and would be interesting
    // if they sneaked past git into a shell. Reject the whole URL on
    // sight if we see any of these.
    private static readonly char[] Forbidden =
        { '\r', '\n', '\t', '\0', ';', '|', '&', '`', '$', '<', '>', '"', '\'', ' ' };

    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "empty";
            return false;
        }
        var v = raw.Trim();
        if (v.Length > 512) { error = "too-long"; return false; }
        if (v.IndexOfAny(Forbidden) >= 0) { error = "forbidden-characters"; return false; }

        if (ScpLike.IsMatch(v))
        {
            normalized = v;
            return true;
        }

        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri))
        {
            error = "invalid-url";
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "https" && scheme != "http" && scheme != "ssh" && scheme != "git")
        {
            error = "unsupported-scheme";
            return false;
        }

        // Reject URLs that embed credentials. The whole point of this
        // feature is to hand auth over to the OS user's SSH key / git
        // credential helper — a password baked into the URL would be a
        // mystery secret we never asked for and would sync to Plexxer.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // `ssh://git@host/repo` is conventional and harmless — a bare
            // username with no password. Anything with a colon in userinfo
            // carries a password.
            if (uri.UserInfo.Contains(':'))
            {
                error = "credentials-in-url";
                return false;
            }
            // Also refuse https://user@... — there's no reason for it and
            // it masquerades as an auth mechanism we don't want to support.
            if (scheme is "https" or "http")
            {
                error = "credentials-in-url";
                return false;
            }
        }

        normalized = uri.ToString();
        return true;
    }

    private static readonly Regex BranchRegex = new("^[A-Za-z0-9._/-]{1,100}$", RegexOptions.Compiled);

    public static bool TryNormalizeBranch(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) { error = "empty"; return false; }
        var v = raw.Trim();
        if (!BranchRegex.IsMatch(v) || v.StartsWith('-') || v.StartsWith('/') || v.Contains(".."))
        {
            error = "invalid-branch";
            return false;
        }
        normalized = v;
        return true;
    }
}
