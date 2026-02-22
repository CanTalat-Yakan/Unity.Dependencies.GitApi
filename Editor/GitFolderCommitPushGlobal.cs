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
        private const string MenuTitle = "Git Commit & Push All Changes";
        private const string ReportSectionTitle = "Per-Repository Summary:";

        [MenuItem("Tools/" + MenuTitle, priority = -9000)]
        public static void CommitAndPushAllChanges()
        {
            string assetsPath = Application.dataPath; // absolute path to Assets/

            // Gather all git repositories under Assets/ recursively (skip descending into a repo once found)
            List<string> repositoryRoots = new List<string>();
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
                        // Do not descend into this repo to avoid nested traversal; submodules are separate repos and will be picked if scanning starts from their root
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
                EditorUtility.DisplayDialog(MenuTitle, "Failed to scan Assets/. See Console for details.", "OK");
                return;
            }

            // Also include the ancestor repo that contains the Assets/ folder (the Unity project root repo)
            string ancestorRepository = FindAncestorGitRepoRoot(assetsPath);
            if (!string.IsNullOrEmpty(ancestorRepository))
            {
                // Only add if it's not already one of the repos we found beneath Assets/
                bool alreadyIncluded = repositoryRoots.Exists(r => string.Equals(Path.GetFullPath(r), Path.GetFullPath(ancestorRepository), StringComparison.OrdinalIgnoreCase));
                if (!alreadyIncluded)
                {
                    // Process sub-repositories first, then the root repo last, so submodule pointer updates can be captured.
                    repositoryRoots.Add(ancestorRepository);
                }
            }

            if (repositoryRoots.Count == 0)
            {
                EditorUtility.DisplayDialog(MenuTitle, "No git repositories found under Assets/.", "OK");
                return;
            }

            // Token is required
            string token = EditorPrefs.GetString(TokenKey, string.Empty);
            if (string.IsNullOrEmpty(token))
            {
                bool shouldContinue = EditorUtility.DisplayDialog(MenuTitle, "No Git token found. Please open 'Assets/Git Commit and Push' once to set your token, then retry.", "OK", "Cancel");
                if (!shouldContinue) return;
            }

            // Run work in background with progress bar on main thread
            int processed = 0;
            int committed = 0;
            int pushed = 0;
            var sbReport = new StringBuilder();

            StartProgress(MenuTitle, report =>
            {
                int total = repositoryRoots.Count;
                for (int i = 0; i < total; i++)
                {
                    string dir = repositoryRoots[i];
                    string folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName)) folderName = dir;

                    // Count this repository as processed regardless of outcome
                    processed++;

                    float Base(int step, int steps) => Math.Clamp((i + (step / (float)steps)) / Math.Max(1, total), 0f, 1f);

                    // Evaluate repo state
                    report($"{folderName}: checking status…", Base(1, 6));
                    bool hasUncommitted = HasUncommittedChanges(dir);
                    bool hasAheadOnly = !hasUncommitted && HasUnpushedCommits(dir);

                    if (!hasUncommitted && !hasAheadOnly)
                    {
                        sbReport.AppendLine($"- [No Changes] {folderName}");
                        continue;
                    }

                    // If uncommitted, commit with invisible characters
                    if (hasUncommitted)
                    {
                        report($"{folderName}: staging & committing (empty message)…", Base(3, 6));
                        try
                        {
                            // Reuse the editor's Commit helper which does `add .` and uses invisible-message placeholder
                            Commit(dir, "");
                            committed++;
                        }
                        catch (Exception ex)
                        {
                            sbReport.AppendLine($"- [Commit Failed] {folderName}: {TrimToSingleLine(ex.Message)}");
                            Debug.LogError($"[Git Sync] {folderName}: commit failed: {ex}");
                            continue;
                        }
                    }

                    // Push using the same method the editor window uses (token auth), regardless of upstream
                    report($"{folderName}: pushing to remote…", Base(5, 6));
                    var pushResult = RunPushGitCommand(dir, token);
                    if (pushResult.exitCode != 0)
                    {
                        sbReport.AppendLine($"- [Push Failed] {folderName}: {TrimToSingleLine(pushResult.error)}");
                        Debug.LogError($"[Git Sync] {folderName}: push failed\nSTDERR: {pushResult.error}\nSTDOUT: {pushResult.output}");
                        continue;
                    }

                    RunGitCommand(dir, "fetch");

                    pushed++;
                    var label = hasUncommitted ? "Committed and Pushed" : "Pushed";
                    sbReport.AppendLine($"- [{label}] {folderName}");
                }
            },
            onComplete: () =>
            {
                string summary = $"Processed: {processed}, Repositories Found: {repositoryRoots.Count}, Committed: {committed}, Pushed: {pushed}";
                Debug.Log($"[Git Sync] {summary}\n{ReportSectionTitle}\n{sbReport}");
            });
        }
    }
}
#endif