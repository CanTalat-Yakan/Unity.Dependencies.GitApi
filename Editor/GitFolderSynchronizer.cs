﻿#if UNITY_EDITOR
using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace UnityEssentials
{
    public partial class GitFolderSynchronizer
    {
        private const string TokenKey = "GitToken";

        // Background progress runner state
        private static volatile bool s_ProgressActive;
        private static string s_ProgressTitle;
        private static string s_ProgressInfo;
        private static float s_ProgressValue;
        private static Action s_OnProgressComplete;

        private static void StartProgress(string title, Action<Action<string, float>> work, Action onComplete)
        {
            if (s_ProgressActive)
            {
                Debug.LogWarning("[Git] Another operation is in progress.");
                return;
            }

            s_ProgressActive = true;
            s_ProgressTitle = title;
            s_ProgressInfo = "Starting...";
            s_ProgressValue = 0f;
            s_OnProgressComplete = onComplete;

            // Hook updater on main thread
            EditorApplication.update -= OnProgressUpdate;
            EditorApplication.update += OnProgressUpdate;

            // Start background work
            new Thread(() =>
            {
                try
                {
                    void Report(string info, float progress)
                    {
                        s_ProgressInfo = info;
                        s_ProgressValue = Math.Clamp(progress, 0f, 1f);
                    }

                    work(Report);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Git] Operation failed: " + ex);
                }
                finally
                {
                    s_ProgressActive = false;
                }
            }) { IsBackground = true }.Start();
        }

        private static void OnProgressUpdate()
        {
            if (s_ProgressActive)
            {
                EditorUtility.DisplayProgressBar(s_ProgressTitle, s_ProgressInfo, s_ProgressValue);
                return;
            }

            // Done: clear and cleanup
            EditorUtility.ClearProgressBar();
            EditorApplication.update -= OnProgressUpdate;
            var complete = s_OnProgressComplete;
            s_OnProgressComplete = null;
            try { complete?.Invoke(); }
            catch (Exception ex) { Debug.LogError("[Git] Completion handler failed: " + ex); }
        }

        /// <summary>
        /// Executes a Git command in the specified directory and captures its output, error messages, and exit code.
        /// </summary>
        /// <remarks>This method uses the `git` executable to run the specified command. Ensure that Git
        /// is installed and available in the system's PATH. The method redirects both standard output and standard
        /// error streams and waits for the process to complete. If an exception occurs during execution, the error
        /// message is captured in the <c>error</c> field of the returned tuple.</remarks>
        /// <param name="path">The working directory where the Git command will be executed. Must be a valid directory path.</param>
        /// <param name="arguments">The arguments to pass to the Git command. For example, "status" or "commit -m 'message'".</param>
        /// <returns>A tuple containing the following: <list type="bullet"> <item><description><c>output</c>: The standard output
        /// produced by the Git command.</description></item> <item><description><c>error</c>: The standard error output
        /// produced by the Git command, or the exception message if an error occurs.</description></item>
        /// <item><description><c>exitCode</c>: The exit code returned by the Git process. A value of 0 typically
        /// indicates success.</description></item> </list></returns>
        private static (string output, string error, int exitCode) RunGitCommand(string path, string arguments)
        {
            string output = string.Empty;
            string error = string.Empty;
            int exitCode = -1;

            try
            {
                ProcessStartInfo startInfo = new("git", arguments)
                {
                    WorkingDirectory = path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.EnvironmentVariables["LC_ALL"] = "C";

                using (Process process = Process.Start(startInfo))
                {
                    output = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.LogError($"[Git] Command 'git {arguments}' failed: {ex.Message}");
            }

            return (output, error, exitCode);
        }

        /// <summary>
        /// Executes a Git push command for the specified repository path, using an authentication token stored in
        /// EditorPrefs.
        /// </summary>
        /// <remarks>This method retrieves the remote URL of the repository and attempts to authenticate
        /// using a token stored in EditorPrefs. If the remote URL uses HTTPS, the token is embedded in the URL for
        /// authentication. If the remote URL uses SSH, the method will fail because tokens are not compatible with SSH
        /// authentication, and SSH keys should be used instead.</remarks>
        /// <param name="path">The file system path to the local Git repository.</param>
        /// <param name="token">The authentication token to use for the push operation. This should be a valid
        /// token with permissions to access the repository.</param>
        /// <returns>A tuple containing the following: <list type="bullet"> <item><description><c>output</c>: The standard output
        /// from the Git command.</description></item> <item><description><c>error</c>: The standard error output from
        /// the Git command, if any.</description></item> <item><description><c>exitCode</c>: The exit code of the Git
        /// command. A value of 0 indicates success; non-zero indicates failure.</description></item> </list></returns>
        private static (string output, string error, int exitCode) RunPushGitCommand(string path, string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                Debug.LogError("[Git] No token found for push operation");
                return ("", "No Git token configured", -1);
            }

            // First get the remote URL
            var (remoteOutput, remoteError, remoteExitCode) = RunGitCommand(path, "remote get-url origin");
            if (remoteExitCode != 0 || string.IsNullOrEmpty(remoteOutput))
            {
                Debug.LogError("[Git] Failed to get remote URL");
                return (remoteOutput, remoteError, remoteExitCode);
            }

            string remoteUrl = remoteOutput.Trim();

            // Handle different URL formats
            string authenticatedUrl;
            if (remoteUrl.StartsWith("https://"))
            {
                // For HTTPS URLs, insert the token
                var uri = new Uri(remoteUrl);
                authenticatedUrl = $"https://{token}@{uri.Host}{uri.PathAndQuery} HEAD";
            }
            else if (remoteUrl.StartsWith("git@"))
            {
                // For SSH URLs, we can't use token auth - might want to use SSH keys instead
                Debug.LogError("[Git] SSH remote detected - tokens don't work with SSH. Use SSH keys instead.");
                return ("", "SSH remote detected - use SSH keys instead of tokens", -1);
            }
            else
            {
                Debug.LogError($"[Git] Unsupported remote URL format: {remoteUrl}");
                return ("", $"Unsupported remote URL format: {remoteUrl}", -1);
            }

            // Execute the push with the authenticated URL
            return RunGitCommand(path, $"push {authenticatedUrl}");
        }

        private static (string output, string error, int exitCode) RunPullGitCommand(string path, string token)
        {
            // Pull doesn't always require auth (public repos, cached creds, etc). If no token is present,
            // attempt a normal pull.
            if (string.IsNullOrEmpty(token))
            {
                return RunGitCommand(path, "pull");
            }

            // Try to authenticate only for HTTPS remotes.
            var (remoteOutput, remoteError, remoteExitCode) = RunGitCommand(path, "remote get-url origin");
            if (remoteExitCode != 0 || string.IsNullOrEmpty(remoteOutput))
            {
                return (remoteOutput, remoteError, remoteExitCode);
            }

            string remoteUrl = remoteOutput.Trim();

            if (remoteUrl.StartsWith("https://"))
            {
                var uri = new Uri(remoteUrl);
                // Use a token-in-URL for just this command.
                // Note: We keep the pull target explicit to avoid git prompting for credentials.
                string authenticatedUrl = $"https://{token}@{uri.Host}{uri.PathAndQuery}";
                return RunGitCommand(path, $"pull {authenticatedUrl}");
            }

            if (remoteUrl.StartsWith("git@"))
            {
                // SSH remotes should be handled by SSH keys/agent.
                return RunGitCommand(path, "pull");
            }

            // Unknown format: try a normal pull, which might still work.
            return RunGitCommand(path, "pull");
        }

        private static string GetSelectedPath()
        {
            string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(assetPath)) return null;
            string fullPath = Path.GetFullPath(assetPath);
            return Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        }

        internal static bool IsGitRepositoryRoot(string directory)
        {
            string gitDirectory = Path.Combine(directory, ".git");
            return Directory.Exists(gitDirectory) || File.Exists(gitDirectory);
        }

        internal static string FindAncestorGitRepoRoot(string startDir)
        {
            try
            {
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string directory = Path.GetFullPath(startDir);
                while (!string.IsNullOrEmpty(directory) && !seen.Contains(directory))
                {
                    if (IsGitRepositoryRoot(directory)) return directory;
                    seen.Add(directory);
                    var parent = Directory.GetParent(directory);
                    if (parent == null) break;
                    directory = parent.FullName;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Git Sync] Failed to find ancestor repo root from '{startDir}': {e.Message}");
            }
            return null;
        }

        internal static string TrimToSingleLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Replace("\r", "");
            var index = value.IndexOf('\n');
            return index >= 0 ? value.Substring(0, index) : value;
        }
    }
}
#endif