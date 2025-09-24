// makcu.cs
// Requires NuGet: System.Management
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MamboDMA.Input
{
    public enum MakcuMouseButton : int
    {
        Left = 1,
        Right = 2,
        Middle = 3,
        mouse4 = 4,
        mouse5 = 5
    }

    /// <summary>
    /// Makcu serial mouse device (Connect by name or COM, stream buttons, move/click, etc.)
    /// </summary>
    public class Device
    {
        // log every change by default (you can toggle this at runtime)
        public static bool LogButtonEvents = true;

        // last byte we saw from the device; 0xFF means “unknown” so first packet forces a startup log
        private static byte _prevButtons = 0xFF;

        // optional: let other code subscribe to button edges
        public static event Action<MakcuMouseButton, bool>? OnButtonChanged;        
        #region Makcu Identity
        // Tune these to your adapter (used by VID/PID search and validation).
        private const string MAKCU_FRIENDLY_NAME = "USB-Enhanced-SERIAL CH343";
        private const string MAKCU_VID             = "1A86";
        private const string MAKCU_PID             = "55D3";
        // Optional: set a unique serial fragment if you have multiple similar adapters.
        private const string MAKCU_SERIAL_FRAGMENT = "58A6074578";
        private const string MAKCU_EXPECT_SIGNATURE = "km.MAKCU";
        #endregion

        #region Fields / State
        private static readonly byte[] change_cmd = { 0xDE, 0xAD, 0x05, 0x00, 0xA5, 0x00, 0x09, 0x3D, 0x00 };

        public static bool connected = false;
        private static SerialPort port = null;
        private static Thread button_inputs;
        public static string version = "";
        private static bool runReader = false;

        public static Dictionary<int, bool> bState { get; private set; }

        private static readonly HashSet<byte> validBytes = new HashSet<byte>
        {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15,
            0x16, 0x17, 0x19, 0x1F
        };

        private static readonly Random r = new Random();
        private const int DEFAULT_OPEN_BAUD = 115200;   // initial open
        private const int HIGH_BAUD = 4000000;          // Makcu high speed
        #endregion

        // =====================================================================
        // Public convenience API
        // =====================================================================

        /// <summary>
        /// Connect to a Makcu device by partial or exact friendly name (or "COM7").
        /// Validates the "km.MAKCU" signature before returning true.
        /// </summary>
        public static bool MakcuConnect(string deviceNameOrCom)
        {
            try
            {
                string com = ResolveComFromNameOrPort(deviceNameOrCom);
                if (string.IsNullOrEmpty(com))
                {
                    Console.WriteLine($"[-] No COM port found for '{deviceNameOrCom}'.");
                    return false;
                }

                Connect(com);
                if (!connected)
                {
                    Console.WriteLine("[-] Failed to open Makcu serial port.");
                    return false;
                }

                if (!ValidateMakcuSignature())
                {
                    Console.WriteLine("[-] Device did not return expected signature (km.MAKCU).");
                    Disconnect();
                    return false;
                }

                Console.WriteLine($"[+] Makcu connected on {com}. {version}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] MakcuConnect error: {ex}");
                return false;
            }
        }

        /// <summary>
        /// One-shot: try VID/PID or friendly name and Connect, then verify signature.
        /// </summary>
        public static bool AutoConnectMakcu()
        {
            try
            {
                string com =
                    TryGetComByVidPid(MAKCU_VID, MAKCU_PID, MAKCU_SERIAL_FRAGMENT)
                    ?? TryGetComByFriendlyName(MAKCU_FRIENDLY_NAME, MAKCU_SERIAL_FRAGMENT);

                if (string.IsNullOrEmpty(com))
                {
                    Console.WriteLine("[-] Makcu device not found via VID/PID or friendly name.");
                    return false;
                }

                Connect(com);
                if (!connected)
                {
                    Console.WriteLine("[-] Failed to open Makcu serial port.");
                    return false;
                }

                if (!ValidateMakcuSignature())
                {
                    Console.WriteLine("[-] Device did not return expected signature (km.MAKCU).");
                    Disconnect();
                    return false;
                }

                Console.WriteLine("[+] Makcu connected and verified.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] AutoConnectMakcu error: {ex}");
                return false;
            }
        }

        // =====================================================================
        // Discovery / Validation
        // =====================================================================

        private static bool ValidateMakcuSignature(int timeoutMs = 800)
        {
            try
            {
                if (port == null || !port.IsOpen) return false;

                port.DiscardInBuffer();
                port.Write("km.version()\r");

                int oldTimeout = port.ReadTimeout;
                port.ReadTimeout = timeoutMs;

                string line = port.ReadLine()?.Trim();
                port.ReadTimeout = oldTimeout;

                if (string.IsNullOrEmpty(line))
                    return false;

                bool ok = line.StartsWith(MAKCU_EXPECT_SIGNATURE, StringComparison.OrdinalIgnoreCase)
                       || line.Contains(MAKCU_EXPECT_SIGNATURE, StringComparison.OrdinalIgnoreCase);

                if (ok) version = line;
                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveComFromNameOrPort(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            string q = query.Trim();

            // 1) Exact COM given? (COMx)
            if (q.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                return q.ToUpperInvariant();

            // 2) Try friendly-name contains (prefer VID/PID matches if multiple)
            var all = EnumerateSerialDevices();

            string vidPat = $"VID_{MAKCU_VID}";
            string pidPat = $"PID_{MAKCU_PID}";

            var nameMatches = all
                .Where(d =>
                    (!string.IsNullOrEmpty(d.Name) && d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(d.Port) && d.Port.Equals(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (nameMatches.Count == 1) return nameMatches[0].Port;

            var nameMatchesWithVidPid = nameMatches
                .Where(d => d.Pnp.IndexOf(vidPat, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            d.Pnp.IndexOf(pidPat, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (nameMatchesWithVidPid.Count > 0) return nameMatchesWithVidPid[0].Port;

            // 3) If still nothing: any VID/PID (optionally filtered by serial fragment)
            var vidPid = all.Where(d =>
                d.Pnp.IndexOf(vidPat, StringComparison.OrdinalIgnoreCase) >= 0 &&
                d.Pnp.IndexOf(pidPat, StringComparison.OrdinalIgnoreCase) >= 0 &&
                (string.IsNullOrEmpty(MAKCU_SERIAL_FRAGMENT) ||
                 d.Pnp.IndexOf(MAKCU_SERIAL_FRAGMENT, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            if (vidPid.Count > 0) return vidPid[0].Port;

            // 4) Last resort: WMI friendly search
            var wmi = TryGetComByFriendlyName(q);
            if (!string.IsNullOrEmpty(wmi)) return wmi;

            return null;
        }

        public sealed class SerialDeviceInfo
        {
            public string Port { get; init; } = "";
            public string Name { get; init; } = "";
            public string Pnp  { get; init; } = "";
            public override string ToString() => $"{Port,-6} {Name}  [{Pnp}]";
        }

        /// <summary>
        /// Enumerate all serial devices (COM ports) with friendly name and PNP ID.
        /// </summary>
        public static List<SerialDeviceInfo> EnumerateSerialDevices()
        {
            var results = new List<SerialDeviceInfo>();

            // Primary: Win32_SerialPort (already mapped to COMx)
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPDeviceID FROM Win32_SerialPort"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    results.Add(new SerialDeviceInfo
                    {
                        Port = (mo["DeviceID"] as string) ?? "",
                        Name = (mo["Name"] as string) ?? "",
                        Pnp  = (mo["PNPDeviceID"] as string) ?? ""
                    });
                }
            }

            // Fallback: Win32_PnPEntity — sometimes the (COMx) is only in Name here.
            using (var pnp = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
            {
                foreach (ManagementObject mo in pnp.Get())
                {
                    string name = (mo["Name"] as string) ?? "";
                    string pnpId = (mo["PNPDeviceID"] as string) ?? "";
                    string com = ExtractComFromFriendlyName(name) ?? "";

                    if (string.IsNullOrEmpty(com)) continue;
                    if (results.Any(r => string.Equals(r.Port, com, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    results.Add(new SerialDeviceInfo
                    {
                        Port = com,
                        Name = name,
                        Pnp  = pnpId
                    });
                }
            }

            results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Port, b.Port));
            return results;
        }

        public static string TryGetComByVidPid(string vidHex, string pidHex, string serialContains = null)
        {
            string vidPattern = $"VID_{vidHex.Trim().ToUpper()}";
            string pidPattern = $"PID_{pidHex.Trim().ToUpper()}";

            // Direct: Serial ports
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort"))
            {
                foreach (ManagementObject portObj in searcher.Get())
                {
                    string pnp = (portObj["PNPDeviceID"] as string) ?? "";
                    if (!pnp.Contains(vidPattern) || !pnp.Contains(pidPattern))
                        continue;

                    if (!string.IsNullOrEmpty(serialContains) &&
                        !pnp.Contains(serialContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return (portObj["DeviceID"] as string); // "COMx"
                }
            }

            // Fallback: PnP entities; extract (COMx) from Friendly Name
            using (var devs = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_%'"))
            {
                foreach (ManagementObject dev in devs.Get())
                {
                    string pnp = (dev["PNPDeviceID"] as string) ?? "";
                    if (!pnp.Contains(vidPattern) || !pnp.Contains(pidPattern))
                        continue;

                    if (!string.IsNullOrEmpty(serialContains) &&
                        !pnp.Contains(serialContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = (dev["Name"] as string) ?? "";
                    var com = ExtractComFromFriendlyName(name);
                    if (!string.IsNullOrEmpty(com))
                        return com;
                }
            }

            return null;
        }

        public static string TryGetComByFriendlyName(string friendlyContains, string serialContains = null)
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort"))
            {
                foreach (ManagementObject portObj in searcher.Get())
                {
                    string name = (portObj["Name"] as string) ?? "";
                    string pnp  = (portObj["PNPDeviceID"] as string) ?? "";

                    if (!name.Contains(friendlyContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrEmpty(serialContains) &&
                        !pnp.Contains(serialContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return (portObj["DeviceID"] as string); // "COMx"
                }
            }

            using (var devs = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name IS NOT NULL"))
            {
                foreach (ManagementObject dev in devs.Get())
                {
                    string name = (dev["Name"] as string) ?? "";
                    if (!name.Contains(friendlyContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string pnp = (dev["PNPDeviceID"] as string) ?? "";
                    if (!string.IsNullOrEmpty(serialContains) &&
                        !pnp.Contains(serialContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var com = ExtractComFromFriendlyName(name);
                    if (!string.IsNullOrEmpty(com))
                        return com;
                }
            }

            return null;
        }

        private static string ExtractComFromFriendlyName(string name)
        {
            // Examples:
            //  "USB-Enhanced-SERIAL CH343 (COM7)"
            //  "Prolific USB-to-Serial Comm Port (COM5)"
            int open = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (open >= 0)
            {
                int close = name.IndexOf(')', open);
                if (close > open)
                {
                    string inner = name.Substring(open + 1, close - open - 1); // "COM7"
                    if (inner.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        return inner.ToUpper();
                }
            }
            return null;
        }

        // =====================================================================
        // Connect / Disconnect / Reconnect
        // =====================================================================

        public static void Connect(string com)
        {
            try
            {
                if (port == null)
                {
                    port = new SerialPort(com, DEFAULT_OPEN_BAUD, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = 500,
                        WriteTimeout = 500,
                        Encoding = Encoding.ASCII,
                        NewLine = "\n"
                    };
                }
                else
                {
                    if (port.IsOpen) port.Close();
                    port.PortName = com;
                    port.BaudRate = DEFAULT_OPEN_BAUD;
                }

                port.Open();
                if (!port.IsOpen)
                    return;

                Thread.Sleep(150);
                port.Write(change_cmd, 0, change_cmd.Length);
                port.BaseStream.Flush();

                // Switch to high speed (ensure your driver supports it).
                port.BaudRate = HIGH_BAUD;

                GetVersion();
                Thread.Sleep(150);

                Console.WriteLine($"[+] Device connected to {port.PortName} at {port.BaudRate} baudrate");

                // enable button stream + disable echo
                port.Write("km.buttons(1)\r\n");
                port.Write("km.echo(0)\r\n");
                port.DiscardInBuffer();

                start_listening();

                bState = new Dictionary<int, bool>();
                for (int i = 1; i <= 5; i++)
                    bState[i] = false;

                connected = true;
            }
            catch (Exception ex)
            {
                connected = false;
                Console.WriteLine($"[-] Device failed to Connect. {ex}");
            }
        }

        public static void Disconnect()
        {
            if (!connected || port == null)
                return;

            try
            {
                Console.WriteLine("[!] Closing port...");
                runReader = false;

                if (port.IsOpen)
                {
                    port.Write("km.buttons(0)\r\n");
                    Thread.Sleep(10);
                    port.BaseStream.Flush();
                }

                port.Close();
                if (!port.IsOpen)
                    Console.WriteLine("[!] Port terminated successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Port close error: {ex}");
            }
            finally
            {
                connected = false;
            }
        }

        public static async void Reconnect_Device(string com)
        {
            Disconnect();
            await Task.Delay(200);
            try
            {
                if (port != null && !port.IsOpen)
                    port.Open();

                Console.WriteLine("[+] Reconnected to device.");
                connected = port?.IsOpen == true;
            }
            catch (Exception ex)
            {
                connected = false;
                Console.WriteLine($"[-] Reconnect failed: {ex}");
            }
        }

        // =====================================================================
        // Version / Commands
        // =====================================================================

        public static string GetVersion()
        {
            if (port == null || !port.IsOpen) return version = $"Port Null or Closed : {port?.PortName} {port?.IsOpen} {port?.BaudRate} ";

            try
            {
                port.DiscardInBuffer();
                port.Write("km.version()\r");
                Thread.Sleep(100);
                version = port.ReadLine();
                return version;
            }
            catch
            {
                return version = "";
            }
        }

        public static void move(int x, int y)
        {
            if (!connected) return;
            port.Write($"km.move({x}, {y})\r");
            _ = port.BaseStream.FlushAsync();
        }

        public static void move_smooth(int x, int y, int segments)
        {
            if (!connected) return;
            port.Write($"km.move({x}, {y}, {segments})\r");
            _ = port.BaseStream.FlushAsync();
        }

        public static void move_bezier(int x, int y, int segments, int ctrl_x, int ctrl_y)
        {
            if (!connected) return;
            port.Write($"km.move({x}, {y}, {segments}, {ctrl_x}, {ctrl_y})\r");
            _ = port.BaseStream.FlushAsync();
        }

        public static void mouse_wheel(int delta)
        {
            if (!connected) return;
            port.Write($"km.wheel({delta})\r");
            _ = port.BaseStream.FlushAsync();
        }

        public static void lock_axis(string axis, int bit)
        {
            if (!connected) return;
            port.Write($"km.lock_m{axis}({bit})\r");
            _ = port.BaseStream.FlushAsync();
        }

        public static void click(string button, int ms_delay, int click_delay = 0)
        {
            if (!connected) return;

            int time = r.Next(10, 100); // randomized press time
            Thread.Sleep(click_delay);
            port.Write($"km.{button}(1)\r");
            Thread.Sleep(time);
            port.Write($"km.{button}(0)\r");
            _ = port.BaseStream.FlushAsync();
            Thread.Sleep(ms_delay);
        }

        public static void press(MakcuMouseButton button, int press)
        {
            if (!connected) return;
            string cmd = $"km.{MouseButtonToString(button)}({press})\r";
            port.Write(cmd);
            _ = port.BaseStream.FlushAsync();
        }

        // =====================================================================
        // Button Stream (Listener)
        // =====================================================================

        public static void start_listening()
        {
            if (button_inputs != null && button_inputs.IsAlive)
                return;

            Thread.Sleep(500);
            runReader = true;

            // reset state trackers
            _prevButtons = 0xFF;
            bState = new Dictionary<int, bool>();
            for (int i = 1; i <= 5; i++) bState[i] = false;

            button_inputs = new Thread(read_buttons)
            {
                IsBackground = true,
                Name = "MakcuButtonListener"
            };
            button_inputs.Start();
        }

        public static async void read_buttons()
        {
            await Task.Run(() =>
            {
                Console.WriteLine("[+] Listening to device.");
                while (runReader)
                {
                    if (!connected || port == null)
                    {
                        Thread.Sleep(250);
                        connected = port?.IsOpen == true;
                        continue;
                    }

                    try
                    {
                        if (port.BytesToRead > 0)
                        {
                            int data = port.ReadByte();
                            if (!validBytes.Contains((byte)data))
                                continue;

                            byte b = (byte)data;

                            // update shared state
                            for (int i = 1; i <= 5; i++)
                                bState[i] = (b & (1 << (i - 1))) != 0;

                            // ----- NEW: edge logging -----
                            if (LogButtonEvents)
                            {
                                if (_prevButtons == 0xFF)
                                {
                                    // first packet: report any buttons already held
                                    for (int i = 0; i < 5; i++)
                                    {
                                        bool down = (b & (1 << i)) != 0;
                                        //if (down) LogBtn((MakcuMouseButton)(i + 1), true, startup: true);
                                    }
                                }
                                else
                                {
                                    // subsequent packets: log only changes
                                    byte diff = (byte)(b ^ _prevButtons);
                                    if (diff != 0)
                                    {
                                        for (int i = 0; i < 5; i++)
                                        {
                                            if ((diff & (1 << i)) == 0) continue;
                                            bool down = (b & (1 << i)) != 0;
                                            //LogBtn((MakcuMouseButton)(i + 1), down, startup: false);
                                        }
                                    }
                                }
                            }

                            _prevButtons = b;
                            // port.DiscardInBuffer(); // (optional) consider removing to avoid dropping bursts
                        }
                        else
                        {
                            Thread.Sleep(1);
                        }
                    }
                    catch
                    {
                        connected = false;
                        Thread.Sleep(50);
                    }
                }
            });
        }

        // =====================================================================
        // Button Helpers / Locks
        // =====================================================================
        private static void LogBtn(MakcuMouseButton btn, bool down, bool startup)
        {
            string name = MouseButtonToString(btn);
            if (startup)
                Console.WriteLine($"[Makcu] startup: {name} DOWN");
            else
                Console.WriteLine($"[Makcu] {name} {(down ? "DOWN" : "UP")}");
        
            // fire optional event for other consumers
            OnButtonChanged?.Invoke(btn, down);
        }
        public static bool button_pressed(MakcuMouseButton button)
        {
            if (!connected || bState == null) return false;
            return bState.TryGetValue((int)button, out bool state) && state;
        }

        public static async void lock_button(MakcuMouseButton button, int bit)
        {
            if (!connected) return;

            string cmd = button switch
            {
                MakcuMouseButton.Left   => $"km.lock_ml({bit})\r",
                MakcuMouseButton.Right  => $"km.lock_mr({bit})\r",
                MakcuMouseButton.Middle => $"km.lock_mm({bit})\r",
                MakcuMouseButton.mouse4 => $"km.lock_ms1({bit})\r",
                MakcuMouseButton.mouse5 => $"km.lock_ms2({bit})\r",
                _ => $"km.lock_ml({bit})\r"
            };

            await Task.Delay(1);
            port.Write(cmd);
            await port.BaseStream.FlushAsync();
        }

        public static int MouseButtonToInt(MakcuMouseButton button) => (int)button;
        public static MakcuMouseButton IntToMouseButton(int button) => (MakcuMouseButton)button;

        public static string MouseButtonToString(MakcuMouseButton button) =>
            button switch
            {
                MakcuMouseButton.Left   => "left",
                MakcuMouseButton.Right  => "right",
                MakcuMouseButton.Middle => "middle",
                MakcuMouseButton.mouse4 => "ms1",
                MakcuMouseButton.mouse5 => "ms2",
                _ => "left"
            };

        public static void setMouseSerial(string serial)
        {
            if (!connected) return;
            port.Write($"km.serial({serial})\r");
        }

        public static void resetMouseSerial()
        {
            if (!connected) return;
            port.Write("km.serial(0)\r");
        }

        public static void unlock_all_buttons()
        {
            if (port?.IsOpen == true)
            {
                port.Write("km.lock_ml(0)\r");
                port.Write("km.lock_mr(0)\r");
                port.Write("km.lock_mm(0)\r");
                port.Write("km.lock_ms1(0)\r");
                port.Write("km.lock_ms2(0)\r");
            }
        }
    }
}
