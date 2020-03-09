﻿using CommandLine;
using PixelPlanetUtils;
using PixelPlanetUtils.CanvasInteraction;
using PixelPlanetUtils.Eventing;
using PixelPlanetUtils.Logging;
using PixelPlanetUtils.NetworkInteraction;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace PixelPlanetWatcher
{
    using Pixel = ValueTuple<short, short, PixelColor>;

    class Program
    {
        private static readonly CancellationTokenSource finishCTS = new CancellationTokenSource();
        private static ChunkCache cache;
        private static short x1, y1, x2, y2;
        private static string logFilePath;
        private static string filename;
        private static Logger logger;
        private static List<Pixel> updates = new List<Pixel>();
        private static Task<FileStream> lockingStreamTask;
        private static readonly object listLockObj = new object();
        private static readonly Thread saveThread = new Thread(SaveChangesThreadBody);
        private static bool disableUpdates;
        private static Action stopListening;

        private static void Main(string[] args)
        {
            try
            {
                ParseArguments(args);
                if (!disableUpdates)
                {
                    if (CheckForUpdates())
                    {
                        return;
                    }
                }

                logger = new Logger(logFilePath, finishCTS.Token);
                logger.LogDebug("Command line: " + Environment.CommandLine);
                HttpWrapper.Logger = logger;
                cache = new ChunkCache(x1, y1, x2, y2, logger);
                bool initialMapSavingStarted = false;
                saveThread.Start();
                if (string.IsNullOrWhiteSpace(filename))
                {
                    filename = string.Format("pixels_({0};{1})-({2};{3})_{4:yyyy.MM.dd_HH-mm}.bin", x1, y1, x2, y2, DateTime.Now);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filename)));
                do
                {
                    try
                    {
                        HttpWrapper.ConnectToApi();
                        using (WebsocketWrapper wrapper = new WebsocketWrapper(logger, true))
                        {
                            cache.Wrapper = wrapper;
                            if (!initialMapSavingStarted)
                            {
                                logger.LogDebug("Main(): initiating map saving");
                                initialMapSavingStarted = true;
                                lockingStreamTask = Task.Run(SaveInitialMapState);
                            }
                            wrapper.OnPixelChanged += Wrapper_OnPixelChanged;
                            stopListening = wrapper.StopListening;
                            Console.CancelKeyPress += (o, e) =>
                            {
                                logger.LogDebug("Console.CancelKeyPress received");
                                e.Cancel = true;
                                wrapper.StopListening();
                            };
                            logger.LogInfo("Press Ctrl+C to stop");
                            wrapper.StartListening();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"Unhandled exception: {ex.Message}");
                        Thread.Sleep(1000);
                    }
                } while (true);
            }
            finally
            {
                logger?.LogInfo("Exiting...");
                finishCTS.Cancel();
                Thread.Sleep(1000);
                finishCTS.Dispose();
                logger?.Dispose();
                if (!saveThread.Join(10000))
                {
                    Console.WriteLine("Save thread can't finish, aborting. Please contact developer");
                    if (saveThread.IsAlive)
                    {
                        saveThread.Interrupt();
                    }
                }
            }
        }

        private static void Wrapper_OnPixelChanged(object sender, PixelChangedEventArgs e)
        {
            short x = PixelMap.ConvertToAbsolute(e.Chunk.Item1, e.Pixel.Item1);
            short y = PixelMap.ConvertToAbsolute(e.Chunk.Item2, e.Pixel.Item2);
            if (x <= x2 && x >= x1 && y <= y2 && y >= y1)
            {
                logger.LogPixel("Received pixel update:", e.DateTime, MessageGroup.PixelInfo, x, y, e.Color);
                lock (listLockObj)
                {
                    updates.Add((x, y, e.Color));
                }
            }
        }

        private static FileStream SaveInitialMapState()
        {
            DateTime now = DateTime.Now;
            cache.DownloadChunks();
            using (FileStream fileStream = File.Open(filename, FileMode.Create, FileAccess.Write))
            {
                using (GZipStream compressionStream = new GZipStream(fileStream, CompressionLevel.Fastest))
                {
                    using (BinaryWriter writer = new BinaryWriter(compressionStream))
                    {
                        writer.Write(x1);
                        writer.Write(y1);
                        writer.Write(x2);
                        writer.Write(y2);
                        writer.Write(now.ToBinary());
                        for (int y = y1; y <= y2; y++)
                        {
                            for (int x = x1; x <= x2; x++)
                            {
                                writer.Write((byte)cache.GetPixelColor((short)x, (short)y));
                            }
                        }
                    }
                }
            }
            logger.Log("Chunk data is saved to file", MessageGroup.TechInfo);
            return new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        private static bool ParseArguments(string[] args)
        {
            using (Parser parser = new Parser(cfg =>
            {
                cfg.CaseInsensitiveEnumValues = true;
                cfg.HelpWriter = Console.Out;
            }))
            {
                bool success = true;
                parser.ParseArguments<Options>(args)
                    .WithNotParsed(e => success = false)
                    .WithParsed(o =>
                    {
                        x1 = o.LeftX;
                        y1 = o.TopY;
                        x2 = o.RightX;
                        y2 = o.BottomY;
                        if (x1 > x2 || y1 > y2)
                        {
                            Console.WriteLine("Invalid args: check rectangle borders");
                            success = false;
                            return;
                        }
                        logFilePath = o.LogFilePath;
                        disableUpdates = o.DisableUpdates;
                        filename = o.FileName;
                        if (o.UseMirror)
                        {
                            if (o.ServerUrl != null)
                            {
                                Console.WriteLine("Invalid args: mirror usage and custom server address are specified");
                                success = false;
                                return;
                            }
                            UrlManager.MirrorMode = true;
                        }
                        if (o.ServerUrl != null)
                        {
                            UrlManager.BaseUrl = o.ServerUrl;
                        }
                    });
                return success;
            }
        }

        private static bool CheckForUpdates()
        {
            using (UpdateChecker checker = new UpdateChecker(logger))
            {
                if (checker.NeedsToCheckUpdates())
                {
                    logger.Log("Checking for updates...", MessageGroup.Update);
                    if (checker.UpdateIsAvailable(out string version, out bool isCompatible))
                    {
                        logger.Log($"Update is available: {version} (current version is {App.Version})", MessageGroup.Update);
                        if (isCompatible)
                        {
                            logger.Log("New version is backwards compatible, it will be relaunched with same arguments", MessageGroup.Update);
                        }
                        else
                        {
                            logger.Log("Argument list was changed, check it and relaunch bot manually after update", MessageGroup.Update);
                        }
                        logger.Log("Press Enter to update, anything else to skip", MessageGroup.Update);
                        while (Console.KeyAvailable)
                        {
                            Console.ReadKey(true);
                        }
                        ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                        if (keyInfo.Key == ConsoleKey.Enter)
                        {
                            logger.Log("Starting update...", MessageGroup.Update);
                            checker.StartUpdate();
                            return true;
                        }
                    }
                    else
                    {
                        if (version == null)
                        {
                            logger.LogError("Cannot check for updates");
                        }
                    }
                }
            }
            return false;
        }

        private static void SaveChangesThreadBody()
        {
            Task GetDelayTask() => Task.Delay(TimeSpan.FromMinutes(1), finishCTS.Token);

            Task delayTask = GetDelayTask();
            try
            {
                try
                {
                    do
                    {
                        delayTask.Wait();
                        delayTask = GetDelayTask();
                        List<Pixel> saved;
                        lock (listLockObj)
                        {
                            saved = updates;
                            updates = new List<Pixel>();
                        }
                        DateTime now = DateTime.Now;
                        if (lockingStreamTask.IsFaulted)
                        {
                            throw lockingStreamTask.Exception.GetBaseException();
                        }
                        lockingStreamTask = lockingStreamTask.ContinueWith(t =>
                        {
                            t.Result.Close();
                            WriteChangesToFile(saved);
                            return new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.None);
                        });
                    } while (true);
                }
                catch (ThreadInterruptedException)
                {
                    logger.LogDebug("SaveChangesThreadBody(): cancelling (1)");
                }
                catch (TaskCanceledException)
                {
                    logger.LogDebug("SaveChangesThreadBody(): cancelling (2)");
                }
                catch (AggregateException ae) when (ae.GetBaseException() is TaskCanceledException)
                {
                    logger.LogDebug("SaveChangesThreadBody(): cancelling (3)");
                }
                lockingStreamTask.Result.Close();
                WriteChangesToFile(updates);
            }
            catch (Exception ex)
            {
                logger.LogError($"Unhandled exception during saving: {ex.GetBaseException().Message}");
                stopListening();
            }

            void WriteChangesToFile(List<Pixel> pixels)
            {
                if (pixels.Count > 0)
                {
                    using (FileStream fileStream = File.Open(filename, FileMode.Append, FileAccess.Write))
                    {
                        using (GZipStream compressionStream = new GZipStream(fileStream, CompressionLevel.Fastest))
                        {
                            using (BinaryWriter writer = new BinaryWriter(compressionStream))
                            {
                                writer.Write(DateTime.Now.ToBinary());
                                writer.Write((uint)pixels.Count);
                                foreach ((short, short, PixelColor) pixel in pixels)
                                {
                                    writer.Write(pixel.Item1);
                                    writer.Write(pixel.Item2);
                                    writer.Write((byte)pixel.Item3);
                                }
                            }
                        }
                    }
                    logger.LogInfo($"{pixels.Count} pixel updates are saved to file");
                }
                else
                {
                    logger.LogInfo($"No pixel updates to save");
                }
            }
        }
    }
}