using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Potok.Backend.Infrastructure.Security;

public static class TokenEncryptor
{
    private static readonly string KeyFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "encryption.key");
    private static readonly byte[] Key;

    static TokenEncryptor()
    {
        try
        {
            var directory = Path.GetDirectoryName(KeyFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(KeyFilePath))
            {
                var hexKey = File.ReadAllText(KeyFilePath).Trim();
                Key = Convert.FromHexString(hexKey);
            }
            else
            {
                Key = RandomNumberGenerator.GetBytes(32); // 256-bit key
                File.WriteAllText(KeyFilePath, Convert.ToHexString(Key));
            }
        }
        catch (Exception ex)
        {
            // Fallback to in-memory key if file operations fail (though in production we want persistence)
            Console.WriteLine($"[TokenEncryptor] Error initializing encryption key file: {ex.Message}. Falling back to temporary in-memory key.");
            Key = RandomNumberGenerator.GetBytes(32);
        }
    }

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;

        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV(); // Generates a cryptographically strong random IV

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        
        // Write the IV first to the stream so we can retrieve it during decryption
        ms.Write(aes.IV, 0, aes.IV.Length);

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            cs.Write(plainBytes, 0, plainBytes.Length);
            cs.FlushFinalBlock();
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        try
        {
            byte[] fullCipher = Convert.FromBase64String(cipherText);

            using var aes = Aes.Create();
            aes.Key = Key;

            byte[] iv = new byte[aes.BlockSize / 8]; // 16 bytes for AES-256
            byte[] cipherBytes = new byte[fullCipher.Length - iv.Length];

            Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(fullCipher, iv.Length, cipherBytes, 0, cipherBytes.Length);

            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(cipherBytes);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var reader = new StreamReader(cs, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TokenEncryptor] Decryption failed: {ex.Message}");
            return string.Empty;
        }
    }
}
