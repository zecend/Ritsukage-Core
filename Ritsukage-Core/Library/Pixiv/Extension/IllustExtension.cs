﻿using Downloader;
using Ritsukage.Library.Pixiv.Model;
using Ritsukage.Tools.Console;
using Ritsukage.Tools.Zip;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ritsukage.Library.Pixiv.Extension
{
    public static class IllustExtension
    {
        public static async Task<Image<Rgba32>> GetUgoira(this Illust illust)
        {
            string head = $"Pixiv Illust(id: {illust.Id})";
            ConsoleLog.Debug(head, $"Getting illust ugoira metadata...");
            var meta = await illust.GetUgoiraMetadata();
            if (meta.Frames == null || meta.Frames.Length <= 0)
            {
                ConsoleLog.Debug(head, $"Getting illust ugoira metadata failed.");
                return null;
            }
            ConsoleLog.Debug(head, $"Succeed.");
            var downloader = new DownloadService(new DownloadConfiguration()
            {
                BufferBlockSize = 4096,
                ChunkCount = 5,
                OnTheFlyDownload = false,
                ParallelDownload = true,
                RequestConfiguration = new RequestConfiguration()
                {
                    Referer = illust.Url
                }
            });
            downloader.DownloadStarted += (s, e) =>
            {
                ConsoleLog.Debug(head, "Start downloading illust ugoira data pack...");
                ConsoleLog.Debug(head, $"Start to download ({e.TotalBytesToReceive} bytes)");
            };
            DateTime _lastUpdate = DateTime.Now;
            downloader.DownloadProgressChanged += (s, e) =>
            {
                var now = DateTime.Now;
                if ((now - _lastUpdate).TotalSeconds > 3)
                {
                    ConsoleLog.Debug(head, $"Downloading... {e.ReceivedBytesSize}/{e.TotalBytesToReceive} ({e.ProgressPercentage:F2}%)");
                    _lastUpdate = now;
                }
            };
            downloader.DownloadFileCompleted += (s, e) =>
            {
                if (e.Error != null)
                {
                    ConsoleLog.Error(head, "Download failed.");
                    ConsoleLog.Error(head, e.Error.GetFormatString(true));
                }
                else
                {
                    ConsoleLog.Debug(head, "Downloaded.");
                }
            };
            using var stream = await downloader.DownloadFileTaskAsync(meta.ZipUrl);
            if (stream == null)
            {
                return null;
            }
            stream.Seek(0, SeekOrigin.Begin);
            ConsoleLog.Debug(head, "Start to decompression ugoira data pack...");
            using var zip = ZipPackage.OpenStream(stream);
            List<Image<Rgba32>> imgs = new List<Image<Rgba32>>();
            foreach (var frame in meta.Frames)
            {
                using var zipStream = zip.GetFileStream(frame.File);
                var img = Image.Load<Rgba32>(zipStream);
                imgs.Add(img);
                zipStream.Dispose();
            }
            zip.Dispose();
            stream.Dispose();
            ConsoleLog.Debug(head, "Decompression completed.");
            ConsoleLog.Debug(head, "Start compositing GIF images..");
            var baseimg = imgs.First();
            var gif = new Image<Rgba32>(new Configuration(new GifConfigurationModule()),
                baseimg.Width, baseimg.Height);
            gif.Metadata.GetGifMetadata().RepeatCount = 0;
            for (var i = 0; i < imgs.Count; i++)
            {
                var frame = gif.Frames.AddFrame(imgs[i].Frames[0]);
                frame.Metadata.GetGifMetadata().FrameDelay = meta.Frames[i].Delay / 10;
            }
            gif.Frames.RemoveFrame(0);
            ConsoleLog.Debug(head, "Finished.");
            return gif;
        }

        public static async Task<string> SaveGifToTempFile(this Image<Rgba32> image)
        {
            return await Task.Run(() =>
            {
                var name = Path.GetTempFileName();
                var output = File.OpenWrite(name);
                var encoder = new GifEncoder
                {
                    ColorTableMode = GifColorTableMode.Local
                };
                encoder.Encode(image, output);
                output.Dispose();
                return name;
            });
        }

        public static async Task<Image<Rgba32>> LimitGifScale(this Image<Rgba32> image, int maxWidth, int maxHeight)
        {
            return await Task.Run(() =>
            {
                bool flag = false;
                var width = image.Width;
                var height = image.Height;
                if (width > maxWidth)
                {
                    flag = true;
                    var rate = (double)maxWidth / width;
                    width = Convert.ToInt32(Math.Floor(width * rate));
                    height = Convert.ToInt32(Math.Floor(height * rate));
                }
                if (height > maxHeight)
                {
                    flag = true;
                    var rate = (double)maxHeight / height;
                    width = Convert.ToInt32(Math.Floor(width * rate));
                    height = Convert.ToInt32(Math.Floor(height * rate));
                }
                var result = image.Clone();
                if (flag)
                {
                    result.Mutate(x => x.Resize(width, height, new BoxResampler()));
                }
                return result;
            });
        }
    }
}
