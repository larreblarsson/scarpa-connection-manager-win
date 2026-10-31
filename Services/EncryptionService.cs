using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ScarpaConnectionManager.Services;

public static class EncryptionService
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 600000;

    // Generates a 256-bit key from the Master Password
    private static byte[] DeriveKey(string masterPassword, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(masterPassword, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    public static byte[] Encrypt(string plainText, string masterPassword)
    {
        byte[] salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] key = DeriveKey(masterPassword, salt);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        // Bundle everything into a single payload: Salt + Nonce + Tag + Ciphertext
        using var ms = new MemoryStream();
        ms.Write(salt, 0, salt.Length);
        ms.Write(nonce, 0, nonce.Length);
        ms.Write(tag, 0, tag.Length);
        ms.Write(cipherBytes, 0, cipherBytes.Length);
        return ms.ToArray();
    }

    public static string Decrypt(byte[] encryptedData, string masterPassword)
    {
        using var ms = new MemoryStream(encryptedData);

        byte[] salt = new byte[SaltSize];
        ms.Read(salt, 0, SaltSize);

        byte[] nonce = new byte[NonceSize];
        ms.Read(nonce, 0, NonceSize);

        byte[] tag = new byte[TagSize];
        ms.Read(tag, 0, TagSize);

        byte[] cipherBytes = new byte[(int)(ms.Length - ms.Position)];
        ms.Read(cipherBytes, 0, cipherBytes.Length);

        byte[] key = DeriveKey(masterPassword, salt);
        byte[] plainBytes = new byte[cipherBytes.Length];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }

        return Encoding.UTF8.GetString(plainBytes);
    }
}