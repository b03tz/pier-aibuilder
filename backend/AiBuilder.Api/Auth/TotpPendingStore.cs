using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AiBuilder.Api.Auth;

// Two short-lived in-memory buckets, both with the same TTL:
//
//   1. Pending-login: a password-verified admin id, awaiting a TOTP code to
//      complete sign-in. Issued by /auth/login when totpEnabled, consumed by
//      /auth/login/totp. Holds NO grant on its own.
//
//   2. Pending-enrolment: a freshly-generated Base32 secret, scoped to one
//      admin id, awaiting a confirmation code. Issued by /auth/totp/setup,
//      consumed by /auth/totp/confirm. Stays staged on retry so the user
//      doesn't have to re-scan the QR after a typo.
//
// Why in-memory: pending state's job is to expire fast. AiBuilder runs as a
// single instance behind Pier — no horizontal scale to reconcile. Process
// restart drops both buckets, which is harmless: the user re-types their
// password / re-clicks Enable.
public sealed class TotpPendingStore
{
    public static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<string, PendingLoginEntry> _logins = new();
    private readonly ConcurrentDictionary<string, PendingEnrolmentEntry> _enrolments = new();

    private sealed record PendingLoginEntry(string AdminId, DateTime ExpiresUtc);
    private sealed record PendingEnrolmentEntry(string SecretBase32, DateTime ExpiresUtc);

    public string IssuePendingLogin(string adminId)
    {
        SweepLogins();
        var id = NewId();
        _logins[id] = new PendingLoginEntry(adminId, DateTime.UtcNow + PendingTtl);
        return id;
    }

    // One-shot: returns the adminId iff the pendingId is live, then removes it.
    // Caller is responsible for calling this only after a successful TOTP
    // verify, since the entry is consumed regardless.
    public string? ConsumePendingLogin(string pendingId)
    {
        SweepLogins();
        if (!_logins.TryRemove(pendingId, out var entry)) return null;
        if (entry.ExpiresUtc < DateTime.UtcNow) return null;
        return entry.AdminId;
    }

    // Peek-only: returns the adminId without consuming. Used to verify the
    // code first; if it's wrong we leave the pending entry in place so the
    // user can retype.
    public string? PeekPendingLogin(string pendingId)
    {
        SweepLogins();
        if (!_logins.TryGetValue(pendingId, out var entry)) return null;
        if (entry.ExpiresUtc < DateTime.UtcNow) return null;
        return entry.AdminId;
    }

    public void DropPendingLogin(string pendingId) => _logins.TryRemove(pendingId, out _);

    public void StagePendingEnrolment(string adminId, string secretBase32)
    {
        SweepEnrolments();
        _enrolments[adminId] = new PendingEnrolmentEntry(secretBase32, DateTime.UtcNow + PendingTtl);
    }

    public string? GetPendingEnrolment(string adminId)
    {
        SweepEnrolments();
        if (!_enrolments.TryGetValue(adminId, out var entry)) return null;
        if (entry.ExpiresUtc < DateTime.UtcNow) return null;
        return entry.SecretBase32;
    }

    public void ClearPendingEnrolment(string adminId) => _enrolments.TryRemove(adminId, out _);

    private static string NewId()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b);
    }

    private void SweepLogins()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _logins)
            if (kv.Value.ExpiresUtc < now) _logins.TryRemove(kv.Key, out _);
    }

    private void SweepEnrolments()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _enrolments)
            if (kv.Value.ExpiresUtc < now) _enrolments.TryRemove(kv.Key, out _);
    }
}
