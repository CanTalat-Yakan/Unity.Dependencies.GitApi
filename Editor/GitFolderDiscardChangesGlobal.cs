#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityEssentials
{
    public partial class GitApi
    {
        private const string DiscardMenuTitle = "Git Discard All Changes";
        private const string DiscardReportSectionTitle = "Per-Repository Summary:";

        [MenuItem("Tools/" + DiscardMenuTitle, priority = -8999)]
        public static void DiscardAllChanges()
        {
            bool confirmAll = EditorUtility.DisplayDialog(
                DiscardMenuTitle,
                "Discard all local changes across all repositories under Assets/?\n\nThis resets tracked files and removes untracked files.",
                "Discard All",
                "Cancel");

            if (!confirmAll)
                return;

            string assetsPath = Application.dataPath;

            List<string> repositoryRoots = new();
            try
            {
                var stack = new Stack<string>();
                stack.Push(assetsPath);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();

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
                EditorUtility.DisplayDialog(DiscardMenuTitle, "Failed to scan Assets/. See Console for details.", "OK");
                return;
            }

            string ancestorRepository = FindAncestorGitRepoRoot(assetsPath);
            if (!string.IsNullOrEmpty(ancestorRepository))
            {
                bool alreadyIncluded = repositoryRoots.Exists(r => string.Equals(Path.GetFullPath(r), Path.GetFullPath(ancestorRepository), StringComparison.OrdinalIgnoreCase));
                if (!alreadyIncluded)
                {
                    repositoryRoots.Add(ancestorRepository);
                }
            }

            if (repositoryRoots.Count == 0)
            {
                EditorUtility.DisplayDialog(DiscardMenuTitle, "No git repositories found under Assets/.", "OK");
                return;
            }

            int processed = 0;
            int discarded = 0;
            int skipped = 0;
            var sbReport = new StringBuilder();

            StartProgress(DiscardMenuTitle, report =>
            {
                int total = repositoryRoots.Count;
                for (int i = 0; i < total; i++)
                {
                    string dir = repositoryRoots[i];
                    string folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName)) folderName = dir;

                    processed++;

                    float Base(int step, int steps) => Math.Clamp((i + (step / (float)steps)) / Math.Max(1, total), 0f, 1f);

                    report($"{folderName}: checking status...", Base(1, 6));
                    if (!HasUncommittedChanges(dir))
                    {
                        skipped++;
                        sbReport.AppendLine($"- [No Changes] {folderName}");
                        continue;
                    }

                    report($"{folderName}: resetting tracked files...", Base(3, 6));
                    var (_, resetErr, resetCode) = RunGitCommand(dir, "reset --hard HEAD");
                    if (resetCode != 0)
                    {
                        skipped++;
                        sbReport.AppendLine($"- [Reset Failed] {folderName}: {TrimToSingleLine(resetErr)}");
                        Debug.LogError($"[Git Sync] {folderName}: reset failed: {resetErr}");
                        continue;
                    }

                    report($"{folderName}: removing untracked files...", Base(5, 6));
                    var (_, cleanErr, cleanCode) = RunGitCommand(dir, "clean -fd");
                    if (cleanCode != 0)
                    {
                        skipped++;
                        sbReport.AppendLine($"- [Clean Failed] {folderName}: {TrimToSingleLine(cleanErr)}");
                        Debug.LogError($"[Git Sync] {folderName}: clean failed: {cleanErr}");
                        continue;
                    }

                    discarded++;
                    sbReport.AppendLine($"- [Discarded] {folderName}");
                }
            },
            onComplete: () =>
            {
                string summary = $"Processed: {processed}, Repositories Found: {repositoryRoots.Count}, Discarded: {discarded}, Skipped: {skipped}";
                Debug.Log($"[Git Sync] {summary}\n{DiscardReportSectionTitle}\n{sbReport}");
            });
        }
    }
}
#endif