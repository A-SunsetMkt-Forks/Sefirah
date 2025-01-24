﻿using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using NetCoreServer;
using Sefirah.App.Data.Contracts;
using Sefirah.App.Data.Models;
using Sefirah.App.Services.Socket;
using Sefirah.App.Utils;
using Sefirah.App.Utils.Serialization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using Server = Sefirah.App.Services.Socket.Server;

namespace Sefirah.App.Services;
public class FileTransferService(
    ILogger logger, 
    ISessionManager sessionManager, 
    IUserSettingsService userSettingsService
    ) : IFileTransferService, ITcpClientProvider, ITcpServerProvider
{
    private readonly string storageLocation = userSettingsService.FeatureSettingsService.ReceivedFilesPath;
    private FileStream? currentFileStream;
    private FileMetadata? currentFileMetadata;
    private long bytesReceived;
    private Client? client;
    private Server? server;
    private ServerInfo? serverInfo;
    private ServerSession? session;
    private TaskCompletionSource<ServerSession>? connectionSource;
    private uint notificationSequence = 1;
    private TaskCompletionSource<bool>? sendTransferCompletionSource;
    private TaskCompletionSource<bool>? receiveTransferCompletionSource;

    public async Task InitializeAsync()
    {
        try
        {
            await RegisterNotifications();
        }
        catch (Exception ex)
        {
            logger.Error("Failed to initialize FileTransferService", ex);
            throw;
        }
    }

    private async Task RegisterNotifications()
    {
        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;

        try
        {
            await Task.Run(() => AppNotificationManager.Default.Register());
        }
        catch (Exception ex)
        {
            logger.Warn("Could not register for notifications, continuing without notifications", ex);
        }
    }

    public async Task ReceiveBulkFiles(BulkFileTransfer bulkFile)
    {
        // TODO : Implement bulk file transfer
    }

    public async Task ReceiveFile(FileTransfer data)
    {
        try
        {
            // Wait for any existing transfer to complete
            if (receiveTransferCompletionSource?.Task is not null)
            {
                await receiveTransferCompletionSource.Task;
            }

            ArgumentNullException.ThrowIfNull(data);
            string fullPath = string.Empty;

            receiveTransferCompletionSource = new TaskCompletionSource<bool>();
            var serverInfo = data.ServerInfo;
            currentFileMetadata = data.FileMetadata;

            // Open file stream
            currentFileStream = new FileStream(
                storageLocation,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920); // 80KB buffer

            var certificate = await CertificateHelper.GetOrCreateCertificateAsync();

            var context = new SslContext(
                SslProtocols.Tls12,
                certificate,
                (sender, cert, chain, errors) => true
            );

            client = new Client(context, serverInfo.IpAddress, serverInfo.Port, this, logger);
            if (!client.ConnectAsync())
            {
                throw new IOException("Failed to connect to file transfer server");
            }

            await ShowTransferNotification("Receiving File", $"{currentFileMetadata.FileName}", 0);

            // Wait for transfer completion
            await receiveTransferCompletionSource.Task;

            await ShowTransferNotification("File Received", $"{currentFileMetadata.FileName} has been saved successfully");
        }
        catch (Exception ex)
        {
            logger.Error("Error during file transfer setup", ex);
            throw;
        }
    }

    public void OnConnected()
    {
        logger.Info("Connected to file transfer server");
        bytesReceived = 0;
    }

    public void OnDisconnected()
    {
        logger.Info("Disconnected from file transfer server");

        // Close file stream if still open
        if (currentFileStream != null)
        {
            currentFileStream.Close();
            currentFileStream.Dispose();
            currentFileStream = null;

            receiveTransferCompletionSource?.TrySetException(new IOException("Connection to server lost"));
        }
        receiveTransferCompletionSource?.TrySetResult(true);
    }

    public void OnError(SocketError error)
    {
        logger.Error($"Socket error occurred during file transfer: {error}");
        receiveTransferCompletionSource?.TrySetException(new IOException($"Socket error: {error}"));
        CleanupTransfer();
    }

    public async void OnReceived(byte[] buffer, long offset, long size)
    {
        try
        {
            if (currentFileStream == null || currentFileMetadata == null)
            {
                throw new InvalidOperationException("File transfer not properly initialized");
            }

            // Write received data to file
            currentFileStream.Write(buffer, (int)offset, (int)size);
            bytesReceived += size;

            // Calculate progress
            var progress = (double)bytesReceived / currentFileMetadata.FileSize * 100;

            // Update notification for every 1% change
            if (Math.Floor(progress) > Math.Floor((double)(bytesReceived - size) / currentFileMetadata.FileSize * 100))
            {
                await ShowTransferNotification(
                    "File Transfer",
                    $"Receiving {currentFileMetadata.FileName}",
                    progress,
                    isReceiving: true);
            }

            // Check if transfer is complete
            if (bytesReceived >= currentFileMetadata.FileSize)
            {
                logger.Info("File transfer completed successfully");
                await ShowTransferNotification(
                    "File Transfer Complete",
                    $"Successfully received {currentFileMetadata.FileName}",
                    null,
                    isReceiving: true,
                    silent: true);
                CleanupTransfer(true);
            }
        }
        catch (Exception ex)
        {
            logger.Error("Error processing received file data", ex);
            await ShowTransferNotification(
                "File Transfer Failed",
                $"Error while receiving {currentFileMetadata?.FileName}",
                null,
                isReceiving: true);
            CleanupTransfer(false);
            receiveTransferCompletionSource?.TrySetException(ex);
        }
    }


    // Receive cleanup
    private void CleanupTransfer(bool success = false)
    {
        try
        {
            currentFileStream?.Close();
            currentFileStream?.Dispose();
            currentFileStream = null;

            client?.DisconnectAsync();
            client?.Dispose();
            client = null;
            receiveTransferCompletionSource?.SetResult(true);

            if (!success && currentFileMetadata != null)
            {
                // Delete incomplete file
                if (File.Exists(currentFileMetadata.FileName))
                {
                    File.Delete(currentFileMetadata.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("Error during transfer cleanup", ex);
        }
        finally
        {
            currentFileMetadata = null;
            bytesReceived = 0;
        }
    }

    // Share Target handler
    public async Task ProcessShareAsync(ShareOperation shareOperation)
    {
        try
        {
            if (shareOperation.Data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await shareOperation.Data.GetStorageItemsAsync();
                // Convert IStorageItem list to StorageFile array
                var files = items.OfType<StorageFile>().ToArray();
                
                if (files.Length > 1)
                {
                    await SendBulkFiles(files);
                }
                else if (files.Length == 1)
                {
                    var file = files[0];
                    var metadata = new FileMetadata
                    {
                        FileName = file.Name,
                        MimeType = file.ContentType,
                        FileSize = (long)(await file.GetBasicPropertiesAsync()).Size,
                        Uri = file.Path
                    };

                    await SendFile(File.OpenRead(file.Path), metadata);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Error in ProcessShareAsync: {ex.Message}", ex);
        }
        finally
        {
            shareOperation.ReportCompleted();
        }
    }

    public async Task SendFile(Stream stream, FileMetadata metadata)
    {
        try
        {
            // Wait for any existing transfer to complete
            if (sendTransferCompletionSource?.Task.IsCompleted == false)
            {
                await sendTransferCompletionSource.Task;
            }

            if (server != null)
            {
                server.Stop();
                server.Dispose();
                server = null;
            }

            if (session != null)
            {
                session.Disconnect();
                session = null;
            }

            sendTransferCompletionSource = null;
            connectionSource = null;

            var serverInfo = await InitializeServer();
            var transfer = new FileTransfer
            {
                ServerInfo = serverInfo,
                FileMetadata = metadata
            };

            var json = SocketMessageSerializer.Serialize(transfer); 
            logger.Debug($"Sending metadata: {json}");
            sessionManager.SendMessage(json);

            sendTransferCompletionSource = new TaskCompletionSource<bool>();
            await SendFileData(metadata, stream);
            await sendTransferCompletionSource.Task;
        }
        catch (Exception ex)
        {
            logger.Error("Error sending stream data", ex);
            throw;
        }
    }

    public async Task SendBulkFiles(StorageFile[] files)
    {
        var fileMetadataTasks = files.Select(async file => new FileMetadata
        {
            FileName = file.Name,
            MimeType = file.ContentType,
            FileSize = (long)(await file.GetBasicPropertiesAsync()).Size,
            Uri = file.Path
        });

        var fileMetadataList = await Task.WhenAll(fileMetadataTasks);

        try
        {
            var serverInfo = await InitializeServer();
            
            var transfer = new BulkFileTransfer
            {
                ServerInfo = serverInfo,
                Files = [.. fileMetadataList]
            };

            // Send metadata first
            sessionManager.SendMessage(SocketMessageSerializer.Serialize(transfer));

            foreach (var file in fileMetadataList)
            {
                logger.Debug($"Sending file: {file.FileName}");

                sendTransferCompletionSource = new TaskCompletionSource<bool>();

                await SendFileData(file, File.OpenRead(file.Uri!));

                // Wait for the transfer to complete before moving to next file
                await sendTransferCompletionSource.Task;
                
            }

            logger.Debug("All files transferred successfully");
        }
        catch (Exception ex)
        {
            logger.Error("Error in SendBulkFiles", ex);
            throw;
        }
    }

    public async Task SendFileData(FileMetadata metadata, Stream stream)
    {
        try
        {
            if (session == null)
            {
                connectionSource = new TaskCompletionSource<ServerSession>();
                // Wait for OnConnected event to trigger
                session = await connectionSource.Task;
                await Task.Delay(500); // Wait a bit for android to open read channel 
            }
            
            const int ChunkSize = 81920 * 2;

            using (stream)
            {
                var buffer = new byte[ChunkSize];
                long totalBytesRead = 0;

                while (totalBytesRead < metadata.FileSize && session?.IsConnected == true)
                {
                    int bytesRead = await stream.ReadAsync(buffer);
                    if (bytesRead == 0) break;

                    session.SendAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    var progress = (double)totalBytesRead / metadata.FileSize * 100;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error("Error in SendFileData", ex);
            sendTransferCompletionSource?.TrySetException(ex);
            throw;
        }
    }

    public async Task<ServerInfo> InitializeServer()
    {
        // Reuse server if it exists
        if (server != null && serverInfo != null)
        {
            return serverInfo;
        }

        var (ipAddress, _) = NetworkHelper.GetBestAddress();
        var certificate = await CertificateHelper.GetOrCreateCertificateAsync();
        var port = await NetworkHelper.FindAvailablePortAsync(9040);
        var context = new SslContext(SslProtocols.Tls12, certificate);
        server = new Server(context, IPAddress.Any, port, this, logger)
        {
            OptionDualMode = true,
            OptionReuseAddress = true
        };
        server.Start();
        serverInfo = new ServerInfo
        {
            IpAddress = ipAddress,
            Port = port
        };

        logger.Info($"File transfer server initialized at {serverInfo.IpAddress}:{serverInfo.Port}");

        return serverInfo;
    }

    private void CleanupServer()
    {
        server?.Stop();
        server?.Dispose();
        server = null;
        serverInfo = null;
    }

    // Server session event handlers
    public void OnConnected(ServerSession session)
    {
        logger.Info($"Client connected to file transfer server: {session.Id}");
        if (connectionSource?.Task.IsCompleted == false)
        {
            connectionSource.TrySetResult(session);
        }
    }

    public void OnDisconnected(ServerSession session)
    {
        if (sendTransferCompletionSource?.Task.IsCompleted == false)
        {
            sendTransferCompletionSource?.TrySetException(new Exception("Client disconnected"));
        }
        CleanupServer();
    }

    public void OnReceived(ServerSession session, byte[] buffer, long offset, long size)
    {
        string Result = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
        logger.Debug($"Received result: {Result}");
        if (Result == "Success")
        {
            sendTransferCompletionSource?.TrySetResult(true);
        }
    }

    private async Task ShowTransferNotification(string title, string message, double? progress = null, bool isReceiving = true, bool silent = false)
    {
        string tag = isReceiving ? "file-receive" : "file-send";
        try
        {
            if (progress.HasValue && progress > 0 && progress < 100)
            {
                // Update existing notification with progress
                var progressData = new AppNotificationProgressData(notificationSequence++)
                {
                    Title = title,
                    Value = progress.Value / 100,
                    ValueStringOverride = $"{progress.Value:F0}%",
                    Status = message
                };

                await AppNotificationManager.Default.UpdateAsync(progressData, tag, Constants.Notification.NotificationGroup);
            }
            else
            {
                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message)
                    .SetTag(tag)
                    .SetGroup(Constants.Notification.NotificationGroup);

                // Only add progress bar for initial notification
                if (progress == 0)
                {
                    builder.AddProgressBar(new AppNotificationProgressBar()
                        .BindTitle()
                        .BindValue()
                        .BindValueStringOverride()
                        .BindStatus());
                }

                // Add action buttons for completion notification
                if (progress == null && !string.IsNullOrEmpty(currentFileMetadata?.FileName))
                {
                    var filePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Downloads",
                        currentFileMetadata.FileName
                    );

                    builder
                        .AddButton(new AppNotificationButton("Open File")
                            .AddArgument("action", "openFile")
                            .AddArgument("filePath", filePath))
                        .AddButton(new AppNotificationButton("Open Folder")
                            .AddArgument("action", "openFolder")
                            .AddArgument("folderPath", Path.GetDirectoryName(filePath)));
                }

                if (silent)
                {
                    builder.MuteAudio();
                }

                var notification = builder.BuildNotification();

                // Set initial progress data only for initial notification
                if (progress == 0)
                {
                    var initialProgress = new AppNotificationProgressData(notificationSequence)
                    {
                        Title = title,
                        Value = 0,
                        ValueStringOverride = "0%",
                        Status = message
                    };

                    notification.Progress = initialProgress;
                }

                AppNotificationManager.Default.Show(notification);
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Notification failed - Tag: {tag}, Progress: {progress}, Sequence: {notificationSequence}", ex);
        }
    }

    private async void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            logger.Debug($"Notification invoked - Arguments: {string.Join(", ", args.Arguments.Select(x => $"{x.Key}={x.Value}"))}");

            try
            {
                await sender.RemoveByGroupAsync(Constants.Notification.NotificationGroup);
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to remove notifications: {ex.Message}");
            }

            await Task.Delay(100); // Small delay to ensure removal

            if (args.Arguments.TryGetValue("action", out string? action))
            {
                switch (action)
                {
                    case "openFile":
                        if (args.Arguments.TryGetValue("filePath", out string? filePath) && File.Exists(filePath))
                        {
                            logger.Debug($"Opening file: {filePath}");
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = filePath,
                                UseShellExecute = true
                            });
                        }
                        else
                        {
                            logger.Warn($"File not found or path not provided - FilePath: {filePath}");
                        }
                        break;

                    case "openFolder":
                        if (args.Arguments.TryGetValue("folderPath", out string? folderPath) && Directory.Exists(folderPath))
                        {
                            logger.Debug($"Opening folder: {folderPath}");
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{folderPath}\"",
                                UseShellExecute = true
                            });
                        }
                        else
                        {
                            logger.Warn($"Folder not found or path not provided - FolderPath: {folderPath}");
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Error handling notification action: {ex.Message}", ex);
        }
    }
}
