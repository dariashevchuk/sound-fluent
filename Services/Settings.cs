using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundFluent.Services;

public sealed class Settings
{
    /// <summary>Model id sent to the API. Check the current list before changing.</summary>
    public string Model { get; set; } = "gpt-5.6-terra";

    /// <summary>Send the clipboard text the moment the window opens.</summary>
    public bool AutoSend { get; set; } = true;

    /// <summary>Keep the window above other windows.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Copy the reply to the clipboard the moment it lands.</summary>
    public bool AutoCopy { get; set; } = true;

    /// <summary>DPAPI blob, base64. Readable only by this Windows user account.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>Environment variable checked before the stored key.</summary>
    public const string ApiKeyVariable = "OPENAI_API_KEY";

    /// <summary>
    /// Resolution order: environment variable, then the encrypted store. The
    /// variable wins so you can point the app at a different key without
    /// touching settings, and so a machine that has one set never needs the
    /// dialog at all.
    /// </summary>
    [JsonIgnore]
    public string? ApiKey => ReadEnvironment() ?? ReadStored();

    /// <summary>True when the key is coming from the environment, not the file.</summary>
    [JsonIgnore]
    public bool ApiKeyIsFromEnvironment => ReadEnvironment() is not null;

    /// <summary>
    /// Checks the process environment, then the user and machine scopes. Reading
    /// the wider scopes means a key set with `setx` is picked up without
    /// restarting the app.
    /// </summary>
    private static string? ReadEnvironment()
    {
        EnvironmentVariableTarget[] scopes =
        {
            EnvironmentVariableTarget.Process,
            EnvironmentVariableTarget.User,
            EnvironmentVariableTarget.Machine
        };

        foreach (EnvironmentVariableTarget scope in scopes)
        {
            try
            {
                string? value = Environment.GetEnvironmentVariable(ApiKeyVariable, scope);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            catch (PlatformNotSupportedException)
            {
                // User and Machine scopes are Windows-only. Process scope always works.
            }
        }

        return null;
    }

    private string? ReadStored()
    {
        if (string.IsNullOrEmpty(ProtectedApiKey)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(ProtectedApiKey), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Copied from another machine or another user account: unrecoverable.
            return null;
        }
    }

    /// <summary>Encrypts and stores a key. Call <see cref="Save"/> afterwards.</summary>
    public void StoreApiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ProtectedApiKey = null;
            return;
        }

        byte[] blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
        ProtectedApiKey = Convert.ToBase64String(blob);
    }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundFluent");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch
        {
            // Corrupt or unreadable file: fall back to defaults rather than failing to start.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
        }
        catch
        {
            // Settings are a convenience; a failed write shouldn't take the app down.
        }
    }
}
