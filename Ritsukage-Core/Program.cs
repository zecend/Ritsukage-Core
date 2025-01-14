﻿using Meowtrix.PixivApi;
using Meowtrix.PixivApi.Json;
using Newtonsoft.Json;
using Ritsukage.Discord;
using Ritsukage.Library.Data;
using Ritsukage.Library.Subscribe;
using Ritsukage.QQ;
using Ritsukage.Tools.Console;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ritsukage
{
    class Program
    {
        static class PixivAccountClient
        {
            public static PixivApiClient PixivApi { get; private set; }
            public static string PixivApiToken => PixivApi == null ? string.Empty : PixivApiAuthResponse.AccessToken;
            static DateTimeOffset PixivApiAuthTime { get; set; }
            static AuthResponse PixivApiAuthResponse { get; set; }
            static DateTimeOffset PixivApiAuthExpiresIn { get; set; }

            static void UpdatePixivApiToken(DateTimeOffset authTime, AuthResponse authResponse)
            {
                PixivApiAuthTime = authTime;
                PixivApiAuthResponse = authResponse;
                PixivApiAuthExpiresIn = authTime.AddSeconds(authResponse.ExpiresIn);
                SavePixivApiToken();
            }

            static void SavePixivApiToken()
            {
                if (PixivApiAuthResponse != null)
                    File.WriteAllText("pixiv_refresh_token", PixivApiAuthResponse.RefreshToken);
            }

            static async Task<bool> LoginWithLastAuthToken(PixivApiClient pixiv_api)
            {
                try
                {
                    if (File.Exists("pixiv_refresh_token"))
                    {
                        var token = File.ReadAllText("pixiv_refresh_token");
                        (var authTime, var authResponse) = await pixiv_api.AuthAsync(token);
                        UpdatePixivApiToken(authTime, authResponse);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLog.Error("Pixiv", ex.GetFormatString());
                }
                return false;
            }

            public static async void PixivApiLogin()
            {
                var pixiv_api = new PixivApiClient();
                if (await LoginWithLastAuthToken(pixiv_api))
                {
                    KeepPixivApiAuth();
                    PixivApi = pixiv_api;
                    ConsoleLog.Info("Main", "Pixiv Api 已初始化");
                    return;
                }
                else
                {
                    (string verify, string url) = pixiv_api.BeginAuth();
                    File.WriteAllText("PixivLoginUrl.txt", "请在浏览器中打开并登录Pixiv，然后在F12的Network页面中获取其中 pixiv://....?code=xxx 的xxx部分粘贴在程序中" + Environment.NewLine + url);
                    try
                    {
                        Process.Start("notepad.exe", "PixivLoginUrl.txt");
                    } catch (Exception)
                    {
                    }
                    await Task.Factory.StartNew(async () =>
                    {
                        string key = Console.ReadLine();
                        try
                        {
                            File.Delete("PixivLoginUrl.txt");
                        }
                        catch
                        {
                        }
                        if (string.IsNullOrEmpty(key))
                        {
                            ConsoleLog.Error("Pixiv", "Pixiv Api 已禁用，将在下一次启动时重新登录");
                        }
                        else
                        {
                            try
                            {
                                (var authTime, var authResponse) = await pixiv_api.CompleteAuthAsync(key, verify);
                                UpdatePixivApiToken(authTime, authResponse);
                                KeepPixivApiAuth();
                                PixivApi = pixiv_api;
                                ConsoleLog.Info("Main", "Pixiv Api 已初始化");
                            }
                            catch (Exception ex)
                            {
                                ConsoleLog.Error("Pixiv", ex.GetFormatString());
                                ConsoleLog.Error("Pixiv", "Pixiv Api 已禁用，将在下一次启动时重新登录");
                            }
                        }
                    });
                }
            }

            static void KeepPixivApiAuth()
            {
                Task.Factory.StartNew(async () =>
                {
                    while (true)
                    {
                        await Task.Delay(1000);
                        if (PixivApi != null)
                        {
                            if ((PixivApiAuthExpiresIn - DateTimeOffset.Now).TotalSeconds <= 60)
                            {
                                (var authTime, var authResponse) = await PixivApi.AuthAsync(PixivApiAuthResponse.RefreshToken);
                                UpdatePixivApiToken(authTime, authResponse);
                                ConsoleLog.Info("Pixiv", "已更新Pixiv Api登录信息");
                            }
                        }
                    }
                });
            }
        }

        public static Config Config { get; private set; }

        public static WebProxy WebProxy { get; private set; }

        public static QQService QQServer { get; private set; }
        public static DiscordAPP DiscordServer { get; private set; }

        public static PixivApiClient PixivApi => PixivAccountClient.PixivApi;
        public static string PixivApiToken => PixivAccountClient.PixivApiToken;

        public static bool Working = false;

        static DateTime LaunchTime;

        static void Main()
        {
            Console.Title = "Ritsukage Core";
            var currentProcess = Process.GetCurrentProcess();
            _ = new Mutex(true, currentProcess.ProcessName, out var first);
            if (first)
            {
                LaunchTime = DateTime.Now;
                ConsoleLog.Info("Main", "Loading...");
                InitUnhandledExceptionHandler();
                InitWatchDog(currentProcess.Id, currentProcess.ProcessName);
                Launch();
                while (Working)
                {
                    UpdateTitle();
                    Thread.Sleep(100);
                }
                Shutdown();
            }
            else
            {
                ConsoleLog.Error("Ritsukage Core", "本程序目前不支持同一架构多实例同时运行");
            }
            ConsoleLog.Info("Main", "程序主逻辑已结束，按任意键结束程序");
            Console.ReadKey();
        }

        static void Launch()
        {
            var cfg = Config = Config.LoadConfig();
#if DEBUG
            ConsoleLog.SetLogLevel(LogLevel.Debug);
            ConsoleLog.Debug("Main", "当前正在使用Debug模式");
#else
            if (cfg.IsDebug)
            {
                ConsoleLog.SetLogLevel(LogLevel.Debug);
                ConsoleLog.Debug("Main", "当前正在使用Debug模式");
                ConsoleLog.Debug("Main", "！！DEBUG MODE 会导致程序运行速度降低，如果没有必要请不要保持开启！！");
                ConsoleLog.Debug("Main", "！！DEBUG MODE 会导致程序运行速度降低，如果没有必要请不要保持开启！！");
                ConsoleLog.Debug("Main", "！！DEBUG MODE 会导致程序运行速度降低，如果没有必要请不要保持开启！！");
            }
            else
                ConsoleLog.SetLogLevel(LogLevel.Info);
#endif
            ConsoleLog.Debug("Main", "Config:\r\n" + JsonConvert.SerializeObject(cfg, Formatting.Indented));

            ConsoleLog.Info("Main", "初始化数据库中……");
            Database.Init(cfg.DatabasePath);
            ConsoleLog.Info("Main", "数据库已装载");

            if (!string.IsNullOrEmpty(cfg.ProxyHttp))
            {
                ConsoleLog.Info("Main", "设置Http网络代理中……");
                SetHttpProxy(cfg.ProxyHttp);
                ConsoleLog.Info("Main", "Http网络代理已设置");
            }

            ConsoleLog.Info("Main", "订阅系统启动中……");
            SubscribeManager.Init();
            ConsoleLog.Info("Main", "订阅系统已装载");

            if (!string.IsNullOrWhiteSpace(Config.Roll_Api_Id) && !string.IsNullOrWhiteSpace(Config.Roll_Api_Secret))
            {
                Library.Roll.RollApi.Init(Config.Roll_Api_Id, Config.Roll_Api_Secret);
                ConsoleLog.Info("Main", "Roll Api 已初始化");
            }

            Task.Run(PixivAccountClient.PixivApiLogin);

            if (cfg.Discord)
            {
                Working = true;
                ConsoleLog.Info("Main", "已启用Discord功能");
                Task.Run(() =>
                {
                    try
                    {
                        DiscordServer = new(cfg.DiscordToken);
                        DiscordServer.Start();
                    }
                    catch (Exception ex)
                    {
                        ConsoleLog.Fatal("Main", "Discord功能启动失败");
                        ConsoleLog.Error("Main", ConsoleLog.ErrorLogBuilder(ex));
                        Working = false;
                    }
                });
            }

            if (cfg.QQ)
            {
                Working = true;
                ConsoleLog.Info("Main", "已启用QQ功能");
                Task.Run(() =>
                {
                    try
                    {
                        QQServer = new(new()
                        {
                            Host = cfg.Host,
                            Port = cfg.Port,
                            AccessToken = cfg.AccessToken,
                            HeartBeatTimeOut = TimeSpan.FromMilliseconds(cfg.HeartBeatTimeOut),
                            EnableSoraCommandManager = false
                        });
                        QQServer.Start();
                    }
                    catch (Exception ex)
                    {
                        ConsoleLog.Fatal("Main", "QQ功能启动失败");
                        ConsoleLog.Error("Main", ConsoleLog.ErrorLogBuilder(ex, true));
                        Working = false;
                    }
                });
            }
        }

        static void Shutdown()
        {
            try
            {
                QQServer?.Stop();
            }
            catch
            {
            }
            try
            {
                DiscordServer?.Stop();
            }
            catch
            {
            }
        }

        static void SetHttpProxy(string url)
        {
            WebProxy = new WebProxy(url, true);
            WebRequest.DefaultWebProxy = WebProxy;
        }

        static void UpdateTitle() => Console.Title = $"Ritsukage Core | 启动于 {LaunchTime:yyyy-MM-dd HH:mm:ss} | 运行时长 {DateTime.Now - LaunchTime}"
            + (Config.IsDebug ? " | DEBUG MODE" : string.Empty);

        static bool _initedUnhandledExceptionHandler = false;
        static void InitUnhandledExceptionHandler()
        {
            if (_initedUnhandledExceptionHandler) return;
            _initedUnhandledExceptionHandler = true;
            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
        }

        static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs args)
        {
            var directory = Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "crash-report"));
            var now = DateTime.Now;
            File.WriteAllText(
                Path.Combine(directory.FullName, $"crash-{now:yyyyMMdd-HHmmss-ffff}.log"),
                new StringBuilder()
                .AppendLine($"[启动于 {LaunchTime:yyyy-MM-dd HH:mm:ss.ffff}]")
                .AppendLine($"[崩溃于 {now:yyyy-MM-dd HH:mm:ss.ffff}]")
                .AppendLine($"[工作时长 {now - LaunchTime}]")
                .Append(ConsoleLog.ErrorLogBuilder((Exception)args.ExceptionObject))
                .ToString());
        }

        static bool _initedWatchDog = false;
        static void InitWatchDog(int pid, string name)
        {
            if (_initedWatchDog) return;
            _initedWatchDog = true;
            ConsoleLog.Info("Main", "初始化进程监视器……");
            var process = Process.Start(new ProcessStartInfo("SimpleWatchDog", $"{pid} -n \"{name}\"")
            {
                UseShellExecute = false
            });
            SimpleWatchDog.SimpleIPC.Client ipcClient = new(name);
            var delay = TimeSpan.FromSeconds(20);
            new Thread(() =>
            {
                while (!Working)
                    Thread.Sleep(100);
                while (Working)
                {
                    ipcClient.SendMessage("HeartBeat");
                    Thread.Sleep(delay);
                }
            })
            {
                IsBackground = true
            }.Start();
            ConsoleLog.Info("Main", "进程监视器初始化完成");
        }
    }
}