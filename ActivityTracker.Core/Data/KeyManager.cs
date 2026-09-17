using System;
using System.IO;
using System.Security.Cryptography;

namespace ActivityTracker.Core.Data;

public static class KeyManager
{
    private static readonly string KeyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ActivityTracker",
        "db.key");

    public static string GetOrGenerateKey()
    {
        if (!File.Exists(KeyFilePath))
        {
            var directory = Path.GetDirectoryName(KeyFilePath);
            if (!Directory.Exists(directory) && directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            byte[] newKey = new byte[32]; // 256-bit key
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(newKey);
            }

            byte[] protectedKey = ProtectedData.Protect(newKey, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(KeyFilePath, protectedKey);
        }

        byte[] encryptedKey = File.ReadAllBytes(KeyFilePath);
        byte[] decryptedKey = ProtectedData.Unprotect(encryptedKey, null, DataProtectionScope.CurrentUser);
        
        return Convert.ToHexString(decryptedKey);
    }
}
