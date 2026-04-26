using System.Text;
using System.Text.RegularExpressions;

namespace AiBuilder.Api.Projects.Provisioning;

// Derives a Pier-valid app subdomain from an arbitrary project name.
//
// Pier's authoritative regex is `^[a-z][a-z0-9-]{1,30}$` — total length
// 2..31, lowercase letters / digits / hyphens, must start with a letter.
// This helper produces a string that is *guaranteed* to satisfy that
// regex; otherwise it throws (defensive — should be unreachable given
// the fallback rules below).
public static class PierAppSlug
{
    private static readonly Regex PierRegex = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.Compiled);
    private const int MaxLength = 31;
    private const int MinLength = 2;

    public static string Derive(string projectName)
    {
        var slug = NormalizeCore(projectName);

        // First-character rule: must be a-z. If empty or starts with a
        // digit/dash, prefix `app-`. Done before length truncation so we
        // don't accidentally chop the prefix off when truncating later.
        if (slug.Length == 0 || !char.IsBetween(slug[0], 'a', 'z'))
            slug = "app-" + slug;

        // Length rule: cap at 31, then re-trim trailing dashes (truncation
        // can leave one). If we ended up below the 2-char minimum after
        // trimming, fall back to "app".
        if (slug.Length > MaxLength) slug = slug[..MaxLength];
        slug = slug.TrimEnd('-');
        if (slug.Length < MinLength) slug = "app";

        if (!PierRegex.IsMatch(slug))
            // Defensive: NormalizeCore + the rules above should make this
            // unreachable. If it ever fires it's a bug — surface loudly
            // rather than letting an invalid name reach Pier.
            throw new InvalidOperationException(
                $"PierAppSlug.Derive produced '{slug}' which fails Pier's regex.");

        return slug;
    }

    // Append `-2`, `-3`, … to the base slug, preserving the length cap.
    // When the suffix doesn't fit, the base is shortened (and re-trimmed)
    // so the combined slug still passes the regex.
    public static string WithCollisionSuffix(string baseSlug, int attempt)
    {
        if (attempt < 2) throw new ArgumentOutOfRangeException(nameof(attempt), "first attempt is the bare base");
        var suffix = "-" + attempt.ToString();
        var room = MaxLength - suffix.Length;
        if (room < MinLength)
            // No room for any base — should never happen since attempts
            // grow logarithmically (we cap at 5) but defensive anyway.
            throw new InvalidOperationException("collision suffix would not fit within Pier's length cap");
        var trimmed = baseSlug.Length <= room ? baseSlug : baseSlug[..room];
        trimmed = trimmed.TrimEnd('-');
        if (trimmed.Length < MinLength) trimmed = "app";
        var result = trimmed + suffix;
        if (!PierRegex.IsMatch(result))
            throw new InvalidOperationException($"PierAppSlug.WithCollisionSuffix produced '{result}' which fails Pier's regex.");
        return result;
    }

    // Lowercase + replace common separators with `-` + drop everything
    // outside [a-z0-9-] + collapse runs of `-` + trim leading/trailing `-`.
    // Returns "" for an input with no usable chars; the caller handles the
    // fallback to "app".
    private static string NormalizeCore(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lower = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        var lastWasDash = false;
        foreach (var ch in lower)
        {
            char c;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') c = ch;
            else if (ch is ' ' or '_' or '.' or '/' or '\\' or '-') c = '-';
            else continue;

            if (c == '-')
            {
                if (lastWasDash || sb.Length == 0) continue;
                sb.Append('-');
                lastWasDash = true;
            }
            else
            {
                sb.Append(c);
                lastWasDash = false;
            }
        }
        // Trim trailing dash if any.
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }
}
