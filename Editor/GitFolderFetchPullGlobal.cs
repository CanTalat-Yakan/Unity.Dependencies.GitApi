#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityEssentials
{
    public partial class GitFolderSynchronizer
    {
        private const string FetchPullMenuTitle = "Git Fetch & Pull All Changes";
        private const string FetchPullReportSectionTitle = "Per-Repository Summary:";

        [MenuItem("Tools/" + FetchPullMenuTitle, priority = -9001)]
        public static void FetchAndPullAllChanges()
        {
            string assetsPath = Application.dataPath; // absolute path to Assets/

            // Gather all git repositories under Assets/ recursively (skip descending into a repo once found)
            List<string> repositoryRoots = new();
            try
            {
                var stack = new Stack<string>();
                stack.Push(assetsPath);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();

                    // Skip .git directories explicitly
                    var dirName = Path.GetFileName(dir);
                    if (string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (IsGitRepositoryRoot(dir))
                    {
                        repositoryRoots.Add(dir);
                        continue;
                    }

                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dir))
                        {
                            // Avoid diving into hidden folders starting with '.' to reduce noise
                            var name = Path.GetFileName(sub);
                            if (!string.IsNullOrEmpty(name) && name.StartsWith(".")) continue;
                            stack.Push(sub);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Git Sync] Failed to enumerate '{dir}': {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Git Sync] Failed to scan Assets/: {e.Message}");
                EditorUtility.DisplayDialog(FetchPullMenuTitle, "Failed to scan Assets/. See Console for details.", "OK");
                return;
            }

            // Also include the ancestor repo that contains the Assets/ folder (the Unity project root repo)
            string ancestorRepository = FindAncestorGitRepoRoot(assetsPath);
            if (!string.IsNullOrEmpty(ancestorRepository))
            {
                bool alreadyIncluded = repositoryRoots.Exists(r => string.Equals(Path.GetFullPath(r), Path.GetFullPath(ancestorRepository), StringComparison.OrdinalIgnoreCase));
                if (!alreadyIncluded)
                {
                    // Process sub-repositories first, then the root repo last.
                    repositoryRoots.Add(ancestorRepository);
                }
            }

            if (repositoryRoots.Count == 0)
            {
                EditorUtility.DisplayDialog(FetchPullMenuTitle, "No git repositories found under Assets/.", "OK");
                return;
            }

            // Token is optional for fetch/pull (public repos, SSH, etc). We'll use it if present.
            string token = EditorPrefs.GetString(TokenKey, string.Empty);

            int processed = 0;
            int fetched = 0;
            int pulled = 0;
            int skipped = 0;
            var sbReport = new StringBuilder();

            StartProgress(FetchPullMenuTitle, report =>
            {
                int total = repositoryRoots.Count;
                for (int i = 0; i < total; i++)
                {
                    string dir = repositoryRoots[i];
                    string folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName)) folderName = dir;

                    processed++;

                    float Base(int step, int steps) => Math.Clamp((i + (step / (float)steps)) / Math.Max(1, total), 0f, 1f);

                    // Fetch
                    report($"{folderName}: fetching…", Base(1, 6));
                    var (_, fetchErr, fetchCode) = RunGitCommand(dir, "fetch");
                    if (fetchCode != 0)
                    {
                        sbReport.AppendLine($"- [Fetch Failed] {folderName}: {TrimToSingleLine(fetchErr)}");
                        Debug.LogError($"[Git Sync] {folderName}: fetch failed: {fetchErr}");
                        continue;
                    }
                    fetched++;

                    // Check behind
                    report($"{folderName}: checking tracking status…", Base(3, 6));
                    var behindState = GetBehindState(dir);
                    if (behindState == BehindState.Unknown)
                    {
                        skipped++;
                        sbReport.AppendLine($"- [Skipped] {folderName}: unable to determine upstream/behind state");
                        continue;
                    }

                    if (behindState == BehindState.NotBehind)
                    {
                        skipped++;
                        sbReport.AppendLine($"- [Up To Date] {folderName}");
                        continue;
                    }

                    // Pull
                    report($"{folderName}: pulling…", Base(5, 6));

                    // Avoid pulling into a dirty working tree (common failure mode)
                    if (HasUncommittedChanges(dir))
                    {
                        skipped++;
                        sbReport.AppendLine($"- [Skipped] {folderName}: has uncommitted changes");
                        continue;
                    }

                    var pullResult = RunPullGitCommand(dir, token);
                    if (pullResult.exitCode != 0)
                    {
                        sbReport.AppendLine($"- [Pull Failed] {folderName}: {TrimToSingleLine(pullResult.error)}");
                        Debug.LogError($"[Git Sync] {folderName}: pull failed\nSTDERR: {pullResult.error}\nSTDOUT: {pullResult.output}");
                        continue;
                    }

                    pulled++;
                    sbReport.AppendLine($"- [Pulled] {folderName}");
                }
            },
            onComplete: () =>
            {
                string summary = $"Processed: {processed}, Repositories Found: {repositoryRoots.Count}, Fetched: {fetched}, Pulled: {pulled}, Skipped: {skipped}";
                Debug.Log($"[Git Sync] {summary}\n{FetchPullReportSectionTitle}\n{sbReport}");
            });
        }

        private enum BehindState
        {
            Unknown,
            NotBehind,
            Behind
        }

        private static BehindState GetBehindState(string repositoryPath)
        {
            var (output, error, exitCode) = RunGitCommand(repositoryPath, "status --porcelain -b");
            if (exitCode != 0)
            {
                Debug.LogError("[Git] Status check failed: " + error);
                return BehindState.Unknown;
            }

            if (string.IsNullOrWhiteSpace(output))
                return BehindState.Unknown;

            string firstLine = output.Split('\n')[0];

            // If no upstream is set, git prints e.g. "## main" (no ...) or "## main...origin/main".
            if (!firstLine.Contains("..."))
                return BehindState.Unknown;

            return firstLine.Contains("[behind") ? BehindState.Behind : BehindState.NotBehind;
        }
    }
}
#endif

