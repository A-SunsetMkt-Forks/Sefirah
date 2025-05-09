﻿using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Windows.Storage.Streams;

namespace Sefirah.App.Utils;

public static class CurrentUserInformation
{
    public static async Task<(string name, string? avatar)> GetCurrentUserInfoAsync()
    {
        try
        {
            string name = string.Empty;
            string? avatarBase64 = null;
            
            // this returns only the current user always for some reason
            var users = await Windows.System.User.FindAllAsync();
            if (!users.Any())
            {
                return (GetFallbackUserName(), null);
            }
            var currentUser = users[0];
            if (currentUser == null)
            {
                Debug.WriteLine("currentUser is null");
                return (GetFallbackUserName(), null);
            }

            avatarBase64 = await GetUserAvatarBase64Async(currentUser);

            // Try to get the name using properties
            var properties = await currentUser.GetPropertiesAsync(["FirstName", "DisplayName", "AccountName"]);

            if (properties.Any())
            {
                name = GetFirstNameFromProperties(properties);
            }

            if (string.IsNullOrEmpty(name))
            {
                // First try to get name from WindowsIdentity
                using var identity = WindowsIdentity.GetCurrent();
                var identityName = identity.Name;
                if (!string.IsNullOrEmpty(identityName))
                {
                    name = name.Split('\\').Last().Split(' ').First();
                }
            }
            
            // Last resort fallback
            if (string.IsNullOrEmpty(name))
            {
                name = GetFallbackUserName();
            }
            
            return (name, avatarBase64);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting user info: {ex}");
            return (GetFallbackUserName(), null);
        }
    }

    internal static string GetDeviceId()
    {
        throw new NotImplementedException();
    }

    public static string GenerateDeviceId()
    {
        string userSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        string machineName = Environment.MachineName;
        string combinedId = $"{machineName}-{userSid}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(combinedId));
        return BitConverter.ToString(hashBytes).Replace("-", "")[..15];
    }

    private static string GetFallbackUserName()
        => Environment.UserName.Split('\\').Last().Split(' ').First();

    private static string GetFirstNameFromProperties(IDictionary<string, object> properties)
    {
        if (properties.TryGetValue("FirstName", out object? value) && 
            value is string firstNameProperty &&
            !string.IsNullOrEmpty(firstNameProperty))
        {
            return firstNameProperty;
        }

        string fullName = properties["DisplayName"] as string
            ?? properties["AccountName"] as string
            ?? Environment.UserName;

        return fullName.Split(' ').FirstOrDefault() ?? fullName;
    }

    private static async Task<string?> GetUserAvatarBase64Async(Windows.System.User user)
    {
        try
        {
            var picture = await user.GetPictureAsync(Windows.System.UserPictureSize.Size1080x1080);
            if (picture == null)
            {
                return null;
            }

            using var stream = await picture.OpenReadAsync();
            using var reader = new DataReader(stream);

            await reader.LoadAsync((uint)stream.Size);
            byte[] buffer = new byte[stream.Size];
            reader.ReadBytes(buffer);

            return Convert.ToBase64String(buffer);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting user avatar: {ex}");
            return null;
        }
    }

}
