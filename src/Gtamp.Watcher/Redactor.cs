using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gtamp.Watcher
{
    /// <summary>
    /// Strips from captured text everything that identifies the machine or its
    /// owner, before that text goes anywhere.
    /// <para>
    /// This runs on every line of every file the watcher collects, and it runs
    /// whether or not the incident will be published — because the point at which
    /// a decision to publish is made is not the point at which the file was
    /// written, and a redactor that only runs on the publishing path is a redactor
    /// that will one day be skipped.
    /// </para>
    /// <para>
    /// What it cannot do is redact a screenshot. A picture of the game may carry a
    /// player name, a server address in the overlay, and whatever else is on the
    /// screen; there is no way to find that automatically. Screenshots are
    /// therefore never published unless asked for by a separate flag.
    /// </para>
    /// </summary>
    public static class Redactor
    {
        private static readonly (Regex Pattern, string Replacement)[] Rules =
        {
            // The identity keypair. The secret is the private key that proves who a
            // player is; the token is the public half, and still a stable
            // identifier across servers.
            (new Regex(@"(?im)^(\s*IdentitySecret\s*=).*$"), "$1(redacted)"),
            (new Regex(@"(?im)^(\s*IdentityToken\s*=).*$"), "$1(redacted)"),
            (new Regex(@"(?im)^(\s*ServerPassword\s*=).*$"), "$1(redacted)"),
            (new Regex(@"(?i)\bidentity(Secret|Token)""?\s*[:=]\s*""[^""]*"""), "identity$1=\"(redacted)\""),

            // Windows profile directories carry the account name.
            (new Regex(@"(?i)([A-Z]:\\Users\\)[^\\\r\n""]+"), "$1(user)"),
            (new Regex(@"(?i)(/home/)[^/\r\n""]+"), "$1(user)"),

            // Any address that is not loopback or a private LAN range. A server
            // somebody else hosts is somebody else's address to give out.
            (new Regex(
                @"\b(?!127\.|10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|0\.0\.0\.0)(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b"),
                "(address)"),
        };

        /// <summary>Redacts one block of text. Never throws; a rule that cannot run leaves the text as it was.</summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            string result = text;
            foreach ((Regex pattern, string replacement) in Rules)
            {
                try
                {
                    result = pattern.Replace(result, replacement);
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological line is not worth failing an incident over.
                }
            }

            return result;
        }

        /// <summary>
        /// True when a file must never be copied at all, redacted or not.
        /// <para>
        /// <c>client.ini</c> holds the private key. The client already writes a
        /// redacted copy of it into every bundle, and that copy is the one to take.
        /// Relying on <see cref="Scrub"/> to blank the secret would work today and
        /// break the day a setting is renamed — refusing the file outright does not.
        /// </para>
        /// </summary>
        public static bool IsForbidden(string fileName)
        {
            // The name is taken by hand rather than with Path.GetFileName, because
            // that method splits on the *host's* separator: on Linux it leaves
            // "E:\GTA V\Gtamp\client.ini" whole, the comparison fails, and the file
            // holding the private key is copied. The watcher runs on Windows, but a
            // safety check that depends on which platform it runs on is not one.
            string path = fileName ?? string.Empty;
            int separator = path.LastIndexOfAny(new[] { '\\', '/' });
            string name = separator >= 0 ? path.Substring(separator + 1) : path;

            return name.Equals("client.ini", StringComparison.OrdinalIgnoreCase)
                || name.Equals("server.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
        }

        public static IEnumerable<string> ForbiddenExamples() => new[]
        {
            "client.ini — the private key lives in it; the bundle's client.ini.redacted is the copy to take",
            "server.json — may hold the server password",
            "*.db — the persisted world, including every player's identity token",
        };
    }
}
