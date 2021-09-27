﻿using Downloader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ritsukage.Tools.Console;

namespace Ritsukage.Tools
{
    public static class DownloadManager
    {
        public const string CacheFolder = "CacheFolder";
        public const string CacheRecordFile = "CacheRecord";

        struct CacheData
        {
            [JsonProperty("url")]
            public string Url { get; init; }

            [JsonProperty("path")]
            public string Path { get; init; }

            [JsonProperty("time")]
            public DateTime CacheTime { get; init; }

            [JsonProperty("to")]
            public DateTime ClearTime { get; init; }

            [JsonIgnore]
            public bool Exists => File.Exists(Path);

            public CacheData(string url, string path, DateTime cacheTime, int keepTime)
            {
                Url = url;
                Path = path;
                CacheTime = cacheTime;
                ClearTime = cacheTime.AddSeconds(keepTime);
            }

            public void Delete()
            {
                if (Exists)
                    File.Delete(Path);
            }
        }

        /// <summary>
        /// 获取新的缓存文件名
        /// </summary>
        public static string CacheFileName
            => Path.GetFullPath(Path.Combine(CacheFolder, Guid.NewGuid().ToString() + ".cache"));

        static readonly List<string> DownloadingList = new();

        static readonly ConcurrentDictionary<string, CacheData> CacheDataList = new();

        static bool _init = false;

        static void Init()
        {
            if (_init) return;
            _init = true;

            if (!Directory.Exists(CacheFolder))
                Directory.CreateDirectory(CacheFolder);

            if (File.Exists(CacheRecordFile))
            {
                CacheDataList.Clear();

                var array = JArray.Parse(File.ReadAllText(CacheRecordFile));
                foreach (var data in array)
                {
                    var cache = data.ToObject<CacheData>();
                    if (cache.Exists)
                        CacheDataList.TryAdd(cache.Url, cache);
                }

                foreach (var file in Directory.GetFiles(CacheFolder))
                    if (!CacheDataList.Any(x => x.Value.Path == Path.GetFullPath(file)))
                        File.Delete(file);
            }

            Save();

            StartCacheCleanupThread();
        }

        static void Save()
        {
            lock (CacheDataList)
            {
                File.WriteAllText(CacheRecordFile, JsonConvert.SerializeObject(CacheDataList.Values));
            }
        }

        static void DebugLog(string text)
            => ConsoleLog.Debug("Downloader", text);

        public static async Task<string> GetCache(string url)
        {
            while (DownloadingList.Contains(url))
                await Task.Delay(100);

            if (CacheDataList.TryGetValue(url, out var cache))
            {
                if (cache.Exists)
                    return cache.Path;
                else
                {
                    CacheDataList.TryRemove(url, out _);
                    Save();
                }
            }

            return null;
        }

        public static async Task<string> Download(string url, string referer = null, int keepTime = 3600)
        {
            Init();

            #region 检查缓存
            var _cache = await GetCache(url);
            if (!string.IsNullOrEmpty(_cache))
                return _cache;
            #endregion

            DownloadingList.Add(url);

            #region 下载
            var config = new DownloadConfiguration()
            {
                BufferBlockSize = 4096,
                ChunkCount = 5,
                OnTheFlyDownload = false,
                ParallelDownload = true
            };
            if (!string.IsNullOrWhiteSpace(referer))
            {
                config.RequestConfiguration = new RequestConfiguration()
                {
                    Referer = referer
                };
            };
            var downloader = new DownloadService(config);
            long fileSize = -1;
            downloader.DownloadStarted += (s, e) =>
            {
                fileSize = e.TotalBytesToReceive;
                DebugLog($"Start to download file from {url} ({e.TotalBytesToReceive} bytes)");
            };
            DateTime _lastUpdate = DateTime.Now;
            downloader.DownloadProgressChanged += (s, e) =>
            {
                var now = DateTime.Now;
                if ((now - _lastUpdate).TotalSeconds > 3)
                {
                    DebugLog($"Downloading {url}... {e.ReceivedBytesSize}/{e.TotalBytesToReceive} ({e.ProgressPercentage:F2}%)");
                    _lastUpdate = now;
                }
            };
            downloader.DownloadFileCompleted += (s, e) =>
            {
                if (e.Error != null)
                    DebugLog($"Download {url} failed." + Environment.NewLine + e.Error.GetFormatString(true));
                else
                    DebugLog($"Download {url} completed.");
            };
            Stream stream = null;
            try
            {
                stream = await downloader.DownloadFileTaskAsync(url);
            }
            catch (Exception ex)
            {
                ConsoleLog.Error("Download Manager",
                    "下载文件时发生错误："
                    + url
                    + Environment.NewLine
                    + ex.GetFormatString(true));
            }
            if (stream == null || fileSize < 0)
            {
                DownloadingList.Remove(url);
                return null;
            }
            stream.Seek(0, SeekOrigin.Begin);
            #endregion

            #region 储存
            var file = CacheFileName;
            stream.SaveToFile(file);
            stream.Dispose();
            CacheDataList.TryAdd(url, new CacheData(url, file, DateTime.Now, keepTime));
            Save();
            #endregion

            DownloadingList.Remove(url);
            return file;
        }

        public static Task<string[]> Download(string[] urls, string referer = null, int keepTime = 3600)
        {
            var result = new string[urls.Length];
            var tasks = new Task<string>[urls.Length];
            for (int i = 0; i < urls.Length; i++)
                tasks[i] = Download(urls[i], referer, keepTime);
            Task.WaitAll(tasks);
            for (int i = 0; i < urls.Length; i++)
                result[i] = tasks[i].Result;
            return Task.FromResult(result);
        }

        const int SaveBufferSize = 4096;
        static void SaveToFile(this Stream stream, string path)
        {
            var folder = Path.GetDirectoryName(path);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var buffer = new byte[SaveBufferSize];
            using var fileStream = File.OpenWrite(path);
            int osize;
            while ((osize = stream.Read(buffer, 0, SaveBufferSize)) > 0)
                fileStream.Write(buffer, 0, osize);
            fileStream.Close();
            fileStream.Dispose();
        }

        const int ClearCacheDelay = 1000;
        static void StartCacheCleanupThread()
        {
            new Thread(() =>
            {
                while (true)
                {
                    var now = DateTime.Now;
                    var list = CacheDataList.Where(x => x.Value.ClearTime < now).ToArray();
                    foreach (var data in list)
                    {
                        if (data.Value.Exists)
                            data.Value.Delete();
                        CacheDataList.TryRemove(data.Key, out _);
                    }
                    Save();
                    Thread.Sleep(ClearCacheDelay);
                }
            })
            {
                IsBackground = true
            }.Start();
        }
    }
}
