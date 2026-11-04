using Microsoft.AspNetCore.Identity;

namespace JNotes.Web.Auth;

/// <summary>Verifies the single owner password against a PBKDF2 hash from configuration.</summary>
public sealed class PasswordVerifier(string passwordHash)
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object User = new();

    public bool Verify(string? password) =>
        !string.IsNullOrEmpty(password) &&
        Hasher.VerifyHashedPassword(User, passwordHash, password) != PasswordVerificationResult.Failed;

    public static string Hash(string password) => Hasher.HashPassword(User, password);
}
