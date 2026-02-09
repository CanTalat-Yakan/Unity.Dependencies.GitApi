#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityEssentials
{
    public partial class GitFolderSynchronizer
    {
        [MenuItem("Assets/Git/Fetch and Pull", true)]
        public static bool ValidateGitFetchPull()
        {
            string path = GetSelectedPath();
            return !string.IsNullOrEmpty(path) && Directory.Exists(Path.Combine(path, ".git"));
        }

        [MenuItem("Assets/Git/Fetch and Pull", priority = 2011)]
        public static void FetchPull()
        {
            string path = GetSelectedPath();
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[Git] No repository selected.");
                return;
            }

            StartProgress("Git Fetch & Pull", report =>
            {
                report("Fetching from remote...", 0.2f);
                var (_, fetchErr, fetchCode) = RunGitCommand(path, "fetch");
                if (fetchCode != 0)
                {
                    Debug.LogError($"[Git] Fetch failed: {fetchErr}");
                    return;
                }

                report("Checking tracking status...", 0.5f);
                bool isBehind = CheckIfBehind(path);

                if (isBehind)
                {
                    report("Pulling changes...", 0.8f);
                    var (pullOut, pullErr, pullCode) = RunGitCommand(path, "pull");
                    if (pullCode != 0)
                        Debug.LogError($"[Git] Pull failed: {pullErr}");
                    else if (!string.IsNullOrEmpty(pullOut))
                        Debug.Log($"[Git] Successfully pulled changes:\n{pullOut}");
                }

                report("Done", 1f);
            },
            onComplete: () => { });
        }

        private static bool CheckIfBehind(string repositoryPath)
        {
            var (output, error, exitCode) = RunGitCommand(repositoryPath, "status --porcelain -b");
            if (exitCode != 0)
            {
                Debug.LogError("[Git] Status check failed: " + error);
                return false;
            }

            string[] lines = output.Split('\n');
            return lines.Length > 0 && lines[0].Contains("[behind");
        }
    }
}
#endif