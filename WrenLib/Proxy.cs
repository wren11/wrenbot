using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WrenLib
{
    // These delegates are preserved to keep WrenBot handlers unchanged.
    public delegate void InitDelegate(ProxySocket Base, uint Serial);
    public delegate void SerialDelegate(ProxySocket Socket, uint Serial, NewProxy Proxy, uint PlayerSerial);
    public delegate void OnGameLogin(ProxySocket Socket, uint Serial, NewProxy Proxy, string CharacterName);
    public delegate void OnPacketEvent(ProxySocket Socket, uint Serial, NewProxy Proxy, ref MemoryStream Buffer);

    public class NewProxy
    {
        // DA-style fixed addresses from linked reference.
        internal const int SEND_THIS_ADDRESS = 0x0073D958;
        internal const int FUNC_SEND_ADDRESS = 0x00563E00;
        private const int PLAYER_NAME_ADDRESS = 0x0075E850;

        private readonly ManualResetEvent _disconnectReset;
        private readonly object _pollLock = new object();
        private readonly Dictionary<uint, IntPtr> _knownWindows = new Dictionary<uint, IntPtr>();
        private Thread _pollThread;
        private volatile bool _running;
        private uint _nextSocketId = 1;

        public NewProxy() : this(0) { }

        // Port is intentionally ignored now: no local proxy server is created.
        public NewProxy(int Port)
        {
            this.DisconnectReset = new ManualResetEvent(true);
            _disconnectReset = this.DisconnectReset;
            this.Clients = new Dictionary<uint, ProxySocket>();
            StartPolling();
        }

        public Dictionary<uint, ProxySocket> Clients;
        public ManualResetEvent DisconnectReset;
        public InitDelegate OnStartUp;
        public SerialDelegate OnConnect;
        public SerialDelegate OnGameServerConnect;
        public SerialDelegate OnDisconnect;
        public OnGameLogin OnGameEnter;
        public OnPacketEvent OnRecv;
        public OnPacketEvent OnSend;

        // Preserved for compatibility with old callsites.
        public static void TransformStream(ProxySocket Socket, ref MemoryStream PacketStream, ref BinaryReader Reader, ref BinaryWriter Writer)
        {
            if (Socket != null && Socket.Encryption != null)
            {
                byte[] Buffer = PacketStream.ToArray();
                Socket.Encryption.Transform(Buffer);
                long Position = PacketStream.Position;
                PacketStream.Dispose();
                PacketStream = new MemoryStream();
                PacketStream.Write(Buffer, 0, Buffer.Length);
                PacketStream.Seek(Position, SeekOrigin.Begin);
                Reader = new BinaryReader(PacketStream);
                Writer = new BinaryWriter(PacketStream);
            }
        }

        public static void TransformStream(ProxySocket Socket, ref MemoryStream PacketStream)
        {
            if (Socket != null && Socket.Encryption != null)
            {
                byte[] Buffer = PacketStream.ToArray();
                Socket.Encryption.Transform(Buffer);
                long Position = PacketStream.Position;
                PacketStream.Dispose();
                PacketStream = new MemoryStream();
                PacketStream.Write(Buffer, 0, Buffer.Length);
                PacketStream.Seek(Position, SeekOrigin.Begin);
            }
        }

        internal void RaiseOnSend(ProxySocket socket, byte[] packet)
        {
            if (OnSend == null || socket == null || packet == null)
            {
                return;
            }

            MemoryStream stream = new MemoryStream(packet);
            OnSend(socket, socket.ID, this, ref stream);
        }

        internal void RaiseOnRecv(ProxySocket socket, byte[] packet)
        {
            if (OnRecv == null || socket == null || packet == null)
            {
                return;
            }

            MemoryStream stream = new MemoryStream(packet);
            OnRecv(socket, socket.ID, this, ref stream);
        }

        private void StartPolling()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _pollThread = new Thread(PollGameClients);
            _pollThread.IsBackground = true;
            _pollThread.Name = "WrenLib.GameClientPoller";
            _pollThread.Start();
        }

        private void PollGameClients()
        {
            while (_running)
            {
                try
                {
                    Process[] processes = Process.GetProcessesByName("Darkages");
                    Dictionary<uint, IntPtr> current = new Dictionary<uint, IntPtr>();
                    foreach (Process process in processes)
                    {
                        try
                        {
                            if (process.MainWindowHandle == IntPtr.Zero)
                            {
                                continue;
                            }

                            uint key = unchecked((uint)process.Id);
                            current[key] = process.MainWindowHandle;
                        }
                        catch
                        {
                        }
                    }

                    lock (_pollLock)
                    {
                        foreach (KeyValuePair<uint, IntPtr> kvp in current)
                        {
                            if (!_knownWindows.ContainsKey(kvp.Key))
                            {
                                RegisterClient(kvp.Key, kvp.Value);
                            }
                        }

                        uint[] knownIds = new uint[_knownWindows.Count];
                        _knownWindows.Keys.CopyTo(knownIds, 0);
                        foreach (uint pid in knownIds)
                        {
                            if (!current.ContainsKey(pid))
                            {
                                UnregisterClient(pid);
                            }
                        }
                    }
                }
                catch
                {
                }

                Thread.Sleep(1500);
            }
        }

        private void RegisterClient(uint processId, IntPtr mainWindow)
        {
            Process process = null;
            try
            {
                process = Process.GetProcessById((int)processId);
            }
            catch
            {
                return;
            }

            ProxySocket socket = new ProxySocket(this)
            {
                ID = _nextSocketId++,
                ProcessId = processId,
                MainWindowHandle = mainWindow,
                ConnectedSocket = null,
                Serial = processId
            };
            socket.ConnectedSocket = socket;

            string name = ReadPlayerName(process);
            socket.Name = string.IsNullOrEmpty(name) ? process.ProcessName : name;

            this.Clients[socket.ID] = socket;
            _knownWindows[processId] = mainWindow;

            if (OnConnect != null)
            {
                OnConnect(socket, socket.ID, this, socket.Serial.Reverse());
            }

            if (OnGameEnter != null)
            {
                OnGameEnter(socket, socket.ID, this, socket.Name);
            }

            if (OnGameServerConnect != null)
            {
                OnGameServerConnect(socket, socket.ID, this, socket.Serial.Reverse());
            }
        }

        private void UnregisterClient(uint processId)
        {
            ProxySocket target = null;
            foreach (KeyValuePair<uint, ProxySocket> kvp in this.Clients.ToIEnumerable())
            {
                if (kvp.Value.ProcessId == processId)
                {
                    target = kvp.Value;
                    break;
                }
            }

            _knownWindows.Remove(processId);
            if (target != null)
            {
                DisconnectSocket(target);
            }
        }

        public void DisconnectSocket(ProxySocket Socket)
        {
            if (Socket == null)
            {
                return;
            }

            try
            {
                _disconnectReset.WaitOne();
                _disconnectReset.Reset();
                if (OnDisconnect != null)
                {
                    OnDisconnect(Socket, Socket.ID, this, Socket.Serial.Reverse());
                }
                this.Clients.Remove(Socket.ID);
            }
            finally
            {
                _disconnectReset.Set();
            }
        }

        private static string ReadPlayerName(Process process)
        {
            try
            {
                byte[] buffer = new byte[20];
                IntPtr bytesRead;
                if (ReadProcessMemory(process.Handle, (IntPtr)PLAYER_NAME_ADDRESS, buffer, buffer.Length, out bytesRead))
                {
                    string value = Encoding.ASCII.GetString(buffer).TrimEnd('\0', ' ');
                    return value;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            int nSize,
            out IntPtr lpNumberOfBytesRead);
    }

    public class ProxySocket
    {
        public ProxySocket(NewProxy server)
        {
            this.Server = server;
            this.ConnectedSocket = this;
            this.SendReset = new ManualResetEvent(true);
            this.Ordinal = 0;
            this.Encryption = null;
            this.Serial = 0;
        }

        public byte HPPercent = 100;
        public bool IsLoaded = false;
        public NewProxy Server;
        public ManualResetEvent SendReset;
        public byte[] PacketData;
        public ProxySocket ConnectedSocket;
        public uint ID = 0;
        public uint Serial = 0;
        public string Name;
        public Encryption Encryption;
        public string UserName;
        public bool LoggedIn = true;
        public uint ProcessId;
        public IntPtr MainWindowHandle;
        public byte Ordinal;

        public void SendToClient(byte[] packet, uint Serial)
        {
            // In process-hook mode there is no proxying server->client socket.
            // Preserve semantics by feeding packet handlers directly.
            if (packet == null)
            {
                return;
            }

            Server.RaiseOnRecv(this, packet);
        }

        public void SendToServer(byte[] packet, uint Serial)
        {
            if (packet == null)
            {
                return;
            }

            foreach (KeyValuePair<uint, ProxySocket> Sockets in Server.Clients.ToIEnumerable())
            {
                bool isTarget = (Serial != 0) ? (Sockets.Value.Serial == Serial) : (Sockets.Value.ID == this.ID);
                if (isTarget)
                {
                    Sockets.Value.SendPacketTransformed(packet);
                }
            }
        }

        public void SendPacketRaw(byte[] PacketData)
        {
            SendPacketTransformed(PacketData);
        }

        public void SendPacketTransformed(byte[] PacketData)
        {
            if (PacketData == null || PacketData.Length == 0)
            {
                return;
            }

            try
            {
                this.SendReset.WaitOne();
                this.SendReset.Reset();

                byte[] payload = new byte[PacketData.Length];
                Array.Copy(PacketData, payload, PacketData.Length);
                Server.RaiseOnSend(this, payload);
                SendUsingDaOperation(payload);
            }
            catch
            {
            }
            finally
            {
                this.SendReset.Set();
            }
        }

        private void SendUsingDaOperation(byte[] packet)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }

            if (ProcessId == 0)
            {
                return;
            }

            Process process = null;
            try
            {
                process = Process.GetProcessById((int)ProcessId);
            }
            catch
            {
                return;
            }

            IntPtr sender = ReadPointer(process, (IntPtr)NewProxy.SEND_THIS_ADDRESS);
            if (sender == IntPtr.Zero)
            {
                return;
            }

            IntPtr remotePacket = VirtualAllocEx(process.Handle, IntPtr.Zero, (uint)packet.Length, 0x1000 | 0x2000, 0x40);
            if (remotePacket == IntPtr.Zero)
            {
                return;
            }

            try
            {
                IntPtr written;
                if (!WriteProcessMemory(process.Handle, remotePacket, packet, packet.Length, out written))
                {
                    return;
                }

                // pushad; pushfd; mov edx,len; push edx; mov eax,payload; push eax; mov ecx,sender; mov eax,func; call eax; popfd; popad; ret
                byte[] shellcode = new byte[]
                {
                    0x9C, 0x60, 0xBA, 0,0,0,0, 0x52, 0xB8, 0,0,0,0,
                    0x50, 0xB9, 0,0,0,0, 0xB8, 0,0,0,0, 0xFF, 0xD0,
                    0x61, 0x9D, 0xC3
                };

                WriteInt32(shellcode, 3, packet.Length);
                int packetAddress32;
                int senderAddress32;
                if (!TryGet32BitAddress(remotePacket, out packetAddress32) || !TryGet32BitAddress(sender, out senderAddress32))
                {
                    return;
                }

                WriteInt32(shellcode, 9, packetAddress32);
                WriteInt32(shellcode, 15, senderAddress32);
                WriteInt32(shellcode, 20, NewProxy.FUNC_SEND_ADDRESS);

                IntPtr remoteShell = VirtualAllocEx(process.Handle, IntPtr.Zero, (uint)shellcode.Length, 0x1000 | 0x2000, 0x40);
                if (remoteShell == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    if (!WriteProcessMemory(process.Handle, remoteShell, shellcode, shellcode.Length, out written))
                    {
                        return;
                    }

                    IntPtr thread = CreateRemoteThread(process.Handle, IntPtr.Zero, 0, remoteShell, IntPtr.Zero, 0, IntPtr.Zero);
                    if (thread != IntPtr.Zero)
                    {
                        WaitForSingleObject(thread, 250);
                        CloseHandle(thread);
                    }
                }
                finally
                {
                    VirtualFreeEx(process.Handle, remoteShell, 0, 0x8000);
                }
            }
            finally
            {
                VirtualFreeEx(process.Handle, remotePacket, 0, 0x8000);
            }
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            buffer[offset] = bytes[0];
            buffer[offset + 1] = bytes[1];
            buffer[offset + 2] = bytes[2];
            buffer[offset + 3] = bytes[3];
        }

        private static bool TryGet32BitAddress(IntPtr address, out int value)
        {
            long raw = address.ToInt64();
            if (raw <= 0 || raw > uint.MaxValue)
            {
                value = 0;
                return false;
            }

            value = unchecked((int)raw);
            return true;
        }

        private static IntPtr ReadPointer(Process process, IntPtr address)
        {
            try
            {
                byte[] data = new byte[4];
                IntPtr read;
                if (ReadProcessMemory(process.Handle, address, data, data.Length, out read))
                {
                    return (IntPtr)BitConverter.ToInt32(data, 0);
                }
            }
            catch
            {
            }

            return IntPtr.Zero;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            int nSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            byte[] lpBuffer,
            int nSize,
            out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            uint dwSize,
            uint flAllocationType,
            uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            uint dwSize,
            uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(
            IntPtr hProcess,
            IntPtr lpThreadAttributes,
            uint dwStackSize,
            IntPtr lpStartAddress,
            IntPtr lpParameter,
            uint dwCreationFlags,
            IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    // Encryption retained from previous implementation.
    public class Encryption
    {
        private static byte[][] Seeds =
        {
            new byte[]
            {
                0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
                0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
                0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
                0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
                0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
                0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
                0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
                0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
                0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
                0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
                0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
                0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
                0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
                0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
                0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
                0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF
            }
        };

        public byte Seed;
        public byte[] Key;

        unsafe public void Transform(byte[] Packet)
        {
            if (Packet == null || Packet.Length < 3 || Key == null || Key.Length == 0)
            {
                return;
            }

            byte[] seedTable = Seeds[0];
            fixed (byte* PData = Packet, PKey = Key, PSeed = seedTable)
            {
                byte Ordinal = PData[1], Mod = 0;
                int i = 0;
                for (byte* PCur = PData + 2, PEnd = PData + Packet.Length; PCur < PEnd; PCur++, i++)
                {
                    Mod = (byte)(i / Key.Length);
                    *PCur ^= (byte)(
                        *(PKey + (i % Key.Length)) ^
                        *(PSeed + Ordinal) ^
                        *(PSeed + Mod));
                    if (Mod == Ordinal)
                    {
                        *PCur ^= *(PSeed + Ordinal);
                    }
                }
            }
        }
    }
}
