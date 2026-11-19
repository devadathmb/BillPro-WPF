using System.Security.Cryptography;
using System.Text;

namespace BillPro.Helpers
{
    // ─────────────────────────────────────────────────────────────────────
    // Gate for the hidden Company Details unlock shortcut (Ctrl+Shift+Alt+C).
    // Only a SHA-256 hash lives in source — never the plain password — so
    // reading the compiled app or the DB file doesn't hand it over directly.
    // This raises the bar against casual tampering by an end user; it is
    // NOT meant to resist a determined attacker with a decompiler (a hard
    // requirement would need server-side verification or code obfuscation,
    // which is its own separate conversation if you ever need it).
    //
    // TO SET YOUR OWN PASSWORD: compute SHA-256 of your chosen password
    // (e.g. `python3 -c "import hashlib;print(hashlib.sha256(b'yourpass').hexdigest())"`)
    // and paste the result below, replacing the placeholder.
    // ─────────────────────────────────────────────────────────────────────
    public static class PasswordHelper
    {
        // SHA-256 of "changeme123" — CHANGE THIS before shipping to a client.
        private const string PasswordHash =
            "759fb95e38af6f9c532ca84822a1233afc3303598758d943e925dbb234f6f75e";

        public static bool Verify(string enteredPassword)
        {
            if (string.IsNullOrEmpty(enteredPassword)) return false;

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(enteredPassword));
                var sb = new StringBuilder(hashBytes.Length * 2);
                foreach (byte b in hashBytes) sb.Append(b.ToString("x2"));
                return sb.ToString() == PasswordHash;
            }
        }
    }
}