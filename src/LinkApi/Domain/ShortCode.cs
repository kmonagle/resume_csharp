// Why this file exists: generating short codes. RandomNumberGenerator is a
// cryptographically secure source, which makes codes unguessable; System.Random is
// fast but predictable.
//
// JS/TS vs C#: the same split as crypto.getRandomValues (secure) versus Math.random
// (not). Picking the wrong type is the classic security mistake, so the type used
// below is worth a glance.
using System.Security.Cryptography;

namespace LinkApi.Domain;

public static class ShortCode
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Length = 7;

    public static string Generate()
    {
        // JS/TS vs C#: `string.Create` builds a string of a known length in place,
        // avoiding intermediate arrays. `GetInt32(n)` returns an unbiased number in
        // [0, n), so there is no modulo bias from `byte % 62`.
        return string.Create(Length, state: 0, static (buffer, _) =>
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
    }
}
