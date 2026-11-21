using System;

namespace RelayRdp;

public enum ScreenMode { Window, FullScreen, MultiMonitor }
public enum DesktopSize { Default, Hd, FullHd, Qhd, UltraHd }
public enum DriveMode { None, Selected, All, ConnectedLater }
public enum AudioMode { Local, Remote, Muted }

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Gateway { get; set; } = "";
    public bool AdminSession { get; set; }

    public ScreenMode Screen { get; set; }
    public DesktopSize Size { get; set; }
    public bool ReducedEffects { get; set; }

    public bool Clipboard { get; set; } = true;
    public DriveMode Drives { get; set; }
    public string DriveLetters { get; set; } = "";
    public bool Usb { get; set; }
    public bool PortableDevices { get; set; }
    public bool Cameras { get; set; }
    public bool Microphone { get; set; }
    public bool Printers { get; set; }
    public bool SmartCards { get; set; }
    public bool SerialPorts { get; set; }
    public bool SecurityKeys { get; set; }
    public AudioMode Audio { get; set; }

    public ConnectionProfile Copy() => (ConnectionProfile)MemberwiseClone();
}
