using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RelayRdp;

internal static class RdpFile
{
    public static ConnectionProfile Normalize(ConnectionProfile draft)
    {
        var profile = draft.Copy();
        profile.Address = NormalizeAddress(profile.Address, "адрес компьютера");
        profile.Name = SingleLine(profile.Name, "Назовите профиль одной строкой.");
        if (profile.Name.Length == 0) profile.Name = profile.Address;
        profile.UserName = SingleLine(profile.UserName, "Укажите имя пользователя одной строкой.");
        profile.Gateway = string.IsNullOrWhiteSpace(profile.Gateway)
            ? "" : NormalizeAddress(profile.Gateway, "адрес шлюза");

        if (!Enum.IsDefined(typeof(ScreenMode), profile.Screen) || !Enum.IsDefined(typeof(DesktopSize), profile.Size)
            || !Enum.IsDefined(typeof(DriveMode), profile.Drives) || !Enum.IsDefined(typeof(AudioMode), profile.Audio))
        {
            throw new ArgumentException("Повторно выберите режим экрана, разрешение, режим дисков и звук.");
        }

        if (profile.Drives == DriveMode.Selected)
        {
            var letters = Regex.Split(profile.DriveLetters?.Trim() ?? "", @"[\s,;]+");
            if (letters.Any(letter => !Regex.IsMatch(letter, @"\A[A-Za-z](?::\\?)?\z")))
                throw new ArgumentException("Укажите буквы дисков через запятую, например D, E.");
            profile.DriveLetters = string.Join(", ", letters.Select(letter => char.ToUpperInvariant(letter[0])).Distinct());
        }

        return profile;
    }

    public static string Build(ConnectionProfile draft)
    {
        var profile = Normalize(draft);
        var lines = new StringBuilder();
        void Setting(string name, string value) => lines.Append(name).Append(":s:").Append(value).Append("\r\n");
        void Number(string name, int value) => lines.Append(name).Append(":i:")
            .Append(value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        void Toggle(string name, bool enabled) => Number(name, enabled ? 1 : 0);

        Setting("full address", profile.Address);
        Setting("username", profile.UserName);
        Setting("gatewayhostname", profile.Gateway);
        Number("gatewayusagemethod", profile.Gateway.Length == 0 ? 0 : 1);
        Number("gatewayprofileusagemethod", 1);
        Toggle("administrative session", profile.AdminSession);
        Number("authentication level", 2);
        Toggle("enablecredsspsupport", true);
        Toggle("autoreconnection enabled", true);

        Number("screen mode id", profile.Screen == ScreenMode.Window ? 1 : 2);
        Toggle("use multimon", profile.Screen == ScreenMode.MultiMonitor);
        Toggle("displayconnectionbar", true);
        Number("keyboardhook", 2);
        Number("session bpp", 32);

        var size = profile.Screen == ScreenMode.Window ? profile.Size : DesktopSize.Default;
        Toggle("smart sizing", profile.Screen == ScreenMode.Window);
        var (width, height) = size switch
        {
            DesktopSize.Hd => (1280, 720),
            DesktopSize.FullHd => (1920, 1080),
            DesktopSize.Qhd => (2560, 1440),
            DesktopSize.UltraHd => (3840, 2160),
            _ => (0, 0)
        };
        if (width > 0)
        {
            Number("desktopwidth", width);
            Number("desktopheight", height);
        }

        Toggle("networkautodetect", true);
        Toggle("bandwidthautodetect", true);
        Toggle("bitmapcachepersistenable", true);
        Toggle("disable wallpaper", profile.ReducedEffects);
        Toggle("disable full window drag", profile.ReducedEffects);
        Toggle("disable menu anims", profile.ReducedEffects);
        Toggle("allow font smoothing", !profile.ReducedEffects);

        Toggle("redirectclipboard", profile.Clipboard);
        Setting("drivestoredirect", profile.Drives switch
        {
            DriveMode.All => "*",
            DriveMode.ConnectedLater => "DynamicDrives",
            DriveMode.Selected => string.Concat(profile.DriveLetters.Split(',')
                .Select(letter => $@"{letter.Trim()}:\;")),
            _ => ""
        });
        Setting("usbdevicestoredirect", profile.Usb ? "*" : "");
        Setting("devicestoredirect", profile.PortableDevices ? "*" : "");
        Setting("camerastoredirect", profile.Cameras ? "*" : "");
        Toggle("redirectprinters", profile.Printers);
        Toggle("redirectsmartcards", profile.SmartCards);
        Toggle("redirectcomports", profile.SerialPorts);
        Toggle("redirectwebauthn", profile.SecurityKeys);
        Toggle("audiocapturemode", profile.Microphone);
        Number("audiomode", (int)profile.Audio);
        return lines.ToString();
    }

    public static void Write(string path, string settings) => File.WriteAllText(path, settings, Encoding.Unicode);

    public static void Launch(string path, bool edit = false)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Встроенный клиент удалённого рабочего стола Windows не найден.", executable);
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл подключения не найден.", path);

        using var process = Process.Start(new ProcessStartInfo(executable, $"{(edit ? "/edit " : "")}\"{path}\"")
        {
            UseShellExecute = false
        });
        if (process is null) throw new Win32Exception("Windows не запустила клиент RDP.");
    }

    public static void StartSession(string sessionsDirectory, string settings)
    {
        Directory.CreateDirectory(sessionsDirectory);
        var path = Path.Combine(sessionsDirectory, $"relay-{Guid.NewGuid():N}.rdp");
        Write(path, settings);
        Launch(path);
    }

    public static void CleanSessions(string sessionsDirectory)
    {
        if (!Directory.Exists(sessionsDirectory)) return;

        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "relay-*.rdp"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
    }

    private static string SingleLine(string? raw, string error)
    {
        if (raw?.Any(char.IsControl) == true) throw new ArgumentException(error);
        return raw?.Trim() ?? "";
    }

    private static string NormalizeAddress(string? raw, string field)
    {
        var error = $"Проверьте {field}. Примеры: pc.example.net, 192.168.1.20:3389, [2001:db8::1]:3389.";
        var address = SingleLine(raw, error);
        var host = address;
        var suffix = "";
        var bracketed = address.StartsWith("[", StringComparison.Ordinal);
        if (bracketed)
        {
            var end = address.IndexOf(']');
            if (end < 0) throw new ArgumentException(error);
            host = address.Substring(1, end - 1);
            suffix = address.Substring(end + 1);
        }
        else if (address.Count(character => character == ':') == 1)
        {
            var colon = address.IndexOf(':');
            host = address.Substring(0, colon);
            suffix = address.Substring(colon);
        }

        var ipv6 = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            && !host.Any(char.IsWhiteSpace);
        if (ipv6)
        {
            host = $"[{ip}]";
        }
        else if (bracketed || host.Any(character => "\\/@?#[]%".Contains(character))
            || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            throw new ArgumentException(error);
        }

        if (suffix.Length == 0) return host;
        if (!suffix.StartsWith(":", StringComparison.Ordinal)
            || !ushort.TryParse(suffix.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port == 0)
        {
            throw new ArgumentException(error);
        }

        return $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }
}
