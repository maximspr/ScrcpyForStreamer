// ============================================================
//  Экран телефона на компьютере  ->  scrcpy 4.1
//  .NET Framework 4.x WinForms. Без прав администратора.
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace PhoneScreen
{
    // ========================================================
    // PATHS
    // ========================================================
    static class Paths
    {
        public static readonly string Root =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "PhoneScreen");

        public static string ScrcpyDir { get { return Path.Combine(Root, "scrcpy"); } }
        public static string ScrcpyExe { get { return Path.Combine(ScrcpyDir, "scrcpy.exe"); } }
        public static string AdbExe { get { return Path.Combine(ScrcpyDir, "adb.exe"); } }
        public static string Marker { get { return Path.Combine(ScrcpyDir, "installed-version.txt"); } }
        public static string Config { get { return Path.Combine(Root, "config.txt"); } }
        public static string LogFile { get { return Path.Combine(Root, "log.txt"); } }
        public static string SelfCopy { get { return Path.Combine(Root, "PhoneScreen.exe"); } }

        public const string Version = "4.1";
        public const string AppName = "Экран телефона";
        public const string SettingsName = "Экран телефона — настройки";
    }

    // ========================================================
    // LOG  (пишется молча, в интерфейсе не упоминается)
    // ========================================================
    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.Root);
                    FileInfo fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();

                    File.AppendAllText(Paths.LogFile,
                        DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine,
                        new UTF8Encoding(true));
                }
            }
            catch { }
        }
    }

    // ========================================================
    // STATE
    // ========================================================
    enum Stage
    {
        Starting,
        Extracting,
        NoPhone,
        PhoneNoDebug,
        AppleDevice,
        Unauthorized,
        Authorizing,
        Offline,
        Multiple,
        AdbConflict,
        DifferentPhone,
        WrongPhone,
        Probing,
        NoEncoder,
        NetworkSearching,
        NetworkLost,
        Ready,
        Running,
        Closed,
        Failed
    }

    enum Transport { Unknown, UsbDirect, UsbHub, Network }

    class Snapshot
    {
        public Stage Stage = Stage.Starting;
        public string Detail = "";
        public string Serial = "";
        public string Model = "";
        public string Android = "";
        public string Brand = "";
        public string SavedModel = "";
        public string SavedSerial = "";
        public string UsbName = "";
        public Transport Transport = Transport.Unknown;
    }

    // ========================================================
    // BRAND KNOWLEDGE
    // ========================================================
    static class Brands
    {
        public static string Detect(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = text.ToLowerInvariant();

            if (Has(t, "xiaomi", "redmi", "poco")) return "xiaomi";
            if (Has(t, "samsung", "sm-g", "sm-a", "sm-s", "sm-f", "sm-n")) return "samsung";
            if (Has(t, "oneplus")) return "oneplus";
            if (Has(t, "realme")) return "realme";
            if (Has(t, "oppo", "cph")) return "oppo";
            if (Has(t, "vivo", "iqoo")) return "vivo";
            if (Has(t, "honor")) return "honor";
            if (Has(t, "huawei", "hisi")) return "huawei";
            if (Has(t, "nubia", "redmagic", "red magic", "zte")) return "zte";
            if (Has(t, "tecno")) return "tecno";
            if (Has(t, "infinix")) return "infinix";
            if (Has(t, "itel")) return "itel";
            if (Has(t, "motorola", "lenovo", "moto")) return "lenovo";
            if (Has(t, "asus", "rog")) return "asus";
            if (Has(t, "sony", "xperia")) return "sony";
            if (Has(t, "meizu")) return "meizu";
            if (Has(t, "blackview", "oukitel", "doogee", "ulefone")) return "rugged";
            if (Has(t, "google", "pixel")) return "google";
            if (Has(t, "nothing")) return "nothing";
            if (Has(t, "sharp", "aquos")) return "sharp";
            if (Has(t, "lge", "lg electronics")) return "lg";
            if (Has(t, "microsoft", "nokia", "hmd")) return "hmd";
            if (Has(t, "fly")) return "fly";
            return "";
        }

        static bool Has(string t, params string[] needles)
        {
            foreach (string n in needles) if (t.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static string DevModePath(string key)
        {
            switch (key)
            {
                case "xiaomi":
                    return "      Настройки → О телефоне\n" +
                           "      7 раз нажать на «Версия HyperOS» (или «Версия MIUI»)\n\n" +
                           "Дальше: Настройки → Расширенные настройки → Для разработчиков";
                case "samsung":
                    return "      Настройки → Сведения о телефоне → Сведения о ПО\n" +
                           "      7 раз нажать на «Номер сборки»\n\n" +
                           "Дальше: Настройки → Параметры разработчика";
                case "vivo":
                    return "      Настройки → Ещё настройки → О телефоне\n" +
                           "      7 раз нажать на «Версия ПО»\n\n" +
                           "Дальше: Настройки → Ещё настройки → Для разработчиков";
                case "oppo":
                case "realme":
                case "oneplus":
                    return "      Настройки → О телефоне → Версия\n" +
                           "      7 раз нажать на «Номер сборки»\n\n" +
                           "Дальше: Настройки → Дополнительные настройки → Для разработчиков";
                case "huawei":
                case "honor":
                    return "      Настройки → О телефоне\n" +
                           "      7 раз нажать на «Номер сборки»\n\n" +
                           "Дальше: Настройки → Система → Для разработчиков";
                case "meizu":
                    return "      Настройки → Об устройстве\n" +
                           "      7 раз нажать на «Номер сборки»\n\n" +
                           "Дальше: Настройки → Спец. возможности → Для разработчиков";
                default:
                    return "      Настройки → О телефоне\n" +
                           "      7 раз нажать на «Номер сборки»\n\n" +
                           "Дальше: Настройки → Система → Для разработчиков\n" +
                           "(на части телефонов — сразу Настройки → Для разработчиков)";
            }
        }

        public static string ControlNote(string key)
        {
            switch (key)
            {
                case "xiaomi":
                    return "На Xiaomi / Redmi / POCO для управления с компьютера нужен ещё один пункт: " +
                           "«Отладка по USB (Настройки безопасности)», и после него — перезагрузка телефона.\n\n" +
                           "Без управления всё работает и так.";
                case "oppo":
                case "realme":
                case "oneplus":
                    return "На ColorOS / realme UI для управления с компьютера нужно включить " +
                           "«Отключить контроль разрешений» — в самом низу меню «Для разработчиков».";
                case "vivo":
                    return "На vivo / iQOO для управления с компьютера может понадобиться пункт " +
                           "«Отладка по USB (изменение настроек)» в меню разработчика.";
                default:
                    return "";
            }
        }
    }

    // ========================================================
    // НАСТРОЙКИ  (значения по умолчанию — как в исходном скрипте)
    // ========================================================
    class Settings
    {
        public int MaxSize = 1920;      // 0 = без ограничения
        public int MaxFps = 60;         // 0 = без ограничения
        public int BitRateM = 20;       // Мбит/с
        public string Codec = "h264";   // h264 | h265
        public string Audio = "auto";      // auto | both | pc | off
        public string AudioCodec = "opus"; // opus | aac | flac | raw

        public bool PcControl = false;
        public bool TurnScreenOff = false;
        public bool KeepActive = false;

        public bool Fullscreen = false;
        public bool AlwaysOnTop = false;
        public bool Borderless = false;

        public bool NetworkMode = false;
        public string NetworkAddr = "";

        public bool ShareInternet = false;

        public bool CloseOnUnplug = true;
        public const int UnplugDelaySec = 6;

        // по сети физически не пролезает то же, что по проводу
        public const int NetMaxSize = 1600;
        public const int NetMaxBitRate = 8;

        public int EffectiveMaxSize
        {
            get
            {
                if (!NetworkMode) return MaxSize;
                if (MaxSize == 0) return NetMaxSize;
                return Math.Min(MaxSize, NetMaxSize);
            }
        }

        public int EffectiveBitRate
        {
            get { return NetworkMode ? Math.Min(BitRateM, NetMaxBitRate) : BitRateM; }
        }

        public Settings Clone()
        {
            return (Settings)MemberwiseClone();
        }
    }

    // ========================================================
    // CONFIG FILE
    // ========================================================
    class Config
    {
        public string UsbSerial = "";
        public string EncoderH264 = "";
        public string EncoderH265 = "";
        public string EncoderAv1 = "";
        public string EncoderVp8 = "";
        public string EncoderVp9 = "";
        public string Android = "";
        public string Model = "";
        public string Brand = "";
        public Settings S = new Settings();

        public bool IsValid { get { return UsbSerial.Length > 0 && EncoderH264.Length > 0; } }

        public string EncoderName(string codec)
        {
            switch (codec)
            {
                case "h265": return EncoderH265;
                case "av1": return EncoderAv1;
                case "vp8": return EncoderVp8;
                case "vp9": return EncoderVp9;
                default: return EncoderH264;
            }
        }

        public string Target
        {
            get { return (S.NetworkMode && S.NetworkAddr.Length > 0) ? S.NetworkAddr : UsbSerial; }
        }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(Paths.Config)) return c;
                foreach (string line in File.ReadAllLines(Paths.Config))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();

                    switch (k)
                    {
                        case "SERIAL": c.UsbSerial = v; break;
                        case "ENCODER_H264": c.EncoderH264 = v; break;
                        case "ENCODER_H265": c.EncoderH265 = v; break;
                        case "ENCODER_AV1": c.EncoderAv1 = v; break;
                        case "ENCODER_VP8": c.EncoderVp8 = v; break;
                        case "ENCODER_VP9": c.EncoderVp9 = v; break;
                        case "ANDROID": c.Android = v; break;
                        case "MODEL": c.Model = v; break;
                        case "BRAND": c.Brand = v; break;
                        case "MAX_SIZE": c.S.MaxSize = Num(v, c.S.MaxSize); break;
                        case "MAX_FPS": c.S.MaxFps = Num(v, c.S.MaxFps); break;
                        case "BITRATE": c.S.BitRateM = Num(v, c.S.BitRateM); break;
                        case "CODEC":
                            if (v == "h264" || v == "h265" || v == "av1" ||
                                v == "vp8" || v == "vp9") c.S.Codec = v;
                            break;
                        case "AUDIO": c.S.Audio = v; break;
                        case "AUDIO_CODEC": c.S.AudioCodec = v; break;
                        case "PC_CONTROL": c.S.PcControl = (v == "1"); break;
                        case "TURN_SCREEN_OFF": c.S.TurnScreenOff = (v == "1"); break;
                        case "KEEP_ACTIVE": c.S.KeepActive = (v == "1"); break;
                        case "FULLSCREEN": c.S.Fullscreen = (v == "1"); break;
                        case "ALWAYS_ON_TOP": c.S.AlwaysOnTop = (v == "1"); break;
                        case "BORDERLESS": c.S.Borderless = (v == "1"); break;
                        case "NETWORK_MODE": c.S.NetworkMode = (v == "1"); break;
                        case "NETWORK_ADDR": c.S.NetworkAddr = v; break;
                        case "SHARE_INTERNET": c.S.ShareInternet = (v == "1"); break;
                        case "CLOSE_ON_UNPLUG": c.S.CloseOnUnplug = (v == "1"); break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("config read failed: " + ex.Message); }

            c.Sanitize();
            return c;
        }

        // int.TryParse обнуляет переменную при неудачном разборе, поэтому
        // испорченная строка превращала «1920 точек» в «без ограничения»,
        // а битрейт — в ноль, с которым scrcpy просто не стартует
        static int Num(string v, int fallback)
        {
            int r;
            return int.TryParse(v, out r) ? r : fallback;
        }

        // конфиг могли поправить руками или обрубить на середине
        void Sanitize()
        {
            if (S.MaxSize < 0 || S.MaxSize > 8192) S.MaxSize = 1920;
            if (S.MaxFps < 0 || S.MaxFps > 480) S.MaxFps = 60;
            if (S.BitRateM < 1 || S.BitRateM > 200) S.BitRateM = 20;

            if (S.Codec != "h264" && S.Codec != "h265" && S.Codec != "av1" &&
                S.Codec != "vp8" && S.Codec != "vp9") S.Codec = "h264";

            if (S.Audio != "auto" && S.Audio != "both" &&
                S.Audio != "pc" && S.Audio != "off") S.Audio = "auto";

            if (S.AudioCodec != "opus" && S.AudioCodec != "aac" &&
                S.AudioCodec != "flac" && S.AudioCodec != "raw") S.AudioCodec = "opus";

            // без адреса сетевой режим бессмыслен — вернёмся на провод
            if (S.NetworkMode && S.NetworkAddr.Length == 0) S.NetworkMode = false;

            // эти три пункта scrcpy отвергает без управления
            if (!S.PcControl) { S.KeepActive = false; S.TurnScreenOff = false; }

            // раздача интернета и работа по сети несовместимы
            if (S.NetworkMode) S.ShareInternet = false;
        }

        // Настройки могут менять из второго окна (ярлык «Настройки»), которое
        // работает отдельным процессом. Пишем так, чтобы не затереть телефон
        // и кодировщик, найденные тем временем главным окном.
        public void SaveSettingsOnly()
        {
            Config disk = Config.Load();
            disk.S = S;
            disk.Save();

            if (disk.UsbSerial.Length > 0)
            {
                UsbSerial = disk.UsbSerial;
                EncoderH264 = disk.EncoderH264;
                EncoderH265 = disk.EncoderH265;
                EncoderAv1 = disk.EncoderAv1;
                EncoderVp8 = disk.EncoderVp8;
                EncoderVp9 = disk.EncoderVp9;
                Android = disk.Android;
                Model = disk.Model;
                Brand = disk.Brand;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("SERIAL=" + UsbSerial);
                sb.AppendLine("ENCODER_H264=" + EncoderH264);
                sb.AppendLine("ENCODER_H265=" + EncoderH265);
                sb.AppendLine("ENCODER_AV1=" + EncoderAv1);
                sb.AppendLine("ENCODER_VP8=" + EncoderVp8);
                sb.AppendLine("ENCODER_VP9=" + EncoderVp9);
                sb.AppendLine("ANDROID=" + Android);
                sb.AppendLine("MODEL=" + Model);
                sb.AppendLine("BRAND=" + Brand);
                sb.AppendLine("MAX_SIZE=" + S.MaxSize);
                sb.AppendLine("MAX_FPS=" + S.MaxFps);
                sb.AppendLine("BITRATE=" + S.BitRateM);
                sb.AppendLine("CODEC=" + S.Codec);
                sb.AppendLine("AUDIO=" + S.Audio);
                sb.AppendLine("AUDIO_CODEC=" + S.AudioCodec);
                sb.AppendLine("PC_CONTROL=" + B(S.PcControl));
                sb.AppendLine("TURN_SCREEN_OFF=" + B(S.TurnScreenOff));
                sb.AppendLine("KEEP_ACTIVE=" + B(S.KeepActive));
                sb.AppendLine("FULLSCREEN=" + B(S.Fullscreen));
                sb.AppendLine("ALWAYS_ON_TOP=" + B(S.AlwaysOnTop));
                sb.AppendLine("BORDERLESS=" + B(S.Borderless));
                sb.AppendLine("NETWORK_MODE=" + B(S.NetworkMode));
                sb.AppendLine("NETWORK_ADDR=" + S.NetworkAddr);
                sb.AppendLine("SHARE_INTERNET=" + B(S.ShareInternet));
                sb.AppendLine("CLOSE_ON_UNPLUG=" + B(S.CloseOnUnplug));

                // пишем через временный файл: обрыв питания посреди записи
                // не оставит обрубленный конфиг и не потеряет телефон
                string tmp = Paths.Config + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));

                if (File.Exists(Paths.Config)) File.Replace(tmp, Paths.Config, null);
                else File.Move(tmp, Paths.Config);
            }
            catch (Exception ex) { Log.Write("config save failed: " + ex.Message); }
        }

        static string B(bool v) { return v ? "1" : "0"; }
    }

    // ========================================================
    // PROCESS HELPER
    // ========================================================
    static class Exec
    {
        public static string Run(string exe, string args, int timeoutMs)
        {
            StringBuilder output = new StringBuilder();
            object gate = new object();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                if (Directory.Exists(Paths.ScrcpyDir)) psi.WorkingDirectory = Paths.ScrcpyDir;

                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    { if (e.Data != null) { lock (gate) output.AppendLine(e.Data); } };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    { if (e.Data != null) { lock (gate) output.AppendLine(e.Data); } };

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        lock (gate) output.AppendLine("[timeout]");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate) output.AppendLine("[exec error] " + ex.Message);
            }
            lock (gate) return output.ToString();
        }
    }

    // ========================================================
    // UNPACKING
    // ========================================================
    static class Unpacker
    {
        public static bool AlreadyInstalled()
        {
            try
            {
                return File.Exists(Paths.ScrcpyExe)
                    && File.Exists(Paths.AdbExe)
                    && File.Exists(Paths.Marker)
                    && File.ReadAllText(Paths.Marker).Trim() == Paths.Version;
            }
            catch { return false; }
        }

        public static void Extract()
        {
            Log.Write("extracting scrcpy " + Paths.Version);
            Directory.CreateDirectory(Paths.ScrcpyDir);

            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream zip = asm.GetManifestResourceStream("scrcpy.zip"))
            {
                if (zip == null)
                    throw new Exception("Внутри программы не найден архив scrcpy — сборка повреждена.");

                using (ZipArchive archive = new ZipArchive(zip, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Length == 0) continue;

                        string rel = entry.FullName.Replace('/', '\\');
                        int slash = rel.IndexOf('\\');
                        if (slash >= 0) rel = rel.Substring(slash + 1);
                        if (rel.Length == 0) continue;

                        string target = Path.Combine(Paths.ScrcpyDir, rel);
                        string dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        entry.ExtractToFile(target, true);
                    }
                }
            }

            File.WriteAllText(Paths.Marker, Paths.Version);
        }
    }

    // ========================================================
    // USB VIEW
    // ========================================================
    class UsbView
    {
        public bool PortableDevice;
        public bool AndroidInterface;
        public bool Apple;
        public string AppleKind = "устройство Apple";
        public string FirstName = "";

        const string AppleVid = "VID_05AC";

        public static UsbView Query()
        {
            UsbView v = new UsbView();

            foreach (string[] d in Query2("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'WPD'"))
            {
                if (IsApple(d[0], d[1]))
                {
                    v.Apple = true;
                    v.AppleKind = AppleKindOf(d[0]);
                    v.FirstName = d[0];
                    continue;
                }
                v.PortableDevice = true;
                if (v.FirstName.Length == 0) v.FirstName = d[0];
            }

            foreach (string[] d in Query2(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%Android%' OR Name LIKE '%ADB%'"))
            {
                if (IsApple(d[0], d[1])) continue;
                v.AndroidInterface = true;
                if (v.FirstName.Length == 0 || v.Apple) v.FirstName = d[0];
                break;
            }

            if (!v.AndroidInterface)
            {
                foreach (string[] d in Query2(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Service = 'WinUSB'"))
                {
                    if (IsApple(d[0], d[1])) continue;
                    v.AndroidInterface = true;
                    if (v.FirstName.Length == 0) v.FirstName = d[0];
                    break;
                }
            }

            if (!v.Apple)
            {
                foreach (string[] d in Query2(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%iPhone%' " +
                    "OR Name LIKE '%iPad%' OR Name LIKE '%iPod%' OR Name LIKE '%Apple Mobile Device%'"))
                {
                    v.Apple = true;
                    v.AppleKind = AppleKindOf(d[0]);
                    if (v.FirstName.Length == 0) v.FirstName = d[0];
                    break;
                }
            }

            return v;
        }

        static bool IsApple(string name, string id)
        {
            string n = (name ?? "").ToLowerInvariant();
            string i = (id ?? "").ToUpperInvariant();

            if (i.IndexOf(AppleVid, StringComparison.Ordinal) >= 0) return true;
            return n.IndexOf("iphone", StringComparison.Ordinal) >= 0
                || n.IndexOf("ipad", StringComparison.Ordinal) >= 0
                || n.IndexOf("ipod", StringComparison.Ordinal) >= 0
                || n.IndexOf("apple mobile", StringComparison.Ordinal) >= 0;
        }

        static string AppleKindOf(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.IndexOf("ipad", StringComparison.Ordinal) >= 0) return "iPad";
            if (n.IndexOf("ipod", StringComparison.Ordinal) >= 0) return "iPod";
            if (n.IndexOf("iphone", StringComparison.Ordinal) >= 0) return "iPhone";
            return "устройство Apple";
        }

        static List<string[]> Query2(string wql)
        {
            List<string[]> found = new List<string[]>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(wql))
                using (ManagementObjectCollection c = s.Get())
                {
                    foreach (ManagementBaseObject o in c)
                    {
                        object n = o["Name"];
                        object p = o["PNPDeviceID"];
                        found.Add(new string[] {
                            n == null ? "" : n.ToString(),
                            p == null ? "" : p.ToString() });
                        o.Dispose();
                    }
                }
            }
            catch (Exception ex) { Log.Write("wmi failed: " + ex.Message); }
            return found;
        }
    }

    // ========================================================
    // ENCODERS  (по кремнию, а не по логотипу)
    // ========================================================
    class EncoderInfo
    {
        public string Codec;
        public string Name;
        public bool Hardware;
        public bool Vendor;
        public bool Alias;
        public int Score;

        public override string ToString()
        {
            return Codec + " " + Name + " hw=" + Hardware + " vendor=" + Vendor + " score=" + Score;
        }
    }

    static class Encoders
    {
        static readonly Regex Line = new Regex(
            @"--video-codec=([a-z0-9]+)\s+--video-encoder=(\S+?)\s*\((hw|sw|hybrid)\)(.*)$",
            RegexOptions.IgnoreCase);

        public static List<EncoderInfo> Parse(string raw)
        {
            List<EncoderInfo> list = new List<EncoderInfo>();
            foreach (string rawLine in raw.Replace("\r", "").Split('\n'))
            {
                Match m = Line.Match(rawLine.Trim());
                if (!m.Success) continue;

                EncoderInfo e = new EncoderInfo();
                e.Codec = m.Groups[1].Value.ToLowerInvariant();
                e.Name = m.Groups[2].Value.Trim('\'', '"');
                e.Hardware = m.Groups[3].Value.Equals("hw", StringComparison.OrdinalIgnoreCase);

                string tail = m.Groups[4].Value;
                e.Vendor = tail.IndexOf("[vendor]", StringComparison.OrdinalIgnoreCase) >= 0;
                e.Alias = tail.IndexOf("alias", StringComparison.OrdinalIgnoreCase) >= 0;
                e.Score = Rate(e);
                list.Add(e);
            }
            return list;
        }

        static int Rate(EncoderInfo e)
        {
            if (!e.Hardware) return int.MinValue;
            if (e.Alias) return int.MinValue;

            string n = e.Name.ToLowerInvariant();
            if (n.IndexOf(".secure", StringComparison.Ordinal) >= 0) return int.MinValue;

            int s = 0;
            if (e.Vendor) s += 40;

            if (n.StartsWith("c2.", StringComparison.Ordinal)) s += 20;
            else if (n.StartsWith("omx.", StringComparison.Ordinal)) s += 5;

            // Кремний узнан. Между вендорами не выбираем: в одном телефоне
            // стоит чип только одного из них, так что сравнивать их не с чем.
            if (IsKnownSilicon(n)) s += 30;

            // Запасной кодировщик самого Android. Формально бывает "hw",
            // но это не выделенный блок, и телефон от него греется.
            if (n.IndexOf("android", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("google", StringComparison.Ordinal) >= 0) s -= 60;

            if (n.EndsWith(".cq", StringComparison.Ordinal)) s -= 25;
            if (n.IndexOf(".hdr", StringComparison.Ordinal) >= 0) s -= 30;
            if (n.IndexOf("dolby", StringComparison.Ordinal) >= 0) s -= 30;
            if (n.IndexOf("low_latency", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("lowlatency", StringComparison.Ordinal) >= 0) s += 15;

            return s;
        }

        // Известные семейства кремния. Список нужен только чтобы отличить
        // настоящий аппаратный блок от запасного кодировщика Android,
        // а не чтобы предпочесть одного производителя другому.
        static readonly string[] Silicon = {
            "qti", "qcom",          // Qualcomm
            "mtk", "mediatek",      // MediaTek
            "exynos",               // Samsung Exynos, Google Tensor
            "hisi",                 // HiSilicon Kirin
            "sprd", "unisoc",       // Unisoc
            "amlogic", "rockchip", "rk.",
            "nvidia", "tegra", "imx", "hantro", "vpu"
        };

        static bool IsKnownSilicon(string n)
        {
            foreach (string k in Silicon)
                if (n.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static EncoderInfo Best(List<EncoderInfo> all, string codec)
        {
            EncoderInfo best = null;
            foreach (EncoderInfo e in all)
            {
                if (e.Codec != codec) continue;
                if (e.Score == int.MinValue) continue;
                if (best == null || e.Score > best.Score) best = e;
            }
            return best;
        }
    }

    // ========================================================
    // ENGINE
    // ========================================================
    class Engine
    {
        public Config Cfg = Config.Load();
        public Action<Stage, string> Progress;

        static readonly Regex DeviceLine = new Regex(
            @"^(\S+)\s+(device|unauthorized|offline|authorizing|connecting|recovery|sideload|host|no\s+permissions)\b",
            RegexOptions.IgnoreCase);
        static readonly Regex UsbPath = new Regex(@"\busb:(\S+)", RegexOptions.IgnoreCase);
        static readonly Regex Ipv4 = new Regex(@"inet\s+(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");

        readonly object gate = new object();
        string approvedSerial = null;
        readonly HashSet<string> rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> noEncoder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // серийник -> "vendor|model|android", чтобы не читать свойства в каждом опросе
        readonly Dictionary<string, string[]> propCache =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        bool extractionFailed = false;
        int usbTick = 0;
        int netTick = 0;
        UsbView lastUsb = new UsbView();

        public void ApprovePhone(string serial)
        {
            lock (gate) { approvedSerial = serial; rejected.Remove(serial); }
        }

        public void RejectPhone(string serial)
        {
            lock (gate)
            {
                rejected.Add(serial);
                if (string.Equals(approvedSerial, serial, StringComparison.OrdinalIgnoreCase))
                    approvedSerial = null;
            }
        }

        void Report(Stage st, string detail)
        {
            Action<Stage, string> cb = Progress;
            if (cb != null) cb(st, detail);
        }

        public bool EnsureInstalled()
        {
            if (Unpacker.AlreadyInstalled()) return true;
            try { Unpacker.Extract(); return true; }
            catch (Exception ex) { Log.Write("extract failed: " + ex); return false; }
        }

        // ----------------------------------------------------
        public Snapshot Poll()
        {
            Snapshot s = new Snapshot();
            s.SavedSerial = Cfg.UsbSerial;
            s.SavedModel = Cfg.Model;

            if (!Unpacker.AlreadyInstalled())
            {
                if (extractionFailed)
                {
                    s.Stage = Stage.Failed;
                    s.Detail = "Не удалось распаковать программу в папку:\n" + Paths.ScrcpyDir;
                    return s;
                }

                Report(Stage.Extracting, "");
                try { Unpacker.Extract(); }
                catch (Exception ex)
                {
                    extractionFailed = true;
                    s.Stage = Stage.Failed;
                    s.Detail = ex.Message;
                    return s;
                }
            }

            string raw = Exec.Run(Paths.AdbExe, "devices -l", 20000);

            if (raw.IndexOf("doesn't match this client", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("version mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                s.Stage = Stage.AdbConflict;
                return s;
            }

            List<string> ready = new List<string>();
            Dictionary<string, string> usbPaths = new Dictionary<string, string>();
            bool unauth = false, offline = false, authorizing = false;

            foreach (string line in raw.Replace("\r", "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("List of devices")) continue;

                Match m = DeviceLine.Match(t);
                if (!m.Success) continue;

                string serial = m.Groups[1].Value;
                string state = m.Groups[2].Value.ToLowerInvariant();

                if (state == "device")
                {
                    ready.Add(serial);
                    Match u = UsbPath.Match(t);
                    usbPaths[serial] = u.Success ? u.Groups[1].Value : "";
                }
                else if (state == "unauthorized") unauth = true;
                else if (state == "authorizing" || state == "connecting") authorizing = true;
                else if (state == "offline") offline = true;
            }

            // ---------- режим "по сети" -----------------------
            if (Cfg.S.NetworkMode && Cfg.S.NetworkAddr.Length > 0)
            {
                if (ready.Contains(Cfg.S.NetworkAddr))
                {
                    netTick = 0;   // связь есть: следующий обрыв снова начнём с «ищу»
                    s.Serial = Cfg.S.NetworkAddr;
                    s.Model = Cfg.Model;
                    s.Android = Cfg.Android;
                    s.Brand = Cfg.Brand;
                    s.Transport = Transport.Network;
                    s.Stage = Stage.Ready;
                    return s;
                }

                netTick++;
                if (netTick % 4 == 1)
                {
                    Report(Stage.NetworkSearching, Cfg.S.NetworkAddr);
                    Exec.Run(Paths.AdbExe, "connect " + Cfg.S.NetworkAddr, 12000);
                }

                s.Detail = Cfg.S.NetworkAddr;
                s.Stage = (netTick > 6) ? Stage.NetworkLost : Stage.NetworkSearching;
                return s;
            }

            // ---------- обычный режим, по проводу -------------
            // Если среди подключённых есть уже настроенный телефон, берём его,
            // а не упираемся в «подключено несколько устройств»
            if (ready.Count > 1 && Cfg.IsValid)
            {
                foreach (string r in ready)
                {
                    if (!string.Equals(r, Cfg.UsbSerial, StringComparison.OrdinalIgnoreCase)) continue;
                    ready.Clear();
                    ready.Add(Cfg.UsbSerial);
                    break;
                }
            }

            if (ready.Count > 1)
            {
                s.Stage = Stage.Multiple;
                s.Detail = string.Join("\n", ready.ToArray());
                return s;
            }

            if (ready.Count == 0)
            {
                if (authorizing) { s.Stage = Stage.Authorizing; return s; }
                if (unauth) { s.Stage = Stage.Unauthorized; return s; }
                if (offline) { s.Stage = Stage.Offline; return s; }

                if (usbTick % 3 == 0) lastUsb = UsbView.Query();
                usbTick++;

                s.UsbName = lastUsb.FirstName;
                s.Brand = Brands.Detect(lastUsb.FirstName);

                if (lastUsb.Apple && !lastUsb.AndroidInterface)
                {
                    s.Detail = lastUsb.AppleKind;
                    s.Stage = Stage.AppleDevice;
                    return s;
                }

                s.Stage = (lastUsb.PortableDevice || lastUsb.AndroidInterface)
                    ? Stage.PhoneNoDebug
                    : Stage.NoPhone;
                return s;
            }

            s.Serial = ready[0];
            s.Transport = ClassifyUsb(usbPaths.ContainsKey(s.Serial) ? usbPaths[s.Serial] : "");

            bool isSaved = Cfg.IsValid &&
                string.Equals(Cfg.UsbSerial, s.Serial, StringComparison.OrdinalIgnoreCase);

            if (isSaved)
            {
                s.Model = Cfg.Model;
                s.Android = Cfg.Android;
                s.Brand = Cfg.Brand;
                s.Stage = Stage.Ready;
                return s;
            }

            bool approved, wasRejected;
            lock (gate)
            {
                approved = string.Equals(approvedSerial, s.Serial, StringComparison.OrdinalIgnoreCase);
                wasRejected = rejected.Contains(s.Serial);
            }

            // Свойства телефона не меняются, поэтому читаем их ОДИН раз на серийник.
            // Пока висит окно «заменить или нет», опрос идёт каждую секунду, но
            // повторно adb уже не дёргается — берём из кэша.
            string[] props;
            lock (gate)
            {
                if (!propCache.TryGetValue(s.Serial, out props)) props = null;
            }

            if (props == null)
            {
                string vendor = Prop(s.Serial, "ro.product.manufacturer");
                string model = Prop(s.Serial, "ro.product.model");
                string name = (vendor + " " + model).Trim();
                if (name.Length == 0) name = "Android-устройство";
                props = new string[] { name, Prop(s.Serial, "ro.build.version.release"),
                                       Brands.Detect(vendor + " " + model) };
                lock (gate) propCache[s.Serial] = props;
            }

            s.Model = props[0];
            s.Android = props[1];
            s.Brand = props[2];

            // телефон уже проверяли — аппаратного энкодера нет, второй probe не нужен
            bool knownBad;
            lock (gate) knownBad = noEncoder.Contains(s.Serial);
            if (knownBad)
            {
                s.Stage = Stage.NoEncoder;
                return s;
            }

            // Пока человек не подтвердил новый телефон — НЕ трогаем --list-encoders.
            // Он висит до 60 секунд, а раньше запускался в каждом опросе, пока
            // окно выбора было на экране.
            if (!approved)
            {
                if (Cfg.IsValid) { s.Stage = Stage.DifferentPhone; return s; }
                if (wasRejected) { s.Stage = Stage.WrongPhone; return s; }
            }

            // подтверждение израсходовано: что бы дальше ни случилось, второй раз
            // в probe без нового подтверждения не уходим
            lock (gate) approvedSerial = null;

            Report(Stage.Probing, s.Model);

            string listing = Exec.Run(Paths.ScrcpyExe,
                "--serial=" + s.Serial + " --list-encoders", 60000);
            Log.Write("list-encoders:\n" + listing);

            List<EncoderInfo> found = Encoders.Parse(listing);
            foreach (EncoderInfo e in found) Log.Write("  " + e);

            EncoderInfo h264 = Encoders.Best(found, "h264");
            EncoderInfo h265 = Encoders.Best(found, "h265");
            EncoderInfo av1  = Encoders.Best(found, "av1");
            EncoderInfo vp8  = Encoders.Best(found, "vp8");
            EncoderInfo vp9  = Encoders.Best(found, "vp9");

            // H.264 есть почти всегда и это дефолт; без него телефон непригоден
            if (h264 == null)
            {
                // запоминаем, чтобы не гонять 60-секундный probe по кругу
                lock (gate) noEncoder.Add(s.Serial);
                s.Detail = DescribeSoftwareOnly(found);
                s.Stage = Stage.NoEncoder;
                return s;
            }

            Cfg.UsbSerial = s.Serial;
            Cfg.EncoderH264 = h264.Name;
            Cfg.EncoderH265 = (h265 == null) ? "" : h265.Name;
            Cfg.EncoderAv1 = (av1 == null) ? "" : av1.Name;
            Cfg.EncoderVp8 = (vp8 == null) ? "" : vp8.Name;
            Cfg.EncoderVp9 = (vp9 == null) ? "" : vp9.Name;
            Cfg.Android = s.Android;
            Cfg.Model = s.Model;
            Cfg.Brand = s.Brand;
            Cfg.Save();

            s.SavedSerial = Cfg.UsbSerial;
            s.SavedModel = Cfg.Model;
            s.Stage = Stage.Ready;
            return s;
        }

        static Transport ClassifyUsb(string path)
        {
            if (string.IsNullOrEmpty(path)) return Transport.Unknown;
            // "1-4" — прямо в порт компьютера, "1-4.2" — ещё один уровень, то есть хаб
            return path.IndexOf('.') >= 0 ? Transport.UsbHub : Transport.UsbDirect;
        }

        static string DescribeSoftwareOnly(List<EncoderInfo> found)
        {
            if (found.Count == 0) return "Телефон не сообщил ни одного видеокодировщика.";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Телефон предлагает только программные кодировщики:");
            int n = 0;
            foreach (EncoderInfo e in found)
            {
                if (e.Codec != "h264" && e.Codec != "h265") continue;
                if (n++ >= 4) break;
                sb.AppendLine("      " + e.Name);
            }
            return sb.ToString().TrimEnd();
        }

        public string Prop(string serial, string prop)
        {
            string raw = Exec.Run(Paths.AdbExe, "-s " + serial + " shell getprop " + prop, 12000);
            string[] lines = raw.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("*")) continue;
                if (t.StartsWith("adb", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.StartsWith("[exec error]") || t == "[timeout]") continue;
                if (t.IndexOf("daemon", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return t;
            }
            return "";
        }

        // ----------------------------------------------------
        // Переключение на сеть. Телефон должен быть на проводе.
        // Возвращает null при успехе, иначе текст ошибки.
        // ----------------------------------------------------
        public string SwitchToNetwork(out string address)
        {
            address = "";

            string devices = Exec.Run(Paths.AdbExe, "devices", 15000);
            if (devices.IndexOf(Cfg.UsbSerial, StringComparison.OrdinalIgnoreCase) < 0)
                return "Телефон сейчас не подключён проводом. Сначала подключи его кабелем.";

            string ip = FindDeviceIp(Cfg.UsbSerial);
            if (ip.Length == 0)
                return "Не удалось узнать адрес телефона в сети.\n\n" +
                       "Проверь, что телефон подключён к той же сети, что и компьютер — " +
                       "по Wi-Fi или через переходник с Ethernet.";

            string tcpip = Exec.Run(Paths.AdbExe, "-s " + Cfg.UsbSerial + " tcpip 5555", 20000);
            Log.Write("tcpip: " + tcpip);
            Thread.Sleep(2500);

            string addr = ip + ":5555";
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string res = Exec.Run(Paths.AdbExe, "connect " + addr, 15000);
                Log.Write("connect " + addr + ": " + res);
                if (res.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string check = Exec.Run(Paths.AdbExe, "devices", 12000);
                    if (check.IndexOf(addr, StringComparison.Ordinal) >= 0)
                    {
                        address = addr;
                        return null;
                    }
                }
                Thread.Sleep(1500);
            }

            return "Телефон не отозвался по адресу " + addr + ".\n\n" +
                   "Обычно это значит, что телефон и компьютер в разных сетях.";
        }

        public void SwitchToCable()
        {
            try
            {
                if (Cfg.S.NetworkAddr.Length > 0)
                    Exec.Run(Paths.AdbExe, "disconnect " + Cfg.S.NetworkAddr, 10000);
            }
            catch { }
        }

        string FindDeviceIp(string serial)
        {
            string raw = Exec.Run(Paths.AdbExe, "-s " + serial + " shell ip -o -4 addr show", 12000);

            string best = "";
            foreach (string line in raw.Replace("\r", "").Split('\n'))
            {
                Match m = Ipv4.Match(line);
                if (!m.Success) continue;

                string ip = m.Groups[1].Value;
                if (!ValidIp(ip)) continue;
                if (ip.StartsWith("127.")) continue;               // loopback
                if (ip.StartsWith("169.254.")) continue;           // без настоящего адреса

                // проводной переходник интереснее Wi-Fi: он стабильнее
                if (line.IndexOf("eth", StringComparison.OrdinalIgnoreCase) >= 0) return ip;
                if (best.Length == 0) best = ip;
            }
            return best;
        }

        // \d{1,3} в регулярке пропускает 999.999.999.999 — проверяем октеты
        static bool ValidIp(string ip)
        {
            string[] parts = ip.Split('.');
            if (parts.Length != 4) return false;
            foreach (string part in parts)
            {
                int v;
                if (!int.TryParse(part, out v)) return false;
                if (v < 0 || v > 255) return false;
            }
            return true;
        }

        // ----------------------------------------------------
        string EncoderFor(string codec) { return Cfg.EncoderName(codec); }

        public string BuildArgs()
        {
            Settings st = Cfg.S;

            // выбранный кодек, но с откатом на H.264, если у телефона
            // нет аппаратного кодировщика под него
            string codec = st.Codec;
            string encoder = EncoderFor(codec);
            if (encoder.Length == 0) { codec = "h264"; encoder = Cfg.EncoderH264; }

            List<string> a = new List<string>();
            a.Add(Q("--serial=" + Cfg.Target));
            a.Add("--video-codec=" + codec);
            a.Add(Q("--video-encoder=" + encoder));

            int size = st.EffectiveMaxSize;
            if (size > 0) a.Add("--max-size=" + size);
            if (st.MaxFps > 0) a.Add("--max-fps=" + st.MaxFps);
            a.Add("--video-bit-rate=" + st.EffectiveBitRate + "M");
            a.Add(Q("--window-title=" + Paths.AppName));

            // --- звук ---
            int major = 0;
            Match m = Regex.Match(Cfg.Android == null ? "" : Cfg.Android, @"^(\d+)");
            if (m.Success) int.TryParse(m.Groups[1].Value, out major);

            string audio = st.Audio;
            if (audio == "auto") audio = (major >= 13) ? "both" : (major >= 11 ? "pc" : "off");
            if (major < 11) audio = "off";
            if (audio == "both" && major < 13) audio = "pc";

            if (audio == "off")
            {
                a.Add("--no-audio");
            }
            else
            {
                if (audio == "both") a.Add("--audio-dup");
                else a.Add("--audio-source=output");
                if (st.AudioCodec.Length > 0 && st.AudioCodec != "opus")
                    a.Add("--audio-codec=" + st.AudioCodec);
            }

            // --- управление и всё, что от него зависит ---
            // scrcpy отвергает эти три пункта при --no-control, поэтому только вместе
            if (st.PcControl)
            {
                if (st.KeepActive) a.Add("--keep-active");
                if (st.TurnScreenOff) a.Add("--turn-screen-off");
            }
            else
            {
                a.Add("--no-control");
            }

            // --- окно ---
            if (st.Fullscreen) a.Add("--fullscreen");
            if (st.AlwaysOnTop) a.Add("--always-on-top");
            if (st.Borderless) a.Add("--window-borderless");

            return string.Join(" ", a.ToArray());
        }

        public Process Launch(out string error)
        {
            error = null;
            string args = BuildArgs();
            Log.Write("launch: " + args);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Paths.ScrcpyExe, args);
                psi.WorkingDirectory = Paths.ScrcpyDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                StringBuilder tail = new StringBuilder();
                object gate2 = new object();

                Process proc = new Process();
                proc.StartInfo = psi;
                proc.EnableRaisingEvents = true;
                proc.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate2) tail.AppendLine(e.Data);
                    Log.Write("scrcpy: " + e.Data);
                };
                proc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate2) tail.AppendLine(e.Data);
                    Log.Write("scrcpy: " + e.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (proc.WaitForExit(5000))
                {
                    string text;
                    lock (gate2) text = tail.ToString();
                    error = Explain(text);
                    try { proc.Dispose(); } catch { }
                    return null;
                }

                return proc;
            }
            catch (Exception ex)
            {
                Log.Write("launch failed: " + ex);
                error = ex.Message;
                return null;
            }
        }

        static string Explain(string text)
        {
            string t = text ?? "";

            if (t.IndexOf("INJECT_EVENTS", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Телефон не разрешил управление с компьютера.\n\n" +
                       "В настройках выключи «Управлять телефоном с компьютера» — так всё заработает.";

            if (t.IndexOf("Could not find encoder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("InvalidEncoder", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Выбранный кодировщик телефону не подошёл.\n\n" +
                       "Отключи и подключи телефон заново — программа подберёт другой.";

            if (t.IndexOf("Device disconnected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("device not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Телефон отключился. Проверь кабель.";

            string[] lines = t.Replace("\r", "").Split('\n');
            StringBuilder last = new StringBuilder();
            for (int i = Math.Max(0, lines.Length - 5); i < lines.Length; i++)
                if (lines[i].Trim().Length > 0) last.AppendLine(lines[i].Trim());

            return "Трансляция закрылась сразу после запуска.\n\n" + last.ToString().Trim();
        }

        static string Q(string s) { return "\"" + s + "\""; }
    }

    // ========================================================
    // ИНТЕРНЕТ ДЛЯ ТЕЛЕФОНА С КОМПЬЮТЕРА  (gnirehtet 2.5.1)
    //
    // На телефоне поднимается VPN-служба, которая заворачивает весь
    // трафик в туннель через adb на компьютер. Пока туннель жив —
    // интернет есть. Как только он рвётся, VPN остаётся поднятой и
    // телефон остаётся БЕЗ интернета вообще, даже при живом Wi-Fi.
    // Поэтому программа сама снимает VPN, когда телефон пропадает.
    // ========================================================
    static class Net
    {
        public static string Dir { get { return Path.Combine(Paths.Root, "gnirehtet"); } }
        public static string Exe { get { return Path.Combine(Dir, "gnirehtet.exe"); } }
        public static string Apk { get { return Path.Combine(Dir, "gnirehtet.apk"); } }

        const string Package = "com.genymobile.gnirehtet";

        static Process relay;
        static readonly object gate = new object();

        static volatile bool active;
        public static bool Active { get { return active; } }

        public static void EnsureExtracted()
        {
            if (File.Exists(Exe) && File.Exists(Apk)) return;

            Directory.CreateDirectory(Dir);
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream zip = asm.GetManifestResourceStream("gnirehtet.zip"))
            {
                if (zip == null) throw new Exception("Внутри программы нет gnirehtet.");
                using (ZipArchive archive = new ZipArchive(zip, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Length == 0) continue;
                        string rel = entry.FullName.Replace('/', '\\');
                        int slash = rel.IndexOf('\\');
                        if (slash >= 0) rel = rel.Substring(slash + 1);
                        if (rel.Length == 0) continue;

                        string target = Path.Combine(Dir, rel);
                        string d = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                        entry.ExtractToFile(target, true);
                    }
                }
            }
            Log.Write("gnirehtet extracted");
        }

        // gnirehtet зовёт "adb" по PATH, а наш adb лежит в своей папке
        static ProcessStartInfo Psi(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Exe, args);
            psi.WorkingDirectory = Dir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            string path = Environment.GetEnvironmentVariable("PATH");
            psi.EnvironmentVariables["PATH"] = Paths.ScrcpyDir + ";" + (path ?? "");
            psi.EnvironmentVariables["GNIREHTET_APK"] = Apk;
            return psi;
        }

        static string RunGn(string args, int timeoutMs)
        {
            StringBuilder output = new StringBuilder();
            object g = new object();
            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo = Psi(args);
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    { if (e.Data != null) { lock (g) output.AppendLine(e.Data); } };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    { if (e.Data != null) { lock (g) output.AppendLine(e.Data); } };

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                }
            }
            catch (Exception ex) { lock (g) output.AppendLine("[error] " + ex.Message); }

            string text;
            lock (g) text = output.ToString();
            Log.Write("gnirehtet " + args + " -> " + text.Replace("\r\n", " | ").Trim());
            return text;
        }

        // null = получилось, иначе текст для человека
        public static string Start(string serial)
        {
            lock (gate)
            {
                if (active) return null;

                try { EnsureExtracted(); }
                catch (Exception ex) { return ex.Message; }

                string installed = Exec.Run(Paths.AdbExe,
                    "-s " + serial + " shell pm list packages " + Package, 15000);

                if (installed.IndexOf(Package, StringComparison.Ordinal) < 0)
                {
                    string res = RunGn("install " + serial, 90000);
                    if (res.IndexOf("Failure", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "Не удалось поставить на телефон помощника для интернета.\n\n" +
                               "Возможно, телефон запрещает установку через USB. На Xiaomi это " +
                               "пункт «Установка через USB» в меню для разработчиков.";
                }

                // если прошлый запуск умер аварийно, раздатчик мог остаться
                // висеть и держать порт 31416
                if (relay == null)
                {
                    foreach (Process old in Process.GetProcessesByName("gnirehtet"))
                    {
                        try { old.Kill(); Log.Write("killed stray relay"); } catch { }
                        try { old.Dispose(); } catch { }
                    }
                }

                try
                {
                    relay = new Process();
                    relay.StartInfo = Psi("relay");
                    relay.EnableRaisingEvents = true;
                    relay.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    { if (e.Data != null) Log.Write("relay: " + e.Data); };
                    relay.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    { if (e.Data != null) Log.Write("relay: " + e.Data); };
                    relay.Start();
                    relay.BeginOutputReadLine();
                    relay.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    return "Не удалось запустить раздачу интернета: " + ex.Message;
                }

                Thread.Sleep(1500);

                if (relay.HasExited)
                    return "Раздача интернета не запустилась.\n\n" +
                           "Скорее всего, порт 31416 занят другой программой.";

                string start = RunGn("start " + serial, 30000);
                if (start.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    start.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Kill();
                    return "Телефон не принял раздачу интернета.\n\n" +
                           "Проверь, что на его экране нажато «ОК» в окне про VPN-подключение.";
                }

                active = true;
                Log.Write("gnirehtet active for " + serial);
                return null;
            }
        }

        public static void Stop(string serial)
        {
            lock (gate)
            {
                if (!active && relay == null) return;

                try
                {
                    if (serial != null && serial.Length > 0)
                        RunGn("stop " + serial, 8000);
                }
                catch { }

                Kill();
                active = false;
                Log.Write("gnirehtet stopped");
            }
        }

        static void Kill()
        {
            try
            {
                if (relay != null && !relay.HasExited) relay.Kill();
            }
            catch { }
            try { if (relay != null) relay.Dispose(); }
            catch { }
            relay = null;
        }
    }

    // ========================================================
    // ILLUSTRATIONS
    // ========================================================
    static class Art
    {
        public static readonly Color Ink = Color.FromArgb(52, 58, 70);
        public static readonly Color Accent = Color.FromArgb(232, 110, 40);
        public static readonly Color Good = Color.FromArgb(38, 158, 84);
        public static readonly Color Warn = Color.FromArgb(206, 152, 30);
        public static readonly Color Soft = Color.FromArgb(226, 230, 238);
        public static readonly Color Dim = Color.FromArgb(120, 126, 140);

        public static void Draw(Graphics g, Rectangle box, Stage stage)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int ph = box.Height - 40;
            int pw = (int)(ph * 0.50);
            Rectangle phone = new Rectangle(box.X + (box.Width - pw) / 2, box.Y + 20, pw, ph);

            switch (stage)
            {
                case Stage.NoPhone:
                case Stage.Offline:
                    Cable(g, phone); break;
                case Stage.PhoneNoDebug:
                    DevMode(g, phone); break;
                case Stage.Unauthorized:
                    AllowDialog(g, phone); break;
                case Stage.DifferentPhone:
                case Stage.WrongPhone:
                case Stage.Multiple:
                    TwoPhones(g, box); break;
                case Stage.NetworkSearching:
                case Stage.NetworkLost:
                    Waves(g, phone); break;
                case Stage.Ready:
                case Stage.Running:
                    Check(g, phone); break;
                case Stage.AppleDevice:
                case Stage.NoEncoder:
                case Stage.Failed:
                case Stage.AdbConflict:
                    Cross(g, phone); break;
                default:
                    Body(g, phone, Soft); break;
            }
        }

        static void Body(Graphics g, Rectangle r, Color screen)
        {
            using (SolidBrush b = new SolidBrush(Ink))
            using (SolidBrush sc = new SolidBrush(screen))
            {
                g.FillRectangle(b, r);
                g.FillRectangle(sc, new Rectangle(r.X + 5, r.Y + 14, r.Width - 10, r.Height - 28));
            }
        }

        static void Cable(Graphics g, Rectangle r)
        {
            Body(g, r, Soft);
            using (Pen p = new Pen(Accent, 4f))
            {
                int y = r.Bottom + 14;
                g.DrawLine(p, r.X + r.Width / 2, r.Bottom, r.X + r.Width / 2, y);
                g.DrawLine(p, r.X + r.Width / 2, y, r.X - 26, y);
            }
            using (SolidBrush b = new SolidBrush(Ink))
                g.FillRectangle(b, r.X - 50, r.Bottom + 2, 26, 24);
        }

        static void DevMode(Graphics g, Rectangle r)
        {
            Body(g, r, Color.White);
            int x = r.X + 14, w = r.Width - 28;
            using (SolidBrush line = new SolidBrush(Soft))
            using (SolidBrush hot = new SolidBrush(Accent))
            {
                for (int i = 0; i < 5; i++) g.FillRectangle(line, x, r.Y + 34 + i * 24, w, 13);
                g.FillRectangle(hot, x, r.Y + 34 + 5 * 24, w, 13);
            }
            using (Font f = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Accent))
                g.DrawString("×7", f, b, r.Right + 2, r.Y + 30 + 5 * 24);
        }

        static void AllowDialog(Graphics g, Rectangle r)
        {
            Body(g, r, Color.White);
            Rectangle d = new Rectangle(r.X + 12, r.Y + r.Height / 3, r.Width - 24, r.Height / 3);
            using (SolidBrush b = new SolidBrush(Soft)) g.FillRectangle(b, d);
            using (SolidBrush b = new SolidBrush(Dim))
            {
                g.FillRectangle(b, d.X + 8, d.Y + 10, d.Width - 16, 7);
                g.FillRectangle(b, d.X + 8, d.Y + 24, d.Width - 40, 7);
            }
            using (SolidBrush b = new SolidBrush(Accent))
                g.FillRectangle(b, d.X + d.Width / 2, d.Bottom - 26, d.Width / 2 - 8, 19);
        }

        static void TwoPhones(Graphics g, Rectangle box)
        {
            int ph = box.Height - 100;
            int pw = (int)(ph * 0.50);
            Rectangle a = new Rectangle(box.X + 12, box.Y + 60, pw, ph);
            Rectangle b = new Rectangle(box.Right - pw - 12, box.Y + 60, pw, ph);
            Body(g, a, Soft);
            Body(g, b, Color.White);
            using (Pen p = new Pen(Warn, 5f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                int cx = box.X + box.Width / 2, cy = box.Y + 24;
                g.DrawLine(p, cx, cy - 14, cx, cy + 8);
                g.DrawLine(p, cx, cy + 18, cx, cy + 19);
            }
        }

        static void Waves(Graphics g, Rectangle r)
        {
            Body(g, r, Color.White);
            using (Pen p = new Pen(Accent, 4f))
            {
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + 20;
                for (int i = 1; i <= 3; i++)
                {
                    int rad = i * 16;
                    g.DrawArc(p, cx - rad, cy - rad, rad * 2, rad * 2, 200, 140);
                }
                g.FillEllipse(new SolidBrush(Accent), cx - 4, cy - 4, 8, 8);
            }
        }

        static void Check(Graphics g, Rectangle r)
        {
            Body(g, r, Color.White);
            using (Pen p = new Pen(Good, 8f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawLine(p, cx - 22, cy, cx - 7, cy + 17);
                g.DrawLine(p, cx - 7, cy + 17, cx + 24, cy - 19);
            }
        }

        static void Cross(Graphics g, Rectangle r)
        {
            Body(g, r, Color.White);
            using (Pen p = new Pen(Accent, 8f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawLine(p, cx - 20, cy - 20, cx + 20, cy + 20);
                g.DrawLine(p, cx + 20, cy - 20, cx - 20, cy + 20);
            }
        }
    }

    // ========================================================
    // ОКНО НАСТРОЕК
    // ========================================================
    class SettingsForm : Form
    {
        readonly Engine engine;
        readonly bool phoneKnown;

        Panel content;
        ComboBox size, fps, bitrate, codec, audio, audioCodec;
        CheckBox control, screenOff, keepAwake, full, onTop, borderless, share, closeOnUnplug;
        Label shareHint;
        Label netState, netHint, controlHint;
        Button netButton, cableButton, saveButton, resetButton;
        bool loading = true;

        public SettingsForm(Engine e)
        {
            engine = e;
            phoneKnown = engine.Cfg.IsValid;

            Text = Paths.SettingsName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 640);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.None;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            // всё содержимое живёт в прокручиваемой области, иначе окно
            // не влезает на ноутбучные экраны 1366x768
            content = new Panel();
            content.SetBounds(0, 0, ClientSize.Width, ClientSize.Height - 74);
            content.AutoScroll = true;
            Controls.Add(content);

            int y = 18;

            Head("Картинка", ref y);
            size = Combo("Размер", ref y, new string[] {
                "Как есть, без уменьшения", "1280 точек", "1600 точек",
                "1920 точек (по умолчанию)", "2560 точек" });
            fps = Combo("Кадры в секунду", ref y, new string[] {
                "30", "45", "60 (по умолчанию)", "90", "120", "Без ограничения" });
            bitrate = Combo("Битрейт", ref y, new string[] {
                "4 Мбит — экономно", "6 Мбит", "8 Мбит", "12 Мбит",
                "16 Мбит", "20 Мбит (по умолчанию)", "30 Мбит" });
            codec = Combo("Кодек картинки", ref y, new string[] {
                "H.264 — самый быстрый отклик (по умолчанию)",
                "H.265 — чётче при том же битрейте, отклик чуть хуже",
                "AV1 — лучшее сжатие, но кодировщик редкий и медленный",
                "VP8 — старый, смысла почти нет",
                "VP9 — как H.265, но поддержка хуже" });

            y += 8;
            Head("Звук", ref y);
            audio = Combo("Куда идёт звук", ref y, new string[] {
                "Автоматически (по версии Android)",
                "В компьютер и в телефон",
                "Только в компьютер",
                "Выключить звук" });
            audioCodec = Combo("Кодек звука", ref y, new string[] {
                "Opus — лучший, меньше задержка (по умолчанию)",
                "AAC — если Opus заикается",
                "FLAC — без потерь, нужна широкая полоса",
                "Raw — несжатый, только для локали" });

            y += 8;
            Head("Окно на компьютере", ref y);
            full = Check("Открывать во весь экран", ref y);
            onTop = Check("Поверх остальных окон", ref y);
            borderless = Check("Без рамки", ref y);

            y += 8;
            Head("Управление", ref y);
            control = Check("Управлять телефоном с компьютера (мышь и клавиатура)", ref y);
            keepAwake = Check("Не давать телефону гаснуть", ref y);
            screenOff = Check("Погасить экран самого телефона", ref y);

            controlHint = new Label();
            controlHint.SetBounds(46, y, 580, 34);
            controlHint.ForeColor = Art.Dim;
            controlHint.Font = new Font("Segoe UI", 8.5f);
            controlHint.Text = "Два пункта выше работают только вместе с управлением — " +
                               "без него телефон их запрещает.";
            content.Controls.Add(controlHint);
            y += 40;

            y += 8;
            Head("Интернет для телефона  (не обязательно)", ref y);
            share = Check("Раздавать телефону интернет с этого компьютера", ref y);

            shareHint = new Label();
            shareHint.SetBounds(46, y, 590, 46);
            shareHint.ForeColor = Art.Dim;
            shareHint.Font = new Font("Segoe UI", 8.5f);
            shareHint.Text = "Телефон возьмёт интернет из кабеля, а не из Wi-Fi. " +
                             "На телефоне один раз появится окно про VPN — надо нажать «ОК».";
            content.Controls.Add(shareHint);
            y += 52;

            Head("Подключение", ref y);
            closeOnUnplug = Check("Закрывать программу, когда телефон отключают", ref y);

            Label unplugHint = new Label();
            unplugHint.SetBounds(46, y, 590, 32);
            unplugHint.ForeColor = Art.Dim;
            unplugHint.Font = new Font("Segoe UI", 8.5f);
            unplugHint.Text = "Закроется через " + Settings.UnplugDelaySec + " секунд после того, " +
                              "как кабель вынут. Если воткнуть обратно — отмена.";
            content.Controls.Add(unplugHint);
            y += 40;

            netState = new Label();
            netState.SetBounds(26, y, 600, 22);
            netState.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            content.Controls.Add(netState);
            y += 26;

            netHint = new Label();
            netHint.SetBounds(26, y, 610, 52);
            netHint.ForeColor = Art.Dim;
            netHint.Font = new Font("Segoe UI", 8.5f);
            content.Controls.Add(netHint);
            y += 56;

            netButton = Btn("Перейти на подключение по сети", 26, y, 300, 36, false);
            netButton.Click += delegate { OnGoNetwork(); };
            content.Controls.Add(netButton);

            cableButton = Btn("Вернуться на провод", 338, y, 220, 36, false);
            cableButton.Click += delegate { OnGoCable(); };
            content.Controls.Add(cableButton);
            y += 52;

            int footer = ClientSize.Height - 58;

            saveButton = Btn("Сохранить", 26, footer, 200, 44, true);
            saveButton.Click += delegate { Apply(); Close(); };
            Controls.Add(saveButton);

            resetButton = Btn("Сбросить к обычным", 240, footer, 220, 44, false);
            resetButton.Click += delegate { LoadFrom(new Settings()); };
            Controls.Add(resetButton);

            Button close = Btn("Отмена", 480, footer, 160, 44, false);
            close.Click += delegate { Close(); };
            Controls.Add(close);

            control.CheckedChanged += delegate { SyncControl(true); };
            share.CheckedChanged += delegate { OnShareChanged(); };

            LoadFrom(engine.Cfg.S);
            loading = false;
            SyncControl(false);
            SyncNetwork();
        }

        // ---- построители контролов -------------------------
        void Head(string text, ref int y)
        {
            Label l = new Label();
            l.SetBounds(24, y, 600, 24);
            l.Text = text;
            l.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            l.ForeColor = Art.Ink;
            content.Controls.Add(l);
            y += 28;
        }

        ComboBox Combo(string label, ref int y, string[] items)
        {
            Label l = new Label();
            l.SetBounds(40, y + 4, 180, 22);
            l.Text = label;
            l.ForeColor = Color.FromArgb(88, 94, 108);
            content.Controls.Add(l);

            ComboBox c = new ComboBox();
            c.SetBounds(230, y, 396, 26);
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            foreach (string i in items) c.Items.Add(i);
            c.SelectedIndex = 0;
            content.Controls.Add(c);

            y += 32;
            return c;
        }

        CheckBox Check(string label, ref int y)
        {
            CheckBox c = new CheckBox();
            c.SetBounds(42, y, 580, 24);
            c.Text = label;
            c.ForeColor = Color.FromArgb(88, 94, 108);
            content.Controls.Add(c);
            y += 26;
            return c;
        }

        Button Btn(string text, int x, int y, int w, int h, bool primary)
        {
            Button b = new Button();
            b.SetBounds(x, y, w, h);
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            if (primary)
            {
                b.BackColor = Art.Accent;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
                b.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            }
            else
            {
                b.BackColor = Color.White;
                b.ForeColor = Art.Ink;
                b.FlatAppearance.BorderColor = Art.Soft;
            }
            return b;
        }

        // ---- перенос значений ------------------------------
        static readonly int[] SizeValues = { 0, 1280, 1600, 1920, 2560 };
        static readonly int[] FpsValues = { 30, 45, 60, 90, 120, 0 };
        static readonly int[] RateValues = { 4, 6, 8, 12, 16, 20, 30 };

        void LoadFrom(Settings s)
        {
            loading = true;

            size.SelectedIndex = IndexOf(SizeValues, s.MaxSize, 3);
            fps.SelectedIndex = IndexOf(FpsValues, s.MaxFps, 2);
            bitrate.SelectedIndex = IndexOf(RateValues, s.BitRateM, 5);
            codec.SelectedIndex =
                s.Codec == "h265" ? 1 :
                s.Codec == "av1" ? 2 :
                s.Codec == "vp8" ? 3 :
                s.Codec == "vp9" ? 4 : 0;

            audio.SelectedIndex =
                s.Audio == "both" ? 1 :
                s.Audio == "pc" ? 2 :
                s.Audio == "off" ? 3 : 0;

            audioCodec.SelectedIndex =
                s.AudioCodec == "aac" ? 1 :
                s.AudioCodec == "flac" ? 2 :
                s.AudioCodec == "raw" ? 3 : 0;

            control.Checked = s.PcControl;
            keepAwake.Checked = s.KeepActive;
            screenOff.Checked = s.TurnScreenOff;
            full.Checked = s.Fullscreen;
            onTop.Checked = s.AlwaysOnTop;
            borderless.Checked = s.Borderless;
            share.Checked = s.ShareInternet;
            closeOnUnplug.Checked = s.CloseOnUnplug;

            loading = false;
            SyncControl(false);
        }

        static int IndexOf(int[] arr, int value, int fallback)
        {
            for (int i = 0; i < arr.Length; i++) if (arr[i] == value) return i;
            return fallback;
        }

        void Apply()
        {
            Settings s = engine.Cfg.S;

            s.MaxSize = SizeValues[size.SelectedIndex];
            s.MaxFps = FpsValues[fps.SelectedIndex];
            s.BitRateM = RateValues[bitrate.SelectedIndex];
            switch (codec.SelectedIndex)
            {
                case 1: s.Codec = "h265"; break;
                case 2: s.Codec = "av1"; break;
                case 3: s.Codec = "vp8"; break;
                case 4: s.Codec = "vp9"; break;
                default: s.Codec = "h264"; break;
            }

            // выбранного кодека может не быть аппаратно на этом телефоне
            if (s.Codec != "h264" && engine.Cfg.EncoderName(s.Codec).Length == 0 && phoneKnown)
            {
                MessageBox.Show(
                    "У этого телефона нет аппаратного кодировщика для выбранного кодека.\n\n" +
                    "Оставлю H.264 — он есть всегда и даёт самый быстрый отклик.",
                    Paths.SettingsName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                s.Codec = "h264";
                codec.SelectedIndex = 0;
            }

            switch (audio.SelectedIndex)
            {
                case 1: s.Audio = "both"; break;
                case 2: s.Audio = "pc"; break;
                case 3: s.Audio = "off"; break;
                default: s.Audio = "auto"; break;
            }

            switch (audioCodec.SelectedIndex)
            {
                case 1: s.AudioCodec = "aac"; break;
                case 2: s.AudioCodec = "flac"; break;
                case 3: s.AudioCodec = "raw"; break;
                default: s.AudioCodec = "opus"; break;
            }

            s.PcControl = control.Checked;
            s.KeepActive = control.Checked && keepAwake.Checked;
            s.TurnScreenOff = control.Checked && screenOff.Checked;
            s.Fullscreen = full.Checked;
            s.AlwaysOnTop = onTop.Checked;
            s.Borderless = borderless.Checked;
            s.ShareInternet = share.Checked && !s.NetworkMode;
            s.CloseOnUnplug = closeOnUnplug.Checked;

            engine.Cfg.SaveSettingsOnly();
        }

        // notify=true только когда галочку трогает человек: иначе подсказка
        // про бренд выскакивала при каждом открытии окна
        void SyncControl(bool notify)
        {
            keepAwake.Enabled = control.Checked;
            screenOff.Enabled = control.Checked;
            if (!control.Checked) { keepAwake.Checked = false; screenOff.Checked = false; }

            if (!notify || loading || !control.Checked) return;

            string note = Brands.ControlNote(engine.Cfg.Brand);
            if (note.Length > 0)
                MessageBox.Show(note, Paths.SettingsName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OnShareChanged()
        {
            if (loading || !share.Checked) return;

            DialogResult r1 = MessageBox.Show(
                "ПРЕДУПРЕЖДЕНИЕ 1 из 2\n\n" +
                "Это не нужно для показа экрана. Картинка работает и без этого.\n\n" +
                "На телефон поставится небольшая программа от авторов scrcpy. " +
                "При включении телефон покажет окно «Запрос на подключение VPN» — " +
                "надо нажать «ОК». Это не вирус: так Android разрешает пускать весь " +
                "трафик телефона через кабель на компьютер.\n\n" +
                "Пока раздача включена, интернет телефону даёт компьютер, а не Wi-Fi. " +
                "Если у компьютера интернет хуже, чем Wi-Fi у телефона, станет только хуже.\n\n" +
                "Продолжить?",
                Paths.SettingsName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (r1 != DialogResult.Yes) { share.Checked = false; return; }

            DialogResult r2 = MessageBox.Show(
                "ПРЕДУПРЕЖДЕНИЕ 2 из 2\n\n" +
                "Если во время игры выдернуть кабель, оборвётся не только картинка, " +
                "но и интернет на телефоне. Сетевая игра в этот момент отвалится, " +
                "и матч будет потерян.\n\n" +
                "Программа сама вернёт телефон на его Wi-Fi через пару секунд, но " +
                "соединение с игрой уже разорвётся.\n\n" +
                "Если матчи важны — оставь это выключенным.\n\n" +
                "Точно включить?",
                Paths.SettingsName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (r2 != DialogResult.Yes) share.Checked = false;
        }

        void SyncNetwork()
        {
            bool net = engine.Cfg.S.NetworkMode;

            share.Enabled = !net;
            if (net)
            {
                share.Checked = false;
                shareHint.Text = "Недоступно, пока подключение идёт по сети: раздавать интернет " +
                                 "по тому же каналу, по которому идёт картинка, нельзя.";
            }
            else
            {
                shareHint.Text = "Телефон возьмёт интернет из кабеля, а не из Wi-Fi. " +
                                 "На телефоне один раз появится окно про VPN — надо нажать «ОК».";
            }

            netState.Text = net
                ? "Сейчас: по сети  (" + engine.Cfg.S.NetworkAddr + ")"
                : "Сейчас: по проводу";
            netState.ForeColor = net ? Art.Warn : Art.Good;

            netHint.Text = net
                ? "Пока включена сеть, размер ограничен " + Settings.NetMaxSize +
                  " точками, а битрейт — " + Settings.NetMaxBitRate + " Мбит. " +
                  "Вернись на провод, чтобы снять ограничение."
                : "Обычный режим. Провод даёт лучшую картинку и самый быстрый отклик.";

            netButton.Enabled = !net && phoneKnown;
            cableButton.Enabled = net;

            bool locked = net;
            size.Enabled = !locked;
            bitrate.Enabled = !locked;
        }

        // ---- переход на сеть, с двумя предупреждениями -----
        void OnGoNetwork()
        {
            DialogResult r1 = MessageBox.Show(
                "ПРЕДУПРЕЖДЕНИЕ 1 из 2\n\n" +
                "Это режим для опытных. По сети картинка идёт заметно хуже, чем по проводу: " +
                "выше задержка, возможны рывки и рассыпание изображения. Для игр, где важна " +
                "реакция, это обычно не годится.\n\n" +
                "Телефон и компьютер должны быть в одной сети.\n\n" +
                "Продолжить?",
                Paths.SettingsName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (r1 != DialogResult.Yes) return;

            DialogResult r2 = MessageBox.Show(
                "ПРЕДУПРЕЖДЕНИЕ 2 из 2\n\n" +
                "Пока включена сеть:\n" +
                "  • размер картинки будет ограничен " + Settings.NetMaxSize + " точками;\n" +
                "  • битрейт будет ограничен " + Settings.NetMaxBitRate + " Мбит;\n" +
                "  • эти два пункта нельзя будет менять.\n\n" +
                "Режим слетает при перезагрузке телефона. Вернуть всё назад — только " +
                "подключив телефон кабелем.\n\n" +
                "Обычно этот режим не нужен: телефон может сидеть в интернете по Wi-Fi " +
                "и при этом быть подключённым кабелем.\n\n" +
                "Точно включить?",
                Paths.SettingsName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (r2 != DialogResult.Yes) return;

            netButton.Enabled = false;
            netState.Text = "Переключаю…";
            netState.ForeColor = Art.Dim;

            Thread t = new Thread(delegate ()
            {
                string address;
                string error = engine.SwitchToNetwork(out address);

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (error != null)
                        {
                            MessageBox.Show("Не получилось.\n\n" + error,
                                Paths.SettingsName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            SyncNetwork();
                            return;
                        }

                        engine.Cfg.S.NetworkMode = true;
                        engine.Cfg.S.NetworkAddr = address;
                        engine.Cfg.SaveSettingsOnly();

                        MessageBox.Show(
                            "Готово. Телефон отвечает по адресу " + address + ".\n\n" +
                            "Кабель можно отключить.\n\n" +
                            "Важно: после перезагрузки телефона этот режим слетает — чтобы " +
                            "включить его снова, понадобится кабель.",
                            Paths.SettingsName, MessageBoxButtons.OK, MessageBoxIcon.Information);

                        SyncNetwork();
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void OnGoCable()
        {
            engine.SwitchToCable();
            engine.Cfg.S.NetworkMode = false;
            engine.Cfg.SaveSettingsOnly();
            SyncNetwork();
        }
    }

    // ========================================================
    // ГЛАВНОЕ ОКНО
    // ========================================================
    class Wizard : Form
    {
        readonly Engine engine;

        Label title, hint, summary;
        Panel picture;
        Button playButton, settingsButton, yesButton, noButton;

        Stage stage = Stage.Starting;
        string pendingSerial = "";
        Transport transport = Transport.Unknown;

        Thread worker;
        volatile bool stop = false;
        volatile bool busy = false;
        volatile bool running = false;
        bool autoStart;
        volatile bool netBusy = false;
        Process scrcpy;

        bool everConnected = false;   // мастер уже доводили до конца
        bool settingsOpen = false;    // открыто модальное окно настроек
        bool autoClosing = false;     // закрываемся сами, вопросов не задаём
        DateTime goneSince = DateTime.MinValue;

        public Wizard(Engine e)
        {
            engine = e;
            autoStart = engine.Cfg.IsValid;

            Text = Paths.AppName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(790, 584);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.None;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            picture = new Panel();
            picture.SetBounds(28, 30, 215, 330);
            picture.Paint += delegate (object s, PaintEventArgs ev)
            {
                Art.Draw(ev.Graphics, new Rectangle(0, 0, picture.Width, picture.Height), stage);
            };
            Controls.Add(picture);

            title = new Label();
            title.SetBounds(268, 30, 495, 76);
            title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            title.ForeColor = Art.Ink;
            Controls.Add(title);

            hint = new Label();
            hint.SetBounds(268, 110, 495, 280);
            hint.ForeColor = Color.FromArgb(88, 94, 108);
            Controls.Add(hint);

            summary = new Label();
            summary.SetBounds(268, 396, 495, 60);
            summary.ForeColor = Art.Dim;
            summary.Font = new Font("Segoe UI", 8.5f);
            Controls.Add(summary);

            yesButton = MakeButton("Да, использовать этот", 268, 400, 300, 44, true);
            yesButton.Visible = false;
            yesButton.Click += delegate { OnApprove(); };
            Controls.Add(yesButton);

            noButton = MakeButton("Нет", 582, 400, 180, 44, false);
            noButton.Visible = false;
            noButton.Click += delegate { OnReject(); };
            Controls.Add(noButton);

            playButton = MakeButton("Показать экран", 268, 470, 300, 52, true);
            playButton.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            playButton.Enabled = false;
            playButton.Click += delegate { StartScrcpy(); };
            Controls.Add(playButton);

            settingsButton = MakeButton("Настройки", 582, 470, 180, 52, false);
            settingsButton.Click += delegate { OpenSettings(); };
            Controls.Add(settingsButton);

            engine.Progress = delegate (Stage st, string detail) { PostProgress(st, detail); };

            Render(new Snapshot());

            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();

            FormClosing += delegate (object s2, FormClosingEventArgs e2) { OnClosingWindow(e2); };
            Activated += delegate { ReloadSettings(); };
        }

        // Настройки могли поменять во втором окне, запущенном с ярлыка
        void ReloadSettings()
        {
            // пока крутится переключение раздачи/сети — не трогаем настройки,
            // иначе перечитанный с диска флаг разойдётся с реальным состоянием
            if (netBusy) return;

            try
            {
                Settings live = engine.Cfg.S;
                Config disk = Config.Load();
                Settings next = disk.S;

                // сетевой режим и раздача управляются кнопками в окне настроек
                // и уже применены к engine.Cfg — не откатываем их чтением файла
                next.NetworkMode = live.NetworkMode;
                next.NetworkAddr = live.NetworkAddr;
                next.ShareInternet = live.ShareInternet;

                engine.Cfg.S = next;
                if (stage == Stage.Ready) ShowSummary();
            }
            catch (Exception ex) { Log.Write("reload settings failed: " + ex.Message); }
        }

        void OnClosingWindow(FormClosingEventArgs e)
        {
            if (autoClosing)
            {
                Shutdown();
                return;
            }

            // раздача интернета активна — предупреждаем, что телефон вернётся на Wi-Fi
            if (Net.Active)
            {
                DialogResult r = MessageBox.Show(
                    "Сейчас телефон берёт интернет с компьютера.\n\n" +
                    "Если закрыть это окно, раздача выключится, и телефон вернётся " +
                    "на свой Wi-Fi или мобильный интернет.\n\n" +
                    "Закрыть?",
                    Paths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }

            Cursor = Cursors.WaitCursor;
            Shutdown();
            Cursor = Cursors.Default;
        }

        void Shutdown()
        {
            stop = true;
            StopScrcpy();
            try { Net.Stop(engine.Cfg.Target); } catch { }
        }

        Button MakeButton(string text, int x, int y, int w, int h, bool primary)
        {
            Button b = new Button();
            b.SetBounds(x, y, w, h);
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            if (primary)
            {
                b.BackColor = Art.Accent;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
                b.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            }
            else
            {
                b.BackColor = Color.White;
                b.ForeColor = Art.Ink;
                b.FlatAppearance.BorderColor = Art.Soft;
            }
            return b;
        }

        void OpenSettings()
        {
            settingsOpen = true;
            try
            {
                using (SettingsForm f = new SettingsForm(engine))
                {
                    f.ShowDialog(this);
                }
            }
            finally { settingsOpen = false; }
            if (stage == Stage.Ready) ShowSummary();
        }

        // ----- фоновый опрос --------------------------------
        void Loop()
        {
            while (!stop)
            {
                if (running || busy) { Sleep(700); continue; }

                Snapshot s;
                try { s = engine.Poll(); }
                catch (Exception ex)
                {
                    s = new Snapshot();
                    s.Stage = Stage.Failed;
                    s.Detail = ex.Message;
                    Log.Write("poll crashed: " + ex);
                }

                Post(delegate { Render(s); });
                Sleep(1000);
            }
        }

        void Sleep(int ms)
        {
            int steps = ms / 100;
            for (int i = 0; i < steps && !stop; i++) Thread.Sleep(100);
        }

        void Post(MethodInvoker action)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(action);
            }
            catch { }
        }

        void PostProgress(Stage st, string detail)
        {
            Post(delegate
            {
                if (running || busy) return;
                Snapshot s = new Snapshot();
                s.Stage = st;
                s.Model = detail;
                s.Detail = detail;
                Render(s);
            });
        }

        // ----- отрисовка ------------------------------------
        void Render(Snapshot s)
        {
            if (running) return;

            if (stage != s.Stage) { stage = s.Stage; picture.Invalidate(); }
            if (s.Transport != Transport.Unknown) transport = s.Transport;
            if (s.Serial.Length > 0) pendingSerial = s.Serial;

            bool confirm = (s.Stage == Stage.DifferentPhone);
            bool wrong = (s.Stage == Stage.WrongPhone);

            switch (s.Stage)
            {
                case Stage.Starting:
                    Set("Запускаюсь…", "Секунду.");
                    break;

                case Stage.Extracting:
                    Set("Готовлю программу…",
                        "Это делается один раз, при первом запуске. Несколько секунд.");
                    break;

                case Stage.NoPhone:
                    Set("Подключи телефон к компьютеру",
                        "Возьми USB-кабель и воткни телефон в компьютер.\n\n" +
                        "Кабель должен уметь передавать данные, а не только заряжать. Если телефон " +
                        "заряжается, но здесь ничего не меняется — попробуй другой кабель.\n\n" +
                        "Через переходник или хаб тоже работает: телефон нужно втыкать в порт " +
                        "для данных, обычно он помечен значком SS. Порт только для зарядки " +
                        "не подойдёт.\n\n" +
                        "Окно закрывать не надо, оно само всё заметит.");
                    break;

                case Stage.AppleDevice:
                    Set("Это " + (s.Detail.Length > 0 ? s.Detail : "устройство Apple") +
                            " — с ним так нельзя",
                        (s.UsbName.Length > 0 ? "Подключено: " + s.UsbName + "\n\n" : "") +
                        "Программа выводит на компьютер экран телефонов на Android. iPhone и iPad " +
                        "так не умеют: Apple закрыла эту возможность в самой iOS, обойти это " +
                        "не может ни одна программа.\n\n" +
                        "Что можно вместо этого:\n" +
                        "      • на Mac — кабель и приложение QuickTime Player;\n" +
                        "      • Apple TV и AirPlay;\n" +
                        "      • на Windows — программы, которые ловят AirPlay по Wi-Fi.\n\n" +
                        "Ничего не сломано. Отключи это устройство и подключи Android-телефон — " +
                        "окно переключится само.");
                    break;

                case Stage.PhoneNoDebug:
                    Set("Устройство вижу. Осталась настройка на нём",
                        (s.UsbName.Length > 0 ? "Подключено: " + s.UsbName + "\n\n" : "") +
                        "Если это Android-телефон, открой на нём:\n\n" +
                        Brands.DevModePath(s.Brand) + "\n\n" +
                        "Там включить «Отладка по USB».\n\n" +
                        "А если это не телефон — фотоаппарат, плеер, флешка — отключи его " +
                        "и подключи телефон.");
                    break;

                case Stage.Unauthorized:
                    Set("Посмотри на экран телефона",
                        "На нём появилось окно «Разрешить отладку по USB?».\n\n" +
                        "Нажми «Разрешить».\n\n" +
                        "Если есть галочка «Всегда разрешать с этого компьютера» — поставь её, " +
                        "тогда телефон больше не будет спрашивать.\n\n" +
                        "Окна нет? Разблокируй телефон, вытащи кабель и воткни снова.");
                    break;

                case Stage.Authorizing:
                    Set("Секунду…", "Телефон и компьютер договариваются.");
                    break;

                case Stage.Offline:
                    Set("Телефон не отвечает",
                        "Разблокируй телефон, вытащи кабель и воткни снова.\n\n" +
                        "Если телефон подключён через хаб — попробуй другой порт на хабе " +
                        "или воткни телефон напрямую в компьютер.");
                    break;

                case Stage.Multiple:
                    Set("Подключено несколько устройств",
                        "Компьютер видит сразу несколько:\n\n" + s.Detail + "\n\n" +
                        "Отключи лишние и оставь только нужный телефон.");
                    break;

                case Stage.AdbConflict:
                    Set("Мешает другая программа",
                        "На компьютере уже работает другая версия ADB — обычно её запускают " +
                        "Android Studio или программы для прошивки телефонов.\n\n" +
                        "Закрой их, вытащи и воткни кабель. Если не помогает — перезагрузи компьютер.");
                    break;

                case Stage.DifferentPhone:
                    Set("Это другой телефон",
                        "Раньше здесь был настроен:\n" +
                        "      " + Describe(s.SavedModel, s.SavedSerial) + "\n\n" +
                        "А сейчас подключён:\n" +
                        "      " + Describe(s.Model, s.Serial) + "\n\n" +
                        "Переключиться на новый? Программа заново подберёт под него настройки.\n\n" +
                        "Если это не то устройство — нажми «Нет».");
                    break;

                case Stage.WrongPhone:
                    Set("Подключён не тот телефон",
                        "Сейчас подключён:\n" +
                        "      " + Describe(s.Model, s.Serial) + "\n\n" +
                        (s.SavedModel.Length > 0
                            ? "А настроен был:\n      " + Describe(s.SavedModel, s.SavedSerial) + "\n\n"
                            : "") +
                        "Отключи это устройство и подключи нужный телефон.\n\n" +
                        "Или нажми кнопку ниже, чтобы всё-таки работать с этим.");
                    break;

                case Stage.NetworkSearching:
                    Set("Ищу телефон в сети…",
                        "Адрес: " + s.Detail + "\n\n" +
                        "Телефон должен быть включён и находиться в той же сети, что и компьютер.");
                    break;

                case Stage.NetworkLost:
                    Set("Телефон в сети не отвечает",
                        "Адрес: " + s.Detail + "\n\n" +
                        "Проверь, что телефон включён и подключён к той же сети.\n\n" +
                        "Если не помогает — подключи телефон кабелем и в настройках нажми " +
                        "«Вернуться на провод».");
                    break;

                case Stage.Probing:
                    Set("Проверяю телефон…",
                        (s.Model.Length > 0 ? s.Model + "\n\n" : "") +
                        "Смотрю, какие кодировщики он умеет, и выбираю подходящий. " +
                        "Несколько секунд, и только при первом подключении этого телефона.");
                    break;

                case Stage.NoEncoder:
                    Set("Этот телефон не подойдёт",
                        (s.Detail.Length > 0 ? s.Detail + "\n\n" : "") +
                        "У него нет аппаратного кодировщика видео. Программный грузит процессор " +
                        "телефона, тот греется и начинает тормозить — поэтому запускать не буду.");
                    break;

                case Stage.Ready:
                    Set("Готово",
                        (s.Model.Length > 0 ? s.Model + "\n" : "") +
                        (s.Android.Length > 0 ? "Android " + s.Android + "\n" : "") +
                        TransportLine() + "\n" +
                        "Кодировщик подобран сам: " + engine.Cfg.EncoderH264 + "\n\n" +
                        "Нажми «Показать экран», а потом запусти на телефоне то, что нужно.\n\n" +
                        "Картинка появится в отдельном окне на компьютере. Если телефон греется — " +
                        "уменьши размер и битрейт в настройках.");
                    ShowSummary();
                    break;

                case Stage.Running:
                    Set("Идёт трансляция", RunningText());
                    break;

                case Stage.Closed:
                    Set("Трансляция закрыта", "Нажми «Показать экран», чтобы включить снова.");
                    break;

                case Stage.Failed:
                    Set("Не получилось", s.Detail);
                    break;
            }

            yesButton.Visible = confirm || wrong;
            noButton.Visible = confirm;
            yesButton.Text = wrong ? "Всё равно использовать этот" : "Да, использовать этот";
            yesButton.Width = wrong ? 340 : 300;

            summary.Visible = (s.Stage == Stage.Ready);
            playButton.Enabled = (s.Stage == Stage.Ready || s.Stage == Stage.Closed) && !busy;

            if (s.Stage == Stage.Ready && autoStart && !busy)
            {
                autoStart = false;
                StartScrcpy();
            }

            if (s.Stage == Stage.Ready) Housekeeping.RunOnce();

            SyncSharing(s.Stage);
            SyncUnplug(s.Stage);
        }

        // Закрываемся сами, когда кабель вынули. Три ограничителя:
        //  - только Stage.NoPhone, то есть на шине вообще ничего нет.
        //    «Вытащи и воткни снова» из подсказок мастера сюда не попадает:
        //    там телефон остаётся виден Windows;
        //  - только если телефон хоть раз довели до готовности, иначе окно
        //    захлопнется посреди настройки;
        //  - с задержкой: воткнули обратно — отсчёт отменяется.
        void SyncUnplug(Stage st)
        {
            if (st == Stage.Ready || st == Stage.Running) everConnected = true;

            bool arm = engine.Cfg.S.CloseOnUnplug
                    && everConnected
                    && !settingsOpen
                    && !busy
                    && !netBusy
                    && st == Stage.NoPhone;

            if (!arm)
            {
                goneSince = DateTime.MinValue;
                return;
            }

            if (goneSince == DateTime.MinValue)
            {
                goneSince = DateTime.Now;
                return;
            }

            int left = Settings.UnplugDelaySec - (int)(DateTime.Now - goneSince).TotalSeconds;

            if (left > 0)
            {
                Set("Телефон отключён",
                    "Программа закроется через " + left + " сек.\n\n" +
                    "Если это случайно — просто воткни кабель обратно, и закрытие отменится.");
                return;
            }

            autoClosing = true;
            Log.Write("closing: cable unplugged");
            Close();
        }

        static bool DeviceGone(Stage st)
        {
            switch (st)
            {
                case Stage.NoPhone:
                case Stage.PhoneNoDebug:
                case Stage.AppleDevice:
                case Stage.Unauthorized:
                case Stage.Offline:
                case Stage.Multiple:
                case Stage.AdbConflict:
                case Stage.DifferentPhone:
                case Stage.WrongPhone:
                case Stage.NetworkLost:
                case Stage.NoEncoder:
                case Stage.Failed:
                    return true;
                default:
                    return false;
            }
        }

        // Телефон пропал, а VPN на нём поднята -> он остался вообще без интернета.
        // Снимаем сами, не дожидаясь, пока человек это обнаружит.
        void SyncSharing(Stage st)
        {
            if (netBusy) return;

            bool want = engine.Cfg.S.ShareInternet && !engine.Cfg.S.NetworkMode;
            string serial = engine.Cfg.Target;

            if (st == Stage.Ready && want && !Net.Active) { RunSharing(true, serial); return; }
            if (st == Stage.Ready && !want && Net.Active) { RunSharing(false, serial); return; }
            if (DeviceGone(st) && Net.Active) RunSharing(false, serial);
        }

        void RunSharing(bool on, string serial)
        {
            netBusy = true;
            Thread t = new Thread(delegate ()
            {
                string error = null;
                if (on) error = Net.Start(serial);
                else Net.Stop(serial);

                Post(delegate
                {
                    netBusy = false;
                    if (error != null)
                    {
                        engine.Cfg.S.ShareInternet = false;
                        engine.Cfg.SaveSettingsOnly();
                        MessageBox.Show(error, Paths.AppName,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    if (stage == Stage.Ready) ShowSummary();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        string TransportLine()
        {
            switch (transport)
            {
                case Transport.UsbDirect: return "Подключение: кабель напрямую";
                case Transport.UsbHub: return "Подключение: кабель через переходник (хаб)";
                case Transport.Network: return "Подключение: по сети";
                default: return "";
            }
        }

        void ShowSummary()
        {
            Settings s = engine.Cfg.S;
            string sz = s.EffectiveMaxSize == 0 ? "без уменьшения" : s.EffectiveMaxSize + " точек";
            string f = s.MaxFps == 0 ? "без ограничения" : s.MaxFps + " к/с";
            string a =
                s.Audio == "off" ? "звук выключен" :
                s.Audio == "pc" ? "звук в компьютер" :
                s.Audio == "both" ? "звук в компьютер и телефон" : "звук автоматически";

            summary.Text = sz + "   •   " + f + "   •   " + s.EffectiveBitRate + " Мбит   •   " +
                           s.Codec.ToUpperInvariant().Replace("H264","H.264").Replace("H265","H.265") +
                           "   •   " + a +
                           (s.PcControl ? "   •   управление с ПК" : "") +
                           (Net.Active ? "\nИнтернет телефону раздаётся с компьютера — " +
                                         "пока это окно открыто" : "");
        }

        static string RunningText()
        {
            return "Окно с картинкой открыто. Запусти на телефоне то, что нужно показать.\n\n" +
                   "Это окно можно закрыть — на трансляцию это не повлияет.";
        }

        static string Describe(string model, string serial)
        {
            string m = (model == null || model.Length == 0) ? "Android-устройство" : model;
            if (serial != null && serial.Length > 0) m += "   (" + serial + ")";
            return m;
        }

        void Set(string big, string small)
        {
            title.Text = big;
            hint.Text = small;
        }

        void OnApprove()
        {
            if (pendingSerial.Length == 0) return;
            engine.ApprovePhone(pendingSerial);
            yesButton.Visible = false;
            noButton.Visible = false;
            Snapshot s = new Snapshot();
            s.Stage = Stage.Probing;
            Render(s);
        }

        void OnReject()
        {
            if (pendingSerial.Length == 0) return;
            engine.RejectPhone(pendingSerial);
            yesButton.Visible = false;
            noButton.Visible = false;
        }

        void StartScrcpy()
        {
            if (busy || running) return;

            try { if (scrcpy != null && !scrcpy.HasExited) { running = true; return; } }
            catch { scrcpy = null; }

            busy = true;
            playButton.Enabled = false;
            summary.Visible = false;
            Set("Запускаю…", "Секунду.");

            Thread t = new Thread(delegate ()
            {
                string error;
                Process p = engine.Launch(out error);

                Post(delegate
                {
                    busy = false;

                    if (p == null)
                    {
                        running = false;
                        stage = Stage.Failed;
                        picture.Invalidate();
                        Set("Не удалось запустить", error);
                        playButton.Enabled = true;
                        return;
                    }

                    scrcpy = p;
                    running = true;
                    stage = Stage.Running;
                    picture.Invalidate();
                    Set("Идёт трансляция", RunningText());

                    p.Exited += delegate { Post(OnScrcpyClosed); };
                    try { if (p.HasExited) Post(OnScrcpyClosed); }
                    catch { }
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void OnScrcpyClosed()
        {
            if (!running && scrcpy == null) return;
            running = false;
            scrcpy = null;
            stage = Stage.Closed;
            picture.Invalidate();
            Set("Трансляция закрыта", "Нажми «Показать экран», чтобы включить снова.");
            playButton.Enabled = true;
        }

        // При закрытии окна дочерний scrcpy надо погасить принудительно, иначе
        // он останется висеть без окна управления и будет держать телефон.
        void StopScrcpy()
        {
            Process p = scrcpy;
            scrcpy = null;
            running = false;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
        }
    }

    // ========================================================
    // КОПИЯ ПРОГРАММЫ + ДВА ЯРЛЫКА
    // ========================================================
    static class Housekeeping
    {
        static volatile bool completed = false;
        static int busy = 0;
        static DateTime lastTry = DateTime.MinValue;

        public static void RunOnce()
        {
            if (completed) return;
            if ((DateTime.Now - lastTry).TotalSeconds < 20) return;
            if (Interlocked.Exchange(ref busy, 1) != 0) return;

            lastTry = DateTime.Now;

            Thread t = new Thread(delegate ()
            {
                bool copied = false;
                try
                {
                    string self = Application.ExecutablePath;
                    string target = self;

                    if (string.Equals(self, Paths.SelfCopy, StringComparison.OrdinalIgnoreCase))
                    {
                        copied = true;
                    }
                    else
                    {
                        try
                        {
                            Directory.CreateDirectory(Paths.Root);
                            File.Copy(self, Paths.SelfCopy, true);
                            target = Paths.SelfCopy;
                            copied = true;
                        }
                        catch (Exception ex)
                        {
                            // чаще всего копия занята вторым окном; попробуем позже,
                            // иначе ярлык укажет в «Загрузки», откуда файл могут удалить
                            Log.Write("self-copy failed: " + ex.Message);
                            if (File.Exists(Paths.SelfCopy)) { target = Paths.SelfCopy; copied = true; }
                        }
                    }

                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    if (Directory.Exists(desktop))
                    {
                        Make(desktop, Paths.AppName, target, "", "Экран телефона на компьютере");
                        Make(desktop, Paths.SettingsName, target, "--settings",
                             "Настройки трансляции экрана");
                    }

                    completed = copied;
                }
                catch (Exception ex) { Log.Write("housekeeping failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void Make(string desktop, string name, string target, string args, string description)
        {
            try
            {
                string link = Path.Combine(desktop, name + ".lnk");

                Type wsh = Type.GetTypeFromProgID("WScript.Shell");
                if (wsh == null) return;

                object shell = Activator.CreateInstance(wsh);
                object lnk = wsh.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                                              null, shell, new object[] { link });
                Type lt = lnk.GetType();
                SetProp(lt, lnk, "TargetPath", target);
                if (args.Length > 0) SetProp(lt, lnk, "Arguments", args);
                SetProp(lt, lnk, "WorkingDirectory", Paths.Root);
                SetProp(lt, lnk, "Description", description);
                SetProp(lt, lnk, "IconLocation", target + ",0");
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);

                Log.Write("shortcut: " + link);
            }
            catch (Exception ex) { Log.Write("shortcut failed: " + ex.Message); }
        }

        static void SetProp(Type t, object o, string prop, string value)
        {
            t.InvokeMember(prop, BindingFlags.SetProperty, null, o, new object[] { value });
        }
    }

    // ========================================================
    // ТОЧКА ВХОДА
    // ========================================================
    static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr h, int cmd);

        const int SW_RESTORE = 9;

        static Mutex instanceLock;   // держим до выхода, иначе замок отпустится

        // Второй запуск не нужен: два окна дрались бы за adb, могли поднять
        // две трансляции и два раздатчика интернета на один порт.
        static bool ClaimSingleInstance(string key, string windowTitle)
        {
            bool created;
            try { instanceLock = new Mutex(true, "Local\\PhoneScreen_" + key, out created); }
            catch { return true; }

            if (created) return true;

            try
            {
                IntPtr h = FindWindow(null, windowTitle);
                if (h != IntPtr.Zero) { ShowWindow(h, SW_RESTORE); SetForegroundWindow(h); }
            }
            catch { }

            Log.Write("second instance (" + key + ") refused");
            return false;
        }

        [STAThread]
        static void Main(string[] rawArgs)
        {
            Log.Write("start: " + Application.ExecutablePath);

            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            { Fatal(e.Exception); };

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            { Fatal(e.ExceptionObject as Exception); };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool settingsOnly = false;
            foreach (string a in rawArgs)
                if (a.Equals("--settings", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/settings", StringComparison.OrdinalIgnoreCase)) settingsOnly = true;

            if (!ClaimSingleInstance(settingsOnly ? "settings" : "main",
                                     settingsOnly ? Paths.SettingsName : Paths.AppName)) return;

            try
            {
                Engine engine = new Engine();

                if (settingsOnly)
                {
                    if (!engine.EnsureInstalled())
                    {
                        MessageBox.Show(
                            "Не удалось подготовить программу.\n\n" +
                            "Запусти сначала «" + Paths.AppName + "» — он всё установит, " +
                            "потом открой настройки.",
                            Paths.SettingsName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Application.Run(new SettingsForm(engine));
                }
                else
                {
                    Application.Run(new Wizard(engine));
                }
            }
            catch (Exception ex) { Fatal(ex); }
            finally { GC.KeepAlive(instanceLock); }
        }

        static void Fatal(Exception ex)
        {
            Log.Write("FATAL: " + (ex == null ? "unknown" : ex.ToString()));
            try
            {
                MessageBox.Show(
                    "Что-то пошло не так.\n\n" + (ex == null ? "" : ex.Message),
                    Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
