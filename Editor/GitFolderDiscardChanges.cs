#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityEssentials
{
    public partial class GitApi
    {
        [MenuItem("Assets/Git/Discard Changes", true)]
        public static bool ValidateGitDiscardChanges()
        {
            string path = GetSelectedPath();
            return !string.IsNullOrEmpty(path)
                   && Directory.Exists(Path.Combine(path, ".git"))
                   && HasUncommittedChanges(path);
        }

        [MenuItem("Assets/Git/Discard Changes", priority = 2012)]
        public static void DiscardChanges()
        {
            string path = GetSelectedPath();
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[Git] No repository selected.");
                return;
            }

            string folderName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(folderName))
                folderName = path;

            bool confirm = EditorUtility.DisplayDialog(
                "Git Discard Changes",
                $"Discard all local changes in '{folderName}'?\n\nThis will reset tracked files and remove untracked files.",
                "Discard",
                "Cancel");

            if (!confirm)
                return;

            StartProgress("Git Discard Changes", report =>
            {
                report("Discarding tracked changes...", 0.45f);
                var (_, resetErr, resetCode) = RunGitCommand(path, "reset --hard HEAD");
                if (resetCode != 0)
                {
                    Debug.LogError($"[Git] Reset failed: {resetErr}");
                    return;
                }

                report("Removing untracked files...", 0.8f);
                var (_, cleanErr, cleanCode) = RunGitCommand(path, "clean -fd");
                if (cleanCode != 0)
                {
                    Debug.LogError($"[Git] Clean failed: {cleanErr}");
                    return;
                }

                report("Done", 1f);
            },
            onComplete: () =>
            {
                Debug.Log($"[Git] Discarded local changes for '{folderName}'.");
            });
        }
    }
}
#endif