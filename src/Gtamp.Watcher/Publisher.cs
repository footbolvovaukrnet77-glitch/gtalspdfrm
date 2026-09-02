using System;
using System.Diagnostics;
using System.IO;

namespace Gtamp.Watcher
{
    /// <summary>
    /// Pushes finished incident folders into the git repository, which is the only
    /// route by which anything on this machine reaches anybody else automatically.
    /// <para>
    /// Off by default, and it stays off until asked for, because publishing is not
    /// a technical step: <c>git push</c> puts these files where the repository can
    /// be read, and if that repository is public then "where it can be read" means
    /// everybody, permanently. <see cref="WatcherOptions"/> checks for that and
    /// refuses rather than warns.
    /// </para>
    /// </summary>
    public sealed class Publisher
    {
        private readonly WatcherOptions _options;

        public Publisher(WatcherOptions options)
        {
            _options = options;
        }

        public bool Publish(string incidentFolder, Incident incident, out string detail)
        {
            string relative = Path.GetRelativePath(_options.RepositoryDirectory, incidentFolder);

            // --force on purpose: `diagnostics/` is in .gitignore so incidents never
            // ride along with ordinary work by accident. Publishing is the one time
            // that default is meant to be overridden, and it is overridden here
            // rather than by taking the folder out of .gitignore, so the only way
            // these files reach a commit is somebody passing --publish.
            if (!Git(out detail, "add", "--force", "--", relative))
            {
                return false;
            }

            if (!Git(out detail, "commit", "-m", $"watcher: {incident.Kind} at {incident.NoticedAt:HH:mm:ss}"))
            {
                return false;
            }

            // A branch of its own, on purpose: incidents are evidence, not changes,
            // and mixing them into the branch under review makes both harder to read.
            return Git(out detail, "push", _options.Remote, $"HEAD:{_options.Branch}");
        }

        private bool Git(out string detail, params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = _options.RepositoryDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using Process? process = Process.Start(start);
                if (process == null)
                {
                    detail = "git не запустился";
                    return false;
                }

                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(60_000);
                detail = output.Trim();
                return process.ExitCode == 0;
            }
            catch (Exception exception)
            {
                detail = exception.Message;
                return false;
            }
        }

        /// <summary>Whether the repository's origin points at a public GitHub project.</summary>
        public static bool RemoteLooksPublic(string repositoryDirectory, out string remoteUrl)
        {
            remoteUrl = string.Empty;

            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("remote");
            start.ArgumentList.Add("get-url");
            start.ArgumentList.Add("origin");

            try
            {
                using Process? process = Process.Start(start);
                if (process == null)
                {
                    return false;
                }

                remoteUrl = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(20_000);
            }
            catch (Exception)
            {
                return false;
            }

            // Visibility cannot be read without the API, so this errs towards
            // treating a GitHub remote as public: being wrong in that direction
            // costs one command-line flag, and being wrong in the other direction
            // publishes somebody's logs.
            return remoteUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
