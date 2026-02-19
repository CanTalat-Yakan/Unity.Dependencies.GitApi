#if UNITY_EDITOR
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityEssentials
{
    public partial class GitFolderSynchronizer
    {
        // Tries to bump package.json version (patch +1) in repo root.
        // Returns true if version was updated and written.
        private static bool TryBumpPackageJsonPatchVersion(string repositoryRoot, out string oldVersion, out string newVersion)
        {
            oldVersion = null;
            newVersion = null;

            if (string.IsNullOrEmpty(repositoryRoot)) return false;

            string packageJsonPath = Path.Combine(repositoryRoot, "package.json");
            if (!File.Exists(packageJsonPath))
                return false;

            try
            {
                string json = File.ReadAllText(packageJsonPath);

                // Minimal, dependency-free JSON edit:
                // Find: "version" : "<semver>" where semver is strictly X.Y.Z (no prerelease/build).
                // We skip more complex formats on purpose to avoid unintended edits.
                var regex = new Regex("\\\"version\\\"\\s*:\\s*\\\"(?<ver>(?<maj>\\d+)\\.(?<min>\\d+)\\.(?<pat>\\d+))\\\"", RegexOptions.CultureInvariant);
                var match = regex.Match(json);
                if (!match.Success)
                {
                    Debug.LogWarning($"[Git] package.json found but no simple semver 'version' field (X.Y.Z). Skipping bump at: {packageJsonPath}");
                    return false;
                }

                oldVersion = match.Groups["ver"].Value;
                int maj = int.Parse(match.Groups["maj"].Value);
                int min = int.Parse(match.Groups["min"].Value);
                int pat = int.Parse(match.Groups["pat"].Value);
                newVersion = $"{maj}.{min}.{pat + 1}";

                // Replace only the captured version value.
                string updatedJson = json.Substring(0, match.Groups["ver"].Index)
                                     + newVersion
                                     + json.Substring(match.Groups["ver"].Index + match.Groups["ver"].Length);

                if (string.Equals(updatedJson, json, StringComparison.Ordinal))
                    return false;

                File.WriteAllText(packageJsonPath, updatedJson);
                // Debug.Log($"[Git] Bumped package.json version {oldVersion} -> {newVersion} ({packageJsonPath})");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Git] Failed to bump package.json version at '{packageJsonPath}': {ex.Message}");
                return false;
            }
        }
    }
}
#endif

