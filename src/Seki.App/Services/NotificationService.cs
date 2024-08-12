﻿using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.AppNotifications;
using Seki.App.Data.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.Storage;

namespace Seki.App.Services
{
    public class NotificationService
    {
        private static readonly List<NotificationMessage> _notificationHistory = new List<NotificationMessage>();
        public static IReadOnlyList<NotificationMessage> NotificationHistory => _notificationHistory.AsReadOnly();

        public static event EventHandler<NotificationMessage>? NotificationReceived;

        public static async void ShowDesktopNotification(NotificationMessage notificationMessage)
        {
            if (notificationMessage.Title != null && notificationMessage.NotificationType == "NEW")
            {
                string tag = $"{notificationMessage.Tag}";
                string group = $"{notificationMessage.GroupKey}";
                var builder = new AppNotificationBuilder()
                    .AddText(notificationMessage.AppName, new AppNotificationTextProperties().SetMaxLines(1))
                    .AddText(notificationMessage.Title)
                    .AddText(notificationMessage.Text)
                    //.SetTag(tag)
                    .SetGroup(group);

                // Add large icon
                if (!string.IsNullOrEmpty(notificationMessage.LargeIcon))
                {
                    using (var stream = Base64ToStream(notificationMessage.LargeIcon))
                    {
                        await SetNotificationIcon(builder, notificationMessage.LargeIcon, "largeicon.png");
                    }
                }
                else if (!string.IsNullOrEmpty(notificationMessage.AppIcon))
                {
                    await SetNotificationIcon(builder, notificationMessage.AppIcon, "appicon.png");
                }

                // Add big picture
                //if (!string.IsNullOrEmpty(notificationMessage.BigPicture))
                //{
                //    using (var stream = Base64ToStream(notificationMessage.BigPicture))
                //    {
                //        var randomAccessStream = await ConvertToRandomAccessStreamAsync(stream);
                //        builder.AddImage(new Uri("ms-appdata:///local/bigpicture.png"));
                //        await SaveStreamToFileAsync(randomAccessStream, "bigpicture.png");
                //    }
                //}

                var appNotification = builder.BuildNotification();
                appNotification.ExpiresOnReboot = true;
                AppNotificationManager.Default.Show(appNotification);

                _notificationHistory.Add(notificationMessage);
                NotificationReceived?.Invoke(null, notificationMessage);
            }
            System.Diagnostics.Debug.WriteLine($"Showed or updated notification: {notificationMessage}");
        }

        private static async Task SetNotificationIcon(AppNotificationBuilder builder, string iconBase64, string fileName)
        {
            using (var stream = Base64ToStream(iconBase64))
            {
                var randomAccessStream = await ConvertToRandomAccessStreamAsync(stream);
                builder.SetAppLogoOverride(new Uri($"ms-appdata:///local/{fileName}"), AppNotificationImageCrop.Circle);
                await SaveStreamToFileAsync(randomAccessStream, fileName);
            }
        }


        private static Stream Base64ToStream(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            return new MemoryStream(bytes);
        }

        private static async Task<IRandomAccessStream> ConvertToRandomAccessStreamAsync(Stream stream)
        {
            var randomAccessStream = new InMemoryRandomAccessStream();
            var outputStream = randomAccessStream.GetOutputStreamAt(0);
            await RandomAccessStream.CopyAsync(stream.AsInputStream(), outputStream);
            await outputStream.FlushAsync();
            return randomAccessStream;
        }

        private static async Task SaveStreamToFileAsync(IRandomAccessStream stream, string fileName)
        {
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile file = await localFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            using (var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                await RandomAccessStream.CopyAsync(stream, fileStream);
                await fileStream.FlushAsync();
            }
        }
    }
}
