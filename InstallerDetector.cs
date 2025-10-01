using System;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace WinUIMetadataScraper
{
    public static class InstallerDetector
    {
        /// <summary>
        /// Determines if the given file is likely an installer.
        /// </summary>
        /// <param name="filePath">The path to the .exe file.</param>
        /// <returns>True if the file is likely an installer, otherwise false.</returns>
        public static bool IsInstaller(string filePath)
        {
            if (!File.Exists(filePath) || Path.GetExtension(filePath).ToLower() != ".exe")
            {
                return false; // Not a valid .exe file
            }

            try
            {
                // Check file metadata
                var fileVersionInfo = FileVersionInfo.GetVersionInfo(filePath);
                if (IsMetadataIndicatingInstaller(fileVersionInfo))
                {
                    return true;
                }

                // Check file name
                if (IsFileNameIndicatingInstaller(filePath))
                {
                    return true;
                }

                // Additional checks (e.g., PE sections) can be added here

                return false; // Default to not an installer
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error detecting installer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if the file metadata indicates it is an installer.
        /// </summary>
        private static bool IsMetadataIndicatingInstaller(FileVersionInfo fileVersionInfo)
        {
            var keywords = new[] { "installer", "setup", "installshield", "update", "patch" };
            return keywords.Any(keyword =>
                fileVersionInfo.FileDescription?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                fileVersionInfo.ProductName?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Checks if the file name indicates it is an installer.
        /// </summary>
        private static bool IsFileNameIndicatingInstaller(string filePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath).ToLower();
            var keywords = new[] { "setup", "install", "update", "patch" };
            return keywords.Any(keyword => fileName.Contains(keyword));
        }
    }
}