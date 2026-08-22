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
    // ЯЗЫК / LANGUAGE
    //   ru — русский, en — English.
    //   По умолчанию: русская Windows -> ru, иначе -> en.
    //   Пользователь может переключить вручную в настройках.
    // ========================================================
    static class Lang
    {
        public static bool Ru = SystemIsRussian();

        public static string T(string ruText, string enText)
        {
            return Ru ? ruText : enText;
        }

        public static bool SystemIsRussian()
        {
            try
            {
                return System.Globalization.CultureInfo.CurrentUICulture
                    .TwoLetterISOLanguageName == "ru";
            }
            catch { return false; }
        }

        // применить язык из настроек: "ru"/"en"/"" (по системе)
        public static void Apply(string code)
        {
            if (code == "ru") Ru = true;
            else if (code == "en") Ru = false;
            else Ru = SystemIsRussian();
        }
    }

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

        public static string AppName
        {
            get { return Lang.T("Экран телефона", "Phone Screen"); }
        }
        public static string SettingsName
        {
            get { return Lang.T("Экран телефона — настройки", "Phone Screen — Settings"); }
        }

        // Ссылки на используемые проекты / links to the projects we use
        public const string UrlScrcpy = "https://github.com/Genymobile/scrcpy";
        public const string UrlGnirehtet = "https://github.com/Genymobile/gnirehtet";
        public const string UrlProject = "https://github.com/maximspr/ScrcpyForStreamer";
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
                    return Lang.T(
                        "      Настройки → О телефоне\n" +
                        "      7 раз нажать на «Версия HyperOS» (или «Версия MIUI»)\n\n" +
                        "Дальше: Настройки → Расширенные настройки → Для разработчиков",
                        "      Settings → About phone\n" +
                        "      Tap «HyperOS version» (or «MIUI version») 7 times\n\n" +
                        "Then: Settings → Additional settings → Developer options");
                case "samsung":
                    return Lang.T(
                        "      Настройки → Сведения о телефоне → Сведения о ПО\n" +
                        "      7 раз нажать на «Номер сборки»\n\n" +
                        "Дальше: Настройки → Параметры разработчика",
                        "      Settings → About phone → Software information\n" +
                        "      Tap «Build number» 7 times\n\n" +
                        "Then: Settings → Developer options");
                case "vivo":
                    return Lang.T(
                        "      Настройки → Ещё настройки → О телефоне\n" +
                        "      7 раз нажать на «Версия ПО»\n\n" +
                        "Дальше: Настройки → Ещё настройки → Для разработчиков",
                        "      Settings → More settings → About phone\n" +
                        "      Tap «Software version» 7 times\n\n" +
                        "Then: Settings → More settings → Developer options");
                case "oppo":
                case "realme":
                case "oneplus":
                    return Lang.T(
                        "      Настройки → О телефоне → Версия\n" +
                        "      7 раз нажать на «Номер сборки»\n\n" +
                        "Дальше: Настройки → Дополнительные настройки → Для разработчиков",
                        "      Settings → About device → Version\n" +
                        "      Tap «Build number» 7 times\n\n" +
                        "Then: Settings → Additional settings → Developer options");
                case "huawei":
                case "honor":
                    return Lang.T(
                        "      Настройки → О телефоне\n" +
                        "      7 раз нажать на «Номер сборки»\n\n" +
                        "Дальше: Настройки → Система → Для разработчиков",
                        "      Settings → About phone\n" +
                        "      Tap «Build number» 7 times\n\n" +
                        "Then: Settings → System → Developer options");
                case "meizu":
                    return Lang.T(
                        "      Настройки → Об устройстве\n" +
                        "      7 раз нажать на «Номер сборки»\n\n" +
                        "Дальше: Настройки → Спец. возможности → Для разработчиков",
                        "      Settings → About device\n" +
                        "      Tap «Build number» 7 times\n\n" +
                        "Then: Settings → Accessibility → Developer options");
                default:
                    return Lang.T(
                        "      Настройки → О телефоне\n" +
                        "      7 раз нажать на «Номер сборки»\n\n" +
                        "Дальше: Настройки → Система → Для разработчиков\n" +
                        "(на части телефонов — сразу Настройки → Для разработчиков)",
                        "      Settings → About phone\n" +
                        "      Tap «Build number» 7 times\n\n" +
                        "Then: Settings → System → Developer options\n" +
                        "(on some phones just Settings → Developer options)");
            }
        }

        public static string ControlNote(string key)
        {
            switch (key)
            {
                case "xiaomi":
                    return Lang.T(
                        "На Xiaomi / Redmi / POCO для управления с компьютера нужен ещё один пункт: " +
                        "«Отладка по USB (Настройки безопасности)», и после него — перезагрузка телефона.\n\n" +
                        "Без управления всё работает и так.",
                        "On Xiaomi / Redmi / POCO, controlling from the PC needs one more option: " +
                        "«USB debugging (Security settings)», followed by a phone reboot.\n\n" +
                        "Without control everything still works.");
                case "oppo":
                case "realme":
                case "oneplus":
                    return Lang.T(
                        "На ColorOS / realme UI для управления с компьютера нужно включить " +
                        "«Отключить контроль разрешений» — в самом низу меню «Для разработчиков».",
                        "On ColorOS / realme UI, controlling from the PC needs " +
                        "«Disable permission monitoring» at the bottom of Developer options.");
                case "vivo":
                    return Lang.T(
                        "На vivo / iQOO для управления с компьютера может понадобиться пункт " +
                        "«Отладка по USB (изменение настроек)» в меню разработчика.",
                        "On vivo / iQOO, controlling from the PC may need " +
                        "«USB debugging (modify settings)» in Developer options.");
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
        public string Language = "";   // "" = по системе, "ru", "en"
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
                        case "LANGUAGE": c.S.Language = v; break;
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
                sb.AppendLine("LANGUAGE=" + S.Language);

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
                    throw new Exception(Lang.T("Внутри программы не найден архив scrcpy — сборка повреждена.", "The scrcpy archive is missing inside the program — the build is corrupted."));

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
        public string AppleKind = Lang.T("устройство Apple", "Apple device");
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
            return Lang.T("устройство Apple", "Apple device");
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
                    s.Detail = Lang.T("Не удалось распаковать программу в папку:\n", "Could not unpack the program into:\n") + Paths.ScrcpyDir;
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
                if (name.Length == 0) name = Lang.T("Android-устройство", "Android device");
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
            if (found.Count == 0) return Lang.T("Телефон не сообщил ни одного видеокодировщика.", "The phone reported no video encoders at all.");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Lang.T("Телефон предлагает только программные кодировщики:", "The phone offers only software encoders:"));
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
                return Lang.T("Телефон сейчас не подключён проводом. Сначала подключи его кабелем.", "The phone is not connected by cable right now. Connect it with a cable first.");

            string ip = FindDeviceIp(Cfg.UsbSerial);
            if (ip.Length == 0)
                return Lang.T("Не удалось узнать адрес телефона в сети.\n\nПроверь, что телефон подключён к той же сети, что и компьютер — по Wi-Fi или через переходник с Ethernet.", "Could not find the phone's network address.\n\nMake sure the phone is on the same network as the PC — over Wi-Fi or through an Ethernet adapter.");

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

            return Lang.T("Телефон не отозвался по адресу ", "The phone did not respond at ") + addr + Lang.T(".\n\nОбычно это значит, что телефон и компьютер в разных сетях.", ".\n\nUsually this means the phone and the PC are on different networks.");
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
                return Lang.T("Телефон не разрешил управление с компьютера.\n\nВ настройках выключи «Управлять телефоном с компьютера» — так всё заработает.", "The phone refused control from the PC.\n\nIn settings, turn off «Control the phone from the PC» — then it will work.");

            if (t.IndexOf("Could not find encoder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("InvalidEncoder", StringComparison.OrdinalIgnoreCase) >= 0)
                return Lang.T("Выбранный кодировщик телефону не подошёл.\n\nОтключи и подключи телефон заново — программа подберёт другой.", "The chosen encoder did not work on the phone.\n\nUnplug and replug the phone — the program will pick another one.");

            if (t.IndexOf("Device disconnected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("device not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return Lang.T("Телефон отключился. Проверь кабель.", "The phone disconnected. Check the cable.");

            string[] lines = t.Replace("\r", "").Split('\n');
            StringBuilder last = new StringBuilder();
            for (int i = Math.Max(0, lines.Length - 5); i < lines.Length; i++)
                if (lines[i].Trim().Length > 0) last.AppendLine(lines[i].Trim());

            return Lang.T("Трансляция закрылась сразу после запуска.\n\n", "The stream closed right after starting.\n\n") + last.ToString().Trim();
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
                if (zip == null) throw new Exception(Lang.T("Внутри программы нет gnirehtet.", "gnirehtet is missing inside the program."));
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
                        return Lang.T(
                            "Не удалось поставить на телефон помощника для интернета.\n\n" +
                            "Возможно, телефон запрещает установку через USB. На Xiaomi это " +
                            "пункт «Установка через USB» в меню для разработчиков.",
                            "Could not install the internet helper on the phone.\n\n" +
                            "The phone may block USB installs. On Xiaomi this is " +
                            "«Install via USB» in Developer options.");
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
                    return Lang.T("Не удалось запустить раздачу интернета: ", "Could not start internet sharing: ") + ex.Message;
                }

                Thread.Sleep(1500);

                if (relay.HasExited)
                    return Lang.T("Раздача интернета не запустилась.\n\nСкорее всего, порт 31416 занят другой программой.", "Internet sharing did not start.\n\nMost likely port 31416 is taken by another program.");

                string start = RunGn("start " + serial, 30000);
                if (start.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    start.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Kill();
                    return Lang.T("Телефон не принял раздачу интернета.\n\nПроверь, что на его экране нажато «ОК» в окне про VPN-подключение.", "The phone did not accept internet sharing.\n\nMake sure «OK» was tapped in the VPN connection dialog on its screen.");
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
        ComboBox size, fps, bitrate, codec, audio, audioCodec, language;
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

            Head(Lang.T("Язык / Language", "Language / Язык"), ref y);
            language = Combo(Lang.T("Язык программы", "Program language"), ref y, new string[] {
                Lang.T("Как в системе", "Follow system"),
                "Русский",
                "English" });

            y += 8;
            Head(Lang.T("Картинка", "Video"), ref y);
            size = Combo(Lang.T("Размер", "Size"), ref y, new string[] {
                Lang.T("Как есть, без уменьшения", "As is, no downscale"),
                Lang.T("1280 точек", "1280 px"), Lang.T("1600 точек", "1600 px"),
                Lang.T("1920 точек (по умолчанию)", "1920 px (default)"),
                Lang.T("2560 точек", "2560 px") });
            fps = Combo(Lang.T("Кадры в секунду", "Frames per second"), ref y, new string[] {
                "30", "45", Lang.T("60 (по умолчанию)", "60 (default)"), "90", "120",
                Lang.T("Без ограничения", "No limit") });
            bitrate = Combo(Lang.T("Битрейт", "Bit rate"), ref y, new string[] {
                Lang.T("4 Мбит — экономно", "4 Mbps — light"), Lang.T("6 Мбит", "6 Mbps"),
                Lang.T("8 Мбит", "8 Mbps"), Lang.T("12 Мбит", "12 Mbps"),
                Lang.T("16 Мбит", "16 Mbps"), Lang.T("20 Мбит (по умолчанию)", "20 Mbps (default)"),
                Lang.T("30 Мбит", "30 Mbps") });
            codec = Combo(Lang.T("Кодек картинки", "Video codec"), ref y, new string[] {
                Lang.T("H.264 — самый быстрый отклик (по умолчанию)", "H.264 — lowest latency (default)"),
                Lang.T("H.265 — чётче при том же битрейте, отклик чуть хуже", "H.265 — sharper at same bit rate, slightly higher latency"),
                Lang.T("AV1 — лучшее сжатие, но кодировщик редкий и медленный", "AV1 — best compression, but the encoder is rare and slow"),
                Lang.T("VP8 — старый, смысла почти нет", "VP8 — old, little reason to use"),
                Lang.T("VP9 — как H.265, но поддержка хуже", "VP9 — like H.265, but worse support") });

            y += 8;
            Head(Lang.T("Звук", "Audio"), ref y);
            audio = Combo(Lang.T("Куда идёт звук", "Audio output"), ref y, new string[] {
                Lang.T("Автоматически (по версии Android)", "Automatic (by Android version)"),
                Lang.T("В компьютер и в телефон", "PC and phone"),
                Lang.T("Только в компьютер", "PC only"),
                Lang.T("Выключить звук", "Mute audio") });
            audioCodec = Combo(Lang.T("Кодек звука", "Audio codec"), ref y, new string[] {
                Lang.T("Opus — лучший, меньше задержка (по умолчанию)", "Opus — best, lower latency (default)"),
                Lang.T("AAC — если Opus заикается", "AAC — if Opus stutters"),
                Lang.T("FLAC — без потерь, нужна широкая полоса", "FLAC — lossless, needs wide bandwidth"),
                Lang.T("Raw — несжатый, только для локали", "Raw — uncompressed, local only") });

            y += 8;
            Head(Lang.T("Окно на компьютере", "Window on the PC"), ref y);
            full = Check(Lang.T("Открывать во весь экран", "Open fullscreen"), ref y);
            onTop = Check(Lang.T("Поверх остальных окон", "Always on top"), ref y);
            borderless = Check(Lang.T("Без рамки", "Borderless"), ref y);

            y += 8;
            Head(Lang.T("Управление", "Control"), ref y);
            control = Check(Lang.T("Управлять телефоном с компьютера (мышь и клавиатура)", "Control the phone from the PC (mouse and keyboard)"), ref y);
            keepAwake = Check(Lang.T("Не давать телефону гаснуть", "Keep the phone awake"), ref y);
            screenOff = Check(Lang.T("Погасить экран самого телефона", "Turn off the phone's own screen"), ref y);

            controlHint = new Label();
            controlHint.SetBounds(46, y, 580, 34);
            controlHint.ForeColor = Art.Dim;
            controlHint.Font = new Font("Segoe UI", 8.5f);
            controlHint.Text = Lang.T(
                "Два пункта выше работают только вместе с управлением — без него телефон их запрещает.",
                "The two options above work only together with control — without it the phone rejects them.");
            content.Controls.Add(controlHint);
            y += 40;

            y += 8;
            Head(Lang.T("Интернет для телефона  (не обязательно)", "Internet for the phone  (optional)"), ref y);
            share = Check(Lang.T("Раздавать телефону интернет с этого компьютера", "Share this PC's internet with the phone"), ref y);

            shareHint = new Label();
            shareHint.SetBounds(46, y, 590, 46);
            shareHint.ForeColor = Art.Dim;
            shareHint.Font = new Font("Segoe UI", 8.5f);
            shareHint.Text = Lang.T(
                "Телефон возьмёт интернет из кабеля, а не из Wi-Fi. На телефоне один раз появится окно про VPN — надо нажать «ОК».",
                "The phone will take internet from the cable instead of Wi-Fi. A VPN dialog appears once on the phone — tap «OK».");
            content.Controls.Add(shareHint);
            y += 52;

            Head(Lang.T("Подключение", "Connection"), ref y);
            closeOnUnplug = Check(Lang.T("Закрывать программу, когда телефон отключают", "Close the program when the phone is unplugged"), ref y);

            Label unplugHint = new Label();
            unplugHint.SetBounds(46, y, 590, 32);
            unplugHint.ForeColor = Art.Dim;
            unplugHint.Font = new Font("Segoe UI", 8.5f);
            unplugHint.Text = Lang.T(
                "Закроется через " + Settings.UnplugDelaySec + " секунд после того, как кабель вынут. Если воткнуть обратно — отмена.",
                "Closes " + Settings.UnplugDelaySec + " seconds after the cable is pulled. Plug it back in to cancel.");
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

            netButton = Btn(Lang.T("Перейти на подключение по сети", "Switch to network connection"), 26, y, 300, 36, false);
            netButton.Click += delegate { OnGoNetwork(); };
            content.Controls.Add(netButton);

            cableButton = Btn(Lang.T("Вернуться на провод", "Back to cable"), 338, y, 220, 36, false);
            cableButton.Click += delegate { OnGoCable(); };
            content.Controls.Add(cableButton);
            y += 52;

            Head(Lang.T("Ярлыки", "Shortcuts"), ref y);
            Button restoreLinks = Btn(Lang.T("Восстановить ярлыки на рабочем столе", "Restore desktop shortcuts"), 26, y, 360, 36, false);
            restoreLinks.Click += delegate
            {
                Housekeeping.Recreate();
                MessageBox.Show(Lang.T("Ярлыки созданы заново на рабочем столе.", "Desktop shortcuts have been recreated."),
                    Paths.SettingsName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            content.Controls.Add(restoreLinks);
            y += 52;

            Head(Lang.T("О программе и проекты", "About & projects"), ref y);

            AddLink(Lang.T("Этот проект на GitHub", "This project on GitHub"), Paths.UrlProject, ref y);
            AddLink("scrcpy — " + Lang.T("вывод экрана (Apache 2.0)", "screen mirroring (Apache 2.0)"),
                    Paths.UrlScrcpy, ref y);
            AddLink("gnirehtet — " + Lang.T("интернет по USB (Apache 2.0)", "reverse tethering (Apache 2.0)"),
                    Paths.UrlGnirehtet, ref y);

            Label credit = new Label();
            credit.SetBounds(26, y, 610, 40);
            credit.ForeColor = Art.Dim;
            credit.Font = new Font("Segoe UI", 8.5f);
            credit.Text = Lang.T(
                "Программа использует scrcpy и gnirehtet (© Genymobile / Romain Vimont), " +
                "распространяемые без изменений. Это не официальный продукт Genymobile.",
                "This program bundles scrcpy and gnirehtet (© Genymobile / Romain Vimont), " +
                "distributed unmodified. Not an official Genymobile product.");
            content.Controls.Add(credit);
            y += 48;

            int footer = ClientSize.Height - 58;

            saveButton = Btn(Lang.T("Сохранить", "Save"), 26, footer, 200, 44, true);
            saveButton.Click += delegate { Apply(); Close(); };
            Controls.Add(saveButton);

            resetButton = Btn(Lang.T("Сбросить к обычным", "Reset to defaults"), 240, footer, 220, 44, false);
            resetButton.Click += delegate { LoadFrom(new Settings()); };
            Controls.Add(resetButton);

            Button close = Btn(Lang.T("Отмена", "Cancel"), 480, footer, 160, 44, false);
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

        // кликабельная ссылка на веб-страницу
        void AddLink(string text, string url, ref int y)
        {
            LinkLabel l = new LinkLabel();
            l.SetBounds(40, y, 590, 22);
            l.Text = text;
            l.LinkColor = Art.Accent;
            l.Font = new Font("Segoe UI", 9.5f);
            l.LinkClicked += delegate
            {
                try { Process.Start(url); }
                catch (Exception ex) { Log.Write("open url failed: " + ex.Message); }
            };
            content.Controls.Add(l);
            y += 26;
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
            language.SelectedIndex = s.Language == "ru" ? 1 : s.Language == "en" ? 2 : 0;

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
                MessageBox.Show(Lang.T(
                    "У этого телефона нет аппаратного кодировщика для выбранного кодека.\n\n" +
                    "Оставлю H.264 — он есть всегда и даёт самый быстрый отклик.",
                    "This phone has no hardware encoder for the selected codec.\n\n" +
                    "Keeping H.264 — it is always present and gives the lowest latency."),
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
            s.Language = language.SelectedIndex == 1 ? "ru" : language.SelectedIndex == 2 ? "en" : "";
            Lang.Ru = s.Language == "ru" ? true : s.Language == "en" ? false : Lang.SystemIsRussian();

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
                Lang.T(
                "ПРЕДУПРЕЖДЕНИЕ 1 из 2\n\n" +
                "Это не нужно для показа экрана. Картинка работает и без этого.\n\n" +
                "На телефон поставится небольшая программа от авторов scrcpy. " +
                "При включении телефон покажет окно «Запрос на подключение VPN» — " +
                "надо нажать «ОК». Это не вирус: так Android разрешает пускать весь " +
                "трафик телефона через кабель на компьютер.\n\n" +
                "Пока раздача включена, интернет телефону даёт компьютер, а не Wi-Fi. " +
                "Если у компьютера интернет хуже, чем Wi-Fi у телефона, станет только хуже.\n\n" +
                "Продолжить?",
                "WARNING 1 of 2\n\n" +
                "This is not needed for screen mirroring. The picture works without it.\n\n" +
                "A small app by the scrcpy authors will be installed on the phone. " +
                "When enabled, the phone shows a «VPN connection request» — tap «OK». " +
                "It is not a virus: this is how Android allows routing all phone traffic " +
                "through the cable to the PC.\n\n" +
                "While sharing is on, the phone gets internet from the PC, not Wi-Fi. " +
                "If the PC's internet is worse than the phone's Wi-Fi, it only gets worse.\n\n" +
                "Continue?"),
                Paths.SettingsName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (r1 != DialogResult.Yes) { share.Checked = false; return; }

            DialogResult r2 = MessageBox.Show(
                Lang.T(
                "ПРЕДУПРЕЖДЕНИЕ 2 из 2\n\n" +
                "Если во время игры выдернуть кабель, оборвётся не только картинка, " +
                "но и интернет на телефоне. Сетевая игра в этот момент отвалится, " +
                "и матч будет потерян.\n\n" +
                "Программа сама вернёт телефон на его Wi-Fi через пару секунд, но " +
                "соединение с игрой уже разорвётся.\n\n" +
                "Если матчи важны — оставь это выключенным.\n\n" +
                "Точно включить?",
                "WARNING 2 of 2\n\n" +
                "If you pull the cable during a game, not only the picture drops but also " +
                "the phone's internet. An online match will disconnect at that moment and be lost.\n\n" +
                "The program returns the phone to its Wi-Fi within a couple of seconds, but " +
                "the game connection is already broken.\n\n" +
                "If matches matter — leave this off.\n\n" +
                "Enable anyway?"),
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
                shareHint.Text = Lang.T(
                    "Недоступно, пока подключение идёт по сети: раздавать интернет " +
                    "по тому же каналу, по которому идёт картинка, нельзя.",
                    "Unavailable while connected over the network: you can't share internet " +
                    "over the same channel the picture goes through.");
            }
            else
            {
                shareHint.Text = Lang.T(
                    "Телефон возьмёт интернет из кабеля, а не из Wi-Fi. " +
                    "На телефоне один раз появится окно про VPN — надо нажать «ОК».",
                    "The phone will take internet from the cable instead of Wi-Fi. " +
                    "A VPN dialog appears once on the phone — tap «OK».");
            }

            netState.Text = net
                ? Lang.T("Сейчас: по сети  (", "Now: over network  (") + engine.Cfg.S.NetworkAddr + ")"
                : Lang.T("Сейчас: по проводу", "Now: over cable");
            netState.ForeColor = net ? Art.Warn : Art.Good;

            netHint.Text = net
                ? Lang.T(
                  "Пока включена сеть, размер ограничен " + Settings.NetMaxSize +
                  " точками, а битрейт — " + Settings.NetMaxBitRate + " Мбит. " +
                  "Вернись на провод, чтобы снять ограничение.",
                  "While the network is on, size is capped at " + Settings.NetMaxSize +
                  " px and bit rate at " + Settings.NetMaxBitRate + " Mbps. " +
                  "Go back to cable to remove the limit.")
                : Lang.T(
                  "Обычный режим. Провод даёт лучшую картинку и самый быстрый отклик.",
                  "Normal mode. Cable gives the best picture and the lowest latency.");

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
                Lang.T(
                "ПРЕДУПРЕЖДЕНИЕ 1 из 2\n\n" +
                "Это режим для опытных. По сети картинка идёт заметно хуже, чем по проводу: " +
                "выше задержка, возможны рывки и рассыпание изображения. Для игр, где важна " +
                "реакция, это обычно не годится.\n\n" +
                "Телефон и компьютер должны быть в одной сети.\n\n" +
                "Продолжить?",
                "WARNING 1 of 2\n\n" +
                "This is an advanced mode. Over the network the picture is noticeably worse than " +
                "over cable: higher latency, possible stutter and image breakup. For games where " +
                "reaction matters, it usually won't do.\n\n" +
                "The phone and the PC must be on the same network.\n\n" +
                "Continue?"),
                Paths.SettingsName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (r1 != DialogResult.Yes) return;

            DialogResult r2 = MessageBox.Show(
                Lang.T(
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
                "WARNING 2 of 2\n\n" +
                "While the network is on:\n" +
                "  • picture size will be capped at " + Settings.NetMaxSize + " px;\n" +
                "  • bit rate will be capped at " + Settings.NetMaxBitRate + " Mbps;\n" +
                "  • these two cannot be changed.\n\n" +
                "The mode drops after a phone reboot. To restore everything you'll need " +
                "the cable again.\n\n" +
                "Usually this mode is not needed: the phone can stay on Wi-Fi internet " +
                "while connected by cable.\n\n" +
                "Enable anyway?"),
                Paths.SettingsName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (r2 != DialogResult.Yes) return;

            netButton.Enabled = false;
            netState.Text = Lang.T("Переключаю…", "Switching…");
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
                            MessageBox.Show(Lang.T("Не получилось.\n\n", "It didn't work.\n\n") + error,
                                Paths.SettingsName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            SyncNetwork();
                            return;
                        }

                        engine.Cfg.S.NetworkMode = true;
                        engine.Cfg.S.NetworkAddr = address;
                        engine.Cfg.SaveSettingsOnly();

                        MessageBox.Show(
                            Lang.T(
                            "Готово. Телефон отвечает по адресу " + address + ".\n\n" +
                            "Кабель можно отключить.\n\n" +
                            "Важно: после перезагрузки телефона этот режим слетает — чтобы " +
                            "включить его снова, понадобится кабель.",
                            "Done. The phone responds at " + address + ".\n\n" +
                            "You can unplug the cable now.\n\n" +
                            "Note: after a phone reboot this mode drops — to turn it on again " +
                            "you'll need the cable."),
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

            yesButton = MakeButton(Lang.T("Да, использовать этот", "Yes, use this one"), 268, 400, 300, 44, true);
            yesButton.Visible = false;
            yesButton.Click += delegate { OnApprove(); };
            Controls.Add(yesButton);

            noButton = MakeButton(Lang.T("Нет", "No"), 582, 400, 180, 44, false);
            noButton.Visible = false;
            noButton.Click += delegate { OnReject(); };
            Controls.Add(noButton);

            playButton = MakeButton(Lang.T("Показать экран", "Show screen"), 268, 470, 300, 52, true);
            playButton.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            playButton.Enabled = false;
            playButton.Click += delegate { StartScrcpy(); };
            Controls.Add(playButton);

            settingsButton = MakeButton(Lang.T("Настройки", "Settings"), 582, 470, 180, 52, false);
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
                    Lang.T(
                    "Сейчас телефон берёт интернет с компьютера.\n\n" +
                    "Если закрыть это окно, раздача выключится, и телефон вернётся " +
                    "на свой Wi-Fi или мобильный интернет.\n\n" +
                    "Закрыть?",
                    "The phone is currently getting internet from the PC.\n\n" +
                    "If you close this window, sharing turns off and the phone returns " +
                    "to its own Wi-Fi or mobile data.\n\n" +
                    "Close?"),
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
                    Set(Lang.T("Запускаюсь…", "Starting…"), Lang.T("Секунду.", "One moment."));
                    break;

                case Stage.Extracting:
                    Set(Lang.T("Готовлю программу…", "Preparing the program…"),
                        Lang.T("Это делается один раз, при первом запуске. Несколько секунд.",
                               "This is done once, on first launch. A few seconds."));
                    break;

                case Stage.NoPhone:
                    Set(Lang.T("Подключи телефон к компьютеру", "Connect the phone to the PC"),
                        Lang.T(
                        "Возьми USB-кабель и воткни телефон в компьютер.\n\n" +
                        "Кабель должен уметь передавать данные, а не только заряжать. Если телефон " +
                        "заряжается, но здесь ничего не меняется — попробуй другой кабель.\n\n" +
                        "Через переходник или хаб тоже работает: телефон нужно втыкать в порт " +
                        "для данных, обычно он помечен значком SS. Порт только для зарядки " +
                        "не подойдёт.\n\n" +
                        "Окно закрывать не надо, оно само всё заметит.",
                        "Take a USB cable and plug the phone into the PC.\n\n" +
                        "The cable must carry data, not just charge. If the phone charges " +
                        "but nothing changes here — try another cable.\n\n" +
                        "Through an adapter or hub works too: plug the phone into a data port, " +
                        "usually marked SS. A charge-only port won't do.\n\n" +
                        "No need to close this window, it notices everything by itself."));
                    break;

                case Stage.AppleDevice:
                    Set(Lang.T("Это ", "This is ") + (s.Detail.Length > 0 ? s.Detail : Lang.T("устройство Apple", "an Apple device")) +
                            Lang.T(" — с ним так нельзя", " — this won't work with it"),
                        (s.UsbName.Length > 0 ? Lang.T("Подключено: ", "Connected: ") + s.UsbName + "\n\n" : "") +
                        Lang.T(
                        "Программа выводит на компьютер экран телефонов на Android. iPhone и iPad " +
                        "так не умеют: Apple закрыла эту возможность в самой iOS, обойти это " +
                        "не может ни одна программа.\n\n" +
                        "Что можно вместо этого:\n" +
                        "      • на Mac — кабель и приложение QuickTime Player;\n" +
                        "      • Apple TV и AirPlay;\n" +
                        "      • на Windows — программы, которые ловят AirPlay по Wi-Fi.\n\n" +
                        "Ничего не сломано. Отключи это устройство и подключи Android-телефон — " +
                        "окно переключится само.",
                        "The program mirrors the screen of Android phones. iPhone and iPad " +
                        "can't do this: Apple closed this off inside iOS itself, and no program " +
                        "can get around it.\n\n" +
                        "What you can do instead:\n" +
                        "      • on a Mac — a cable and the QuickTime Player app;\n" +
                        "      • Apple TV and AirPlay;\n" +
                        "      • on Windows — apps that catch AirPlay over Wi-Fi.\n\n" +
                        "Nothing is broken. Unplug this device and connect an Android phone — " +
                        "the window switches by itself."));
                    break;

                case Stage.PhoneNoDebug:
                    Set(Lang.T("Устройство вижу. Осталась настройка на нём", "I see the device. One setting left on it"),
                        (s.UsbName.Length > 0 ? Lang.T("Подключено: ", "Connected: ") + s.UsbName + "\n\n" : "") +
                        Lang.T("Если это Android-телефон, открой на нём:\n\n", "If this is an Android phone, open on it:\n\n") +
                        Brands.DevModePath(s.Brand) + "\n\n" +
                        Lang.T("Там включить «Отладка по USB».\n\n", "There, turn on «USB debugging».\n\n") +
                        Lang.T(
                        "А если это не телефон — фотоаппарат, плеер, флешка — отключи его " +
                        "и подключи телефон.",
                        "And if it's not a phone — a camera, a player, a flash drive — unplug it " +
                        "and connect a phone."));
                    break;

                case Stage.Unauthorized:
                    Set(Lang.T("Посмотри на экран телефона", "Look at the phone's screen"),
                        Lang.T(
                        "На нём появилось окно «Разрешить отладку по USB?».\n\n" +
                        "Нажми «Разрешить».\n\n" +
                        "Если есть галочка «Всегда разрешать с этого компьютера» — поставь её, " +
                        "тогда телефон больше не будет спрашивать.\n\n" +
                        "Окна нет? Разблокируй телефон, вытащи кабель и воткни снова.",
                        "A dialog «Allow USB debugging?» has appeared on it.\n\n" +
                        "Tap «Allow».\n\n" +
                        "If there's an «Always allow from this computer» checkbox — tick it, " +
                        "then the phone won't ask again.\n\n" +
                        "No dialog? Unlock the phone, pull the cable and plug it back in."));
                    break;

                case Stage.Authorizing:
                    Set(Lang.T("Секунду…", "One moment…"), Lang.T("Телефон и компьютер договариваются.", "The phone and the PC are handshaking."));
                    break;

                case Stage.Offline:
                    Set(Lang.T("Телефон не отвечает", "The phone doesn't respond"),
                        Lang.T(
                        "Разблокируй телефон, вытащи кабель и воткни снова.\n\n" +
                        "Если телефон подключён через хаб — попробуй другой порт на хабе " +
                        "или воткни телефон напрямую в компьютер.",
                        "Unlock the phone, pull the cable and plug it back in.\n\n" +
                        "If the phone is connected through a hub — try another port on the hub " +
                        "or plug the phone straight into the PC."));
                    break;

                case Stage.Multiple:
                    Set(Lang.T("Подключено несколько устройств", "Several devices connected"),
                        Lang.T("Компьютер видит сразу несколько:\n\n", "The PC sees several at once:\n\n") + s.Detail + "\n\n" +
                        Lang.T("Отключи лишние и оставь только нужный телефон.", "Unplug the extras and leave only the phone you need."));
                    break;

                case Stage.AdbConflict:
                    Set(Lang.T("Мешает другая программа", "Another program is interfering"),
                        Lang.T(
                        "На компьютере уже работает другая версия ADB — обычно её запускают " +
                        "Android Studio или программы для прошивки телефонов.\n\n" +
                        "Закрой их, вытащи и воткни кабель. Если не помогает — перезагрузи компьютер.",
                        "Another version of ADB is already running — usually started by " +
                        "Android Studio or phone-flashing tools.\n\n" +
                        "Close them, replug the cable. If that doesn't help — restart the PC."));
                    break;

                case Stage.DifferentPhone:
                    Set(Lang.T("Это другой телефон", "This is a different phone"),
                        Lang.T("Раньше здесь был настроен:\n", "Previously set up here:\n") +
                        "      " + Describe(s.SavedModel, s.SavedSerial) + "\n\n" +
                        Lang.T("А сейчас подключён:\n", "Now connected:\n") +
                        "      " + Describe(s.Model, s.Serial) + "\n\n" +
                        Lang.T(
                        "Переключиться на новый? Программа заново подберёт под него настройки.\n\n" +
                        "Если это не то устройство — нажми «Нет».",
                        "Switch to the new one? The program will pick settings for it again.\n\n" +
                        "If this is the wrong device — press «No»."));
                    break;

                case Stage.WrongPhone:
                    Set(Lang.T("Подключён не тот телефон", "The wrong phone is connected"),
                        Lang.T("Сейчас подключён:\n", "Now connected:\n") +
                        "      " + Describe(s.Model, s.Serial) + "\n\n" +
                        (s.SavedModel.Length > 0
                            ? Lang.T("А настроен был:\n      ", "But set up was:\n      ") + Describe(s.SavedModel, s.SavedSerial) + "\n\n"
                            : "") +
                        Lang.T(
                        "Отключи это устройство и подключи нужный телефон.\n\n" +
                        "Или нажми кнопку ниже, чтобы всё-таки работать с этим.",
                        "Unplug this device and connect the right phone.\n\n" +
                        "Or press the button below to use this one anyway."));
                    break;

                case Stage.NetworkSearching:
                    Set(Lang.T("Ищу телефон в сети…", "Looking for the phone on the network…"),
                        Lang.T("Адрес: ", "Address: ") + s.Detail + "\n\n" +
                        Lang.T("Телефон должен быть включён и находиться в той же сети, что и компьютер.",
                               "The phone must be on and on the same network as the PC."));
                    break;

                case Stage.NetworkLost:
                    Set(Lang.T("Телефон в сети не отвечает", "The phone doesn't respond on the network"),
                        Lang.T("Адрес: ", "Address: ") + s.Detail + "\n\n" +
                        Lang.T(
                        "Проверь, что телефон включён и подключён к той же сети.\n\n" +
                        "Если не помогает — подключи телефон кабелем и в настройках нажми " +
                        "«Вернуться на провод».",
                        "Make sure the phone is on and connected to the same network.\n\n" +
                        "If that doesn't help — connect the phone by cable and in settings press " +
                        "«Back to cable»."));
                    break;

                case Stage.Probing:
                    Set(Lang.T("Проверяю телефон…", "Checking the phone…"),
                        (s.Model.Length > 0 ? s.Model + "\n\n" : "") +
                        Lang.T(
                        "Смотрю, какие кодировщики он умеет, и выбираю подходящий. " +
                        "Несколько секунд, и только при первом подключении этого телефона.",
                        "Checking which encoders it has and picking a suitable one. " +
                        "A few seconds, and only the first time this phone is connected."));
                    break;

                case Stage.NoEncoder:
                    Set(Lang.T("Этот телефон не подойдёт", "This phone won't work"),
                        (s.Detail.Length > 0 ? s.Detail + "\n\n" : "") +
                        Lang.T(
                        "У него нет аппаратного кодировщика видео. Программный грузит процессор " +
                        "телефона, тот греется и начинает тормозить — поэтому запускать не буду.",
                        "It has no hardware video encoder. A software one loads the phone's CPU, " +
                        "it heats up and starts to lag — so I won't launch."));
                    break;

                case Stage.Ready:
                    Set(Lang.T("Готово", "Ready"),
                        (s.Model.Length > 0 ? s.Model + "\n" : "") +
                        (s.Android.Length > 0 ? "Android " + s.Android + "\n" : "") +
                        TransportLine() + "\n" +
                        Lang.T("Кодировщик подобран сам: ", "Encoder picked automatically: ") + engine.Cfg.EncoderH264 + "\n\n" +
                        Lang.T(
                        "Нажми «Показать экран», а потом запусти на телефоне то, что нужно.\n\n" +
                        "Картинка появится в отдельном окне на компьютере. Если телефон греется — " +
                        "уменьши размер и битрейт в настройках.",
                        "Press «Show screen», then start whatever you need on the phone.\n\n" +
                        "The picture appears in a separate window on the PC. If the phone heats up — " +
                        "lower the size and bit rate in settings."));
                    ShowSummary();
                    break;

                case Stage.Running:
                    Set(Lang.T("Идёт трансляция", "Streaming"), RunningText());
                    break;

                case Stage.Closed:
                    Set(Lang.T("Трансляция закрыта", "Stream closed"),
                        Lang.T("Нажми «Показать экран», чтобы включить снова.", "Press «Show screen» to start again."));
                    break;

                case Stage.Failed:
                    Set(Lang.T("Не получилось", "It didn't work"), s.Detail);
                    break;
            }

            yesButton.Visible = confirm || wrong;
            noButton.Visible = confirm;
            yesButton.Text = wrong ? Lang.T("Всё равно использовать этот", "Use this one anyway") : Lang.T("Да, использовать этот", "Yes, use this one");
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
                Set(Lang.T("Телефон отключён", "The phone is disconnected"),
                    Lang.T(
                    "Программа закроется через " + left + " сек.\n\n" +
                    "Если это случайно — просто воткни кабель обратно, и закрытие отменится.",
                    "The program will close in " + left + " s.\n\n" +
                    "If this is accidental — just plug the cable back in, and the close is cancelled."));
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
                case Transport.UsbDirect: return Lang.T("Подключение: кабель напрямую", "Connection: cable, direct");
                case Transport.UsbHub: return Lang.T("Подключение: кабель через переходник (хаб)", "Connection: cable through an adapter (hub)");
                case Transport.Network: return Lang.T("Подключение: по сети", "Connection: over network");
                default: return "";
            }
        }

        void ShowSummary()
        {
            Settings s = engine.Cfg.S;
            string sz = s.EffectiveMaxSize == 0 ? Lang.T("без уменьшения", "no downscale") : s.EffectiveMaxSize + Lang.T(" точек", " px");
            string f = s.MaxFps == 0 ? Lang.T("без ограничения", "no limit") : s.MaxFps + Lang.T(" к/с", " fps");
            string a =
                s.Audio == "off" ? Lang.T("звук выключен", "audio off") :
                s.Audio == "pc" ? Lang.T("звук в компьютер", "audio to PC") :
                s.Audio == "both" ? Lang.T("звук в компьютер и телефон", "audio to PC and phone") : Lang.T("звук автоматически", "audio automatic");

            summary.Text = sz + "   •   " + f + "   •   " + s.EffectiveBitRate + Lang.T(" Мбит", " Mbps") + "   •   " +
                           s.Codec.ToUpperInvariant().Replace("H264","H.264").Replace("H265","H.265") +
                           "   •   " + a +
                           (s.PcControl ? Lang.T("   •   управление с ПК", "   •   PC control") : "") +
                           (Net.Active ? Lang.T("\nИнтернет телефону раздаётся с компьютера — пока это окно открыто",
                                                "\nInternet is shared to the phone from the PC — while this window is open") : "");
        }

        static string RunningText()
        {
            return Lang.T(
                "Окно с картинкой открыто. Запусти на телефоне то, что нужно показать.\n\n" +
                "Это окно можно закрыть — на трансляцию это не повлияет.",
                "The picture window is open. Start whatever you want to show on the phone.\n\n" +
                "You can close this window — it won't affect the stream.");
        }

        static string Describe(string model, string serial)
        {
            string m = (model == null || model.Length == 0) ? Lang.T("Android-устройство", "Android device") : model;
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
            Set(Lang.T("Запускаю…", "Launching…"), Lang.T("Секунду.", "One moment."));

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
                        Set(Lang.T("Не удалось запустить", "Couldn't launch"), error);
                        playButton.Enabled = true;
                        return;
                    }

                    scrcpy = p;
                    running = true;
                    stage = Stage.Running;
                    picture.Invalidate();
                    Set(Lang.T("Идёт трансляция", "Streaming"), RunningText());

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
            Set(Lang.T("Трансляция закрыта", "Stream closed"), Lang.T("Нажми «Показать экран», чтобы включить снова.", "Press «Show screen» to start again."));
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

        // принудительно пересоздать ярлыки (кнопка в настройках)
        public static void Recreate()
        {
            completed = false;
            lastTry = DateTime.MinValue;
            RunOnce();
        }

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
                        Make(desktop, Paths.AppName, target, "",
                             Lang.T("Экран телефона на компьютере", "Phone screen on the PC"));
                        Make(desktop, Paths.SettingsName, target, "--settings",
                             Lang.T("Настройки трансляции экрана", "Screen streaming settings"));
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

                // применяем сохранённый выбор языка до открытия любого окна
                Lang.Apply(engine.Cfg.S.Language);

                if (settingsOnly)
                {
                    if (!engine.EnsureInstalled())
                    {
                        MessageBox.Show(
                            Lang.T("Не удалось подготовить программу.\n\n", "Could not prepare the program.\n\n") +
                            Lang.T("Запусти сначала «", "Run «") + Paths.AppName + Lang.T("» — он всё установит, ", "» first — it will install everything, ") +
                            Lang.T("потом открой настройки.", "then open settings."),
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
                    Lang.T("Что-то пошло не так.\n\n", "Something went wrong.\n\n") + (ex == null ? "" : ex.Message),
                    Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
