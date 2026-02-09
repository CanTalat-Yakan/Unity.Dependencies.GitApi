#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityEssentials
{
    public partial class GitFolderSynchronizer
    {
        public EditorWindowBuilder Window;
        public Action Repaint;
        public Action Close;

        public string Token;
        public List<string> ChangedFiles = new();
        public List<string> UnpushedCommits = new();

        private string _commitMessage = string.Empty;
        private string _tokenPlaceholder = string.Empty;

        private void RefreshState(string path)
        {
            ChangedFiles = GetChangedFiles(path);
            UnpushedCommits = GetUnpushedCommitSummaries(path);
        }

        public void Header()
        {
            string path = GetSelectedPath();

            // If no token, prompt for token entry
            if (string.IsNullOrEmpty(Token))
            {
                GUILayout.Label("Enter your Git Token:");
                _tokenPlaceholder = EditorGUILayout.PasswordField(_tokenPlaceholder, EditorStyles.toolbarTextField);

                if (GUILayout.Button("Save Token", EditorStyles.toolbarButton))
                {
                    Token = _tokenPlaceholder;
                    EditorPrefs.SetString(TokenKey, Token);
                    Repaint?.Invoke();
                }
                return;
            }

            bool hasUncommitted = ChangedFiles != null && ChangedFiles.Count > 0;
            bool hasUnpushedOnly = !hasUncommitted && (UnpushedCommits?.Count ?? 0) > 0;

            // Token exists: context-aware toolbar
            if (hasUncommitted)
            {
                GUILayout.Label("Commit Message:", EditorStyles.label, GUILayout.Width(110));
                _commitMessage = EditorGUILayout.TextField(_commitMessage, EditorStyles.toolbarTextField);
            }
            else if (hasUnpushedOnly)
            {
                GUILayout.Label($"Ahead by {(UnpushedCommits?.Count ?? 0)} commit(s)", EditorStyles.boldLabel);
            }
            else
            {
                GUILayout.Label("Working tree clean", EditorStyles.miniBoldLabel);
            }

            if (GUILayout.Button("Fetch and Pull", EditorStyles.toolbarButton))
            {
                StartProgress("Git Fetch & Pull", report =>
                {
                    if (string.IsNullOrEmpty(path)) return;

                    report("Fetching from remote...", 0.2f);
                    var (_, fetchErr, fetchCode) = RunGitCommand(path, "fetch");

                    report("Checking tracking status...", 0.4f);
                    bool isBehind = CheckIfBehind(path);

                    if (fetchCode != 0)
                    {
                        Debug.LogError($"[Git] Fetch failed: {fetchErr}");
                        return;
                    }

                    if (isBehind)
                    {
                        report("Pulling changes...", 0.7f);
                        var (pullOut, pullErr, pullCode) = RunGitCommand(path, "pull");
                        if (pullCode != 0)
                            Debug.LogError($"[Git] Pull failed: {pullErr}");
                        else
                            Debug.Log($"[Git] Successfully pulled changes:\n{pullOut}");
                    }

                    report("Done", 1f);
                },
                onComplete: () => { RefreshState(path); Repaint?.Invoke(); });
            }

            if (hasUnpushedOnly)
            {
                if (GUILayout.Button("Push", EditorStyles.toolbarButton))
                {
                    var capturedToken = Token; // capture on main thread
                    StartProgress("Git Push", report =>
                    {
                        if (string.IsNullOrEmpty(path)) return;

                        report("Pushing to remote...", 0.6f);
                        var (pushOut, pushErr, pushCode) = RunPushGitCommand(path, capturedToken);
                        if (pushCode != 0)
                            Debug.LogError($"[Git] Push failed: {pushErr}");
                        else if (!string.IsNullOrEmpty(pushOut))
                            Debug.Log("[Git] " + pushOut);

                        report("Updating tracking info...", 0.9f);
                        RunGitCommand(path, "fetch");

                        report("Done", 1f);
                    },
                    onComplete: () =>
                    {
                        RefreshState(path);
                        if ((UnpushedCommits?.Count ?? 0) == 0 && ChangedFiles.Count == 0)
                            Close?.Invoke();
                        else
                            Repaint?.Invoke();
                    });
                }
            }

            if (GUILayout.Button("Change Token", EditorStyles.toolbarButton))
            {
                Token = string.Empty;
                EditorPrefs.DeleteKey(TokenKey);
                _tokenPlaceholder = string.Empty;
                Repaint?.Invoke();
            }
        }

        public void Body()
        {
            // If no token, show guidance
            if (string.IsNullOrEmpty(Token))
            {
                EditorGUILayout.HelpBox(
                    "To push to remote over HTTPS, you need a Personal Access Token (PAT) stored in the editor.",
                    MessageType.Info);
                EditorGUILayout.LabelField(
                    "You can create either a Fine-grained or Classic token. When creating the token:\n\n" +
                    "• Allow access to the repository you plan to push to.\n\n" +
                    "• Ensure 'Contents' permission includes 'Read and write' to allow commits and pushes.\n\n" +
                    "After generating the token, paste it in the header above and save it.", EditorStyles.wordWrappedLabel);
                if (EditorGUILayout.LinkButton("Create Personal Access Tokens"))
                    Application.OpenURL("https://github.com/settings/personal-access-tokens");
                return;
            }

            bool hasUncommitted = ChangedFiles != null && ChangedFiles.Count > 0;
            bool hasUnpushedOnly = !hasUncommitted && (UnpushedCommits?.Count ?? 0) > 0;

            if (hasUncommitted)
            {
                GUILayout.Label("Changed Files:", EditorStyles.boldLabel);

                foreach (string file in ChangedFiles)
                    EditorGUILayout.LabelField(file);
            }
            else if (hasUnpushedOnly)
            {
                GUILayout.Label("Unpushed Commits:", EditorStyles.boldLabel);
                foreach (string line in UnpushedCommits)
                    EditorGUILayout.LabelField(line);
            }
            else
            {
                EditorGUILayout.LabelField("No uncommitted changes and nothing to push.");
            }
        }

        public void Footer()
        {
            // If no token, no actions
            if (string.IsNullOrEmpty(Token))
                return;

            string path = GetSelectedPath();
            if (string.IsNullOrEmpty(path))
                return;

            bool hasUncommitted = ChangedFiles != null && ChangedFiles.Count > 0;
            bool hasUnpushedOnly = !hasUncommitted && (UnpushedCommits?.Count ?? 0) > 0;

            if (hasUncommitted)
                GUILayout.Label($"Total Changes: {ChangedFiles.Count}", EditorStyles.miniBoldLabel);
            else if (hasUnpushedOnly)
                GUILayout.Label($"Ahead by {(UnpushedCommits?.Count ?? 0)} commit(s)", EditorStyles.miniBoldLabel);

            int assetsIndex = path.IndexOf("Assets", StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0)
                EditorGUILayout.HelpBox("Git Repository Path: \n" + path.Substring(assetsIndex), MessageType.Info);
            else
                EditorGUILayout.HelpBox("Git Repository Path: \n" + path, MessageType.Info);

            if (hasUncommitted)
            {
                GUI.enabled = ChangedFiles.Count > 0;
                if (GUILayout.Button("Commit and Push"))
                {
                    string message = _commitMessage; // capture
                    var capturedToken = Token; // capture token on main thread
                    StartProgress("Commit and Push", report =>
                    {
                        if (string.IsNullOrEmpty(path)) return;

                        report("Staging changes...", 0.2f);
                        RunGitCommand(path, "add .");

                        // Commit with empty-message placeholder if needed
                        const string EmptyCommitMessage = "⠀⠀⠀⠀⠀";
                        bool emptyCommitMessage = string.IsNullOrEmpty(message);
                        string effectiveMessage = emptyCommitMessage ? EmptyCommitMessage : message;

                        report("Creating commit...", 0.45f);
                        var (commitOut, commitErr, commitCode) = RunGitCommand(path, $"commit -m \"{effectiveMessage}\"");
                        if (emptyCommitMessage && !string.IsNullOrEmpty(commitOut) && commitOut.Length >= 30)
                        {
                            // Mirror original output post-processing
                            commitOut = commitOut.Remove(15, 15);
                        }
                        if (commitCode != 0 && !string.IsNullOrEmpty(commitErr))
                            Debug.LogError("[Git] " + commitErr);
                        else if (!string.IsNullOrEmpty(commitOut))
                            Debug.Log("[Git] " + commitOut);

                        report("Pushing to remote...", 0.7f);
                        var (pushOut, pushErr, pushCode) = RunPushGitCommand(path, capturedToken);
                        if (pushCode != 0 && !string.IsNullOrEmpty(pushErr))
                            Debug.LogError("[Git] " + pushErr);
                        else if (!string.IsNullOrEmpty(pushOut))
                            Debug.Log("[Git] " + pushOut);

                        report("Updating tracking info...", 0.9f);
                        RunGitCommand(path, "fetch");

                        report("Done", 1f);
                    },
                    onComplete: () =>
                    {
                        RefreshState(path);
                        if ((UnpushedCommits?.Count ?? 0) == 0 && ChangedFiles.Count == 0)
                            Close?.Invoke();
                        else
                            Repaint?.Invoke();
                    });
                }
                GUI.enabled = true;
            }
            else if (hasUnpushedOnly)
            {
                if (GUILayout.Button($"Push {(UnpushedCommits?.Count ?? 0)} Commit(s)"))
                {
                    var capturedToken = Token; // capture token on main thread
                    StartProgress("Git Push", report =>
                    {
                        report("Pushing to remote...", 0.6f);
                        var (pushOut, pushErr, pushCode) = RunPushGitCommand(path, capturedToken);
                        if (pushCode != 0)
                            Debug.LogError("[Git] " + pushErr);
                        else if (!string.IsNullOrEmpty(pushOut))
                            Debug.Log("[Git] " + pushOut);

                        report("Updating tracking info...", 0.9f);
                        RunGitCommand(path, "fetch");

                        report("Done", 1f);
                    },
                    onComplete: () =>
                    {
                        RefreshState(path);
                        if ((UnpushedCommits?.Count ?? 0) == 0)
                            Close?.Invoke();
                        else
                            Repaint?.Invoke();
                    });
                }
            }
        }
    }
}
#endif