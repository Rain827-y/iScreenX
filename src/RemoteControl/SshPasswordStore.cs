using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

internal static class SshPasswordStore
{
    const string KeyPath = @"Software\iScreenX";
    const string ValueName = "SshRootPassword";

    internal static string Load()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath)) {
            if (key == null) return "";
            var value = key.GetValue(ValueName);
            if (value == null) return "";
            byte[] plain = ProtectedData.Unprotect((byte[])value, null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
    }

    internal static void Save(string password)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath)) {
            if (password.Length == 0) {
                key.DeleteValue(ValueName, false);
                return;
            }
            byte[] plain = Encoding.UTF8.GetBytes(password);
            try {
                byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                key.SetValue(ValueName, encrypted, RegistryValueKind.Binary);
            } finally { Array.Clear(plain, 0, plain.Length); }
        }
    }
}
