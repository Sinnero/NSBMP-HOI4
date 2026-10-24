using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NSBMP.Pacer
{
    public enum SessionState
    {
        Disconnected,
        AttachedInitializing,
        LoadingOrMenu,
        SingleplayerOrClient,
        MultiplayerHostPaused,
        MultiplayerHostActive
    }

    public class PacerStatusEventArgs : EventArgs
    {
        public bool IsAttached { get; set; }
        public int ProcessId { get; set; }
        public string StateMessage { get; set; }
        public int CurrentSpeed { get; set; }
        public float CurrentDelayMs { get; set; }
        public float DaySeconds { get; set; }
        public int ConnectedClients { get; set; }
        public int MaxLagHours { get; set; }
        public float PingFloorHours { get; set; }
        public float MpBaseDelayMs { get; set; }
        public string Mode { get; set; }
        public bool IsPaused { get; set; }
        public SessionState State { get; set; }
        public bool IsReadOnly { get; set; }
    }

    public class PacerEngine : IDisposable
    {
        // Canonical 64-bit user address space bounds
        public const long MIN_USER_ADDR = 0x10000;
        public const long MAX_USER_ADDR = 0x7FFFFFFFFFFF;

        // Known default RVAs for HOI4 v1.19.3.0 (fallback if signature scan fails)
        public const long DEFAULT_RVA_GAME_SPEED_SECONDS = 0x033386C0;
        public const long DEFAULT_RVA_GAMESTATE = 0x0332F260;
        public const long DEFAULT_RVA_GAME_APP = 0x0332F698;
        public const long DEFAULT_RVA_SERVER_SESSION = 0x0332F6A0;

        // Engine struct offsets (Verified on Hearts of Iron IV v1.19.3.0+)
        public const int OFFSET_SPEED_REDUCED = 0x054;
        public const int OFFSET_APP_GAME = 0x380;
        public const int OFFSET_GAME_HOST_ID = 0x0A4;
        public const int OFFSET_HOST_HOURS = 0x468;
        public const int OFFSET_CURRENT_SPEED = 0x4BC;
        public const int OFFSET_CLIENT_COUNT = 0x104;
        public const int OFFSET_CLIENT_ARRAY = 0x0F8;
        public const int OFFSET_LAG_RECOVERY_COUNTER = 0x95C;

        public const int CLIENT_STRUCT_SIZE = 0xA0;
        public const int OFFSET_CLIENT_ID = 0x98;
        public const int OFFSET_CLIENT_HOURS = 0x80;
        public const int SENTINEL_UNINIT_HOURS = 43808760;

        public IntPtr ProcessHandle { get; private set; }
        public int ProcessId { get; private set; }
        public long BaseAddress { get; private set; }
        public string ExePath { get; private set; }

        public long RvaSpeed { get; private set; }
        public long RvaGamestate { get; private set; }
        public long RvaApp { get; private set; }
        public long RvaServerSession { get; private set; }
        public bool IsPointerDeref { get; private set; }
        public long Speed5Address { get; private set; }

        public float DefaultSph { get; set; }
        public float MaxDelay { get; set; }
        public float MpBaseDelay { get; set; }
        public float CurrentDelay { get; private set; }

        private float? _lastWrittenDelay;
        private double _lastWriteTime;
        private double _lastHostStepTime;
        private int? _lastHostHours;
        private bool _wasHostAdvancing;
        private int _unpauseSettleHour;
        private bool _hasModifiedMemory = false;

        // Dynamic ping floor & 24h equilibrium
        public float EstPingFloor { get; private set; }
        private readonly Queue<float> _minLagHistory = new Queue<float>();
        private int? _dayWindowStartHour;
        private readonly List<int> _windowLagSamples = new List<int>();
        private int _windowMinLag = 999;
        private int _windowMaxLag = 0;

        private readonly Dictionary<int, ClientTrack> _clientStats = new Dictionary<int, ClientTrack>();
        private readonly List<KeyValuePair<int, int>> _validClients = new List<KeyValuePair<int, int>>(64);
        private readonly HashSet<int> _activeClientSet = new HashSet<int>();
        private readonly List<int> _staleClientList = new List<int>(64);

        public event EventHandler<PacerStatusEventArgs> StatusUpdated;

        private class ClientTrack
        {
            public int LastHour;
            public double LastTime;
            public float InstMs;
        }

        public static bool IsValidUserPointer(long ptr, int alignment = 1)
        {
            return ptr >= MIN_USER_ADDR && ptr <= MAX_USER_ADDR && (ptr % alignment == 0);
        }

        public PacerEngine(float defaultSph = 0.042f, float maxDelay = 0.200f)
        {
            DefaultSph = defaultSph;
            MaxDelay = maxDelay;
            MpBaseDelay = defaultSph;
            CurrentDelay = defaultSph;
            EstPingFloor = 2.0f;
            RvaSpeed = DEFAULT_RVA_GAME_SPEED_SECONDS;
            RvaGamestate = DEFAULT_RVA_GAMESTATE;
            RvaApp = DEFAULT_RVA_GAME_APP;
            RvaServerSession = DEFAULT_RVA_SERVER_SESSION;
            IsPointerDeref = true;
        }

        public bool IsAttached
        {
            get
            {
                if (ProcessHandle == IntPtr.Zero) return false;
                uint exitCode;
                if (Win32.GetExitCodeProcess(ProcessHandle, out exitCode))
                {
                    return exitCode == Win32.STILL_ACTIVE;
                }
                return false;
            }
        }

        public bool TryAttach()
        {
            if (IsAttached) return true;
            Close();

            int pid = FindProcessId("hoi4.exe");
            if (pid == 0) return false;

            IntPtr hProc = Win32.OpenProcess(Win32.PROCESS_VM_READ | Win32.PROCESS_VM_WRITE | Win32.PROCESS_VM_OPERATION | Win32.PROCESS_QUERY_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero)
            {
                hProc = Win32.OpenProcess(Win32.PROCESS_ALL_ACCESS, false, pid);
                if (hProc == IntPtr.Zero) return false;
            }

            ProcessHandle = hProc;
            ProcessId = pid;

            long baseAddr;
            string exePath;
            if (GetModuleInfo(pid, "hoi4.exe", out baseAddr, out exePath))
            {
                BaseAddress = baseAddr;
                ExePath = exePath;
            }
            else
            {
                Close();
                return false;
            }

            // Perform dynamic PE signature scan on live executable
            ScanPeSignatures(ExePath);

            // Attempt initial Speed 5 address resolution (non-blocking if game still loading)
            ResolveSpeed5Address();

            _lastHostStepTime = GetTime();
            return true;
        }

        public void Close()
        {
            if (ProcessHandle != IntPtr.Zero)
            {
                ResetSpeed5Unlocked();
                Win32.CloseHandle(ProcessHandle);
                ProcessHandle = IntPtr.Zero;
            }
            ProcessId = 0;
            BaseAddress = 0;
            Speed5Address = 0;
            _hasModifiedMemory = false;
        }

        public void Dispose()
        {
            Close();
        }

        public bool ResetSpeed5Unlocked()
        {
            if (!IsAttached || !_hasModifiedMemory) return false;

            if (Speed5Address != 0 && IsValidUserPointer(Speed5Address, 4))
            {
                SafeWriteFloat(Speed5Address, 0.0f);
                _lastWrittenDelay = 0.0f;
            }

            long pApp = ReadPtr(BaseAddress + RvaApp);
            long pServer = ReadPtr(BaseAddress + RvaServerSession);
            long pGamestate = ReadPtr(BaseAddress + RvaGamestate);

            // Only clear lag counter if pServer is valid and strictly distinct from gamestate
            if (IsValidUserPointer(pServer, 8) && pServer != pGamestate)
            {
                SafeWriteInt32(pServer + OFFSET_LAG_RECOVERY_COUNTER, 0);
            }

            if (IsValidUserPointer(pApp, 8))
            {
                long pGame = ReadPtr(pApp + OFFSET_APP_GAME);
                if (IsValidUserPointer(pGame, 8))
                {
                    SafeWriteByte(pGame + OFFSET_SPEED_REDUCED, 0);
                }
            }

            _hasModifiedMemory = false;
            return true;
        }

        public void Update()
        {
            if (!IsAttached)
            {
                OnStatusUpdate(false, 0, "Waiting for Hearts of Iron IV (hoi4.exe)...", 0, 0, 0, 0, 0, 0, 0, "DISCONNECTED", false, SessionState.Disconnected, true);
                return;
            }

            double now = GetTime();
            double dt = Math.Max(0.001, Math.Min(0.2, now - _lastWriteTime));

            // Dynamically resolve Speed5Address if not yet ready
            if (Speed5Address == 0 || !IsValidUserPointer(Speed5Address, 4))
            {
                ResolveSpeed5Address();
            }

            long pGamestate = ReadPtr(BaseAddress + RvaGamestate);
            long pApp = ReadPtr(BaseAddress + RvaApp);
            long pServer = ReadPtr(BaseAddress + RvaServerSession);
            long pGame = IsValidUserPointer(pApp, 8) ? ReadPtr(pApp + OFFSET_APP_GAME) : 0;

            // 1. SAFETY GUARD: Verify pGamestate
            if (!IsValidUserPointer(pGamestate, 8))
            {
                OnStatusUpdate(true, ProcessId, "Attached to hoi4.exe | Game loading (Safe Read-Only)...", 0, 0, 0, 0, 0, 0, 0, "INITIALIZING", false, SessionState.AttachedInitializing, true);
                return;
            }

            // Read game state
            int hostHours = ReadInt32(pGamestate + OFFSET_HOST_HOURS);
            int currentSpeed = ReadInt32(pGamestate + OFFSET_CURRENT_SPEED);
            int clientCount = ReadInt32(pGamestate + OFFSET_CLIENT_COUNT);
            long clientArray = ReadPtr(pGamestate + OFFSET_CLIENT_ARRAY);
            
            // In HOI4 multiplayer, Client[0] in clientArray is always the local hosting player.
            int hostPlayerId = 0;
            if (IsValidUserPointer(clientArray, 8) && clientCount > 0)
            {
                hostPlayerId = ReadInt32(clientArray + OFFSET_CLIENT_ID);
            }
            if (hostPlayerId <= 0 && IsValidUserPointer(pGame, 8))
            {
                int cand = ReadInt32(pGame + OFFSET_GAME_HOST_ID);
                if (cand > 0 && cand < 1000) hostPlayerId = cand;
            }
            if (hostPlayerId <= 0)
            {
                hostPlayerId = 1;
            }

            // 2. SAFETY GUARD: Active campaign timestamp check
            // HOI4 epoch: 43,800,000 + (Year * 365 * 24). For 1936, hostHours is ~60,759,000.
            bool isCampaignActive = (hostHours >= 50000000 && hostHours <= 85000000);
            if (!isCampaignActive)
            {
                // In main menu, pre-game loading screen, or lobby setup
                if (_hasModifiedMemory) ResetSpeed5Unlocked();
                string diag = string.Format("Main Menu / Loading Screen (Safe Read-Only | hostHours={0}, speed={1})", hostHours, currentSpeed + 1);
                OnStatusUpdate(true, ProcessId, diag, currentSpeed + 1, 0, 0, 0, 0, EstPingFloor, MpBaseDelay, "SAFE_READONLY", false, SessionState.LoadingOrMenu, true);
                return;
            }

            if (_lastHostHours.HasValue && hostHours != _lastHostHours.Value)
            {
                _lastHostHours = hostHours;
                _lastHostStepTime = now;
            }
            else if (!_lastHostHours.HasValue && hostHours > 0)
            {
                _lastHostHours = hostHours;
                _lastHostStepTime = now;
            }

            float expectedStepSec = (currentSpeed == 0) ? 3.0f : (currentSpeed == 1 ? 1.5f : (currentSpeed == 2 ? 0.8f : 0.5f));
            bool hostIsAdvancing = (now - _lastHostStepTime) < expectedStepSec;

            // Post-unpause transient guard: ignore min_lag for 8 in-game hours after unpausing
            if (hostIsAdvancing && !_wasHostAdvancing)
            {
                _unpauseSettleHour = hostHours + 8;
            }
            _wasHostAdvancing = hostIsAdvancing;

            // 3. SAFETY GUARD: Host check & Connected client array check
            // If clientCount > 1 and clientArray is valid, local player is hosting a multiplayer session with clients.
            bool isMultiplayerHost = (clientCount > 1 && IsValidUserPointer(clientArray, 8));

            // Iterate over connected clients (Zero-allocation reusable buffers)
            _validClients.Clear();
            int maxLagHours = 0;

            if (isMultiplayerHost)
            {
                for (int i = 0; i < Math.Min(clientCount, 64); i++)
                {
                    long clientPtr = clientArray + (i * CLIENT_STRUCT_SIZE);
                    if (!IsValidUserPointer(clientPtr, 4)) continue;

                    int clientId = ReadInt32(clientPtr + OFFSET_CLIENT_ID);
                    int clientHours = ReadInt32(clientPtr + OFFSET_CLIENT_HOURS);

                    // Client[0] is the local host; ignore it as a lag client.
                    // Also ignore uninitialized sentinel or invalid hour ranges.
                    if (i == 0 || clientId == 0 || clientId == hostPlayerId || clientHours == SENTINEL_UNINIT_HOURS ||
                        clientHours < 50000000 || clientHours > hostHours + 100)
                    {
                        continue;
                    }

                    int lagHours = Math.Max(0, hostHours - clientHours);
                    if (lagHours > maxLagHours) maxLagHours = lagHours;

                    ClientTrack track;
                    if (!_clientStats.TryGetValue(clientId, out track))
                    {
                        track = new ClientTrack { LastHour = clientHours, LastTime = now, InstMs = 0f };
                        _clientStats[clientId] = track;
                    }
                    else
                    {
                        if (hostIsAdvancing && clientHours != track.LastHour)
                        {
                            int dh = clientHours - track.LastHour;
                            double dtTick = now - track.LastTime;
                            if (dh > 0 && dtTick > 0.001)
                            {
                                track.InstMs = (float)((dtTick / dh) * 1000.0);
                            }
                            track.LastHour = clientHours;
                            track.LastTime = now;
                        }
                    }

                    _validClients.Add(new KeyValuePair<int, int>(clientId, lagHours));
                }

                // Prune disconnected client IDs without GC heap allocations
                _activeClientSet.Clear();
                for (int i = 0; i < _validClients.Count; i++) _activeClientSet.Add(_validClients[i].Key);
                _staleClientList.Clear();
                foreach (var k in _clientStats.Keys) if (!_activeClientSet.Contains(k)) _staleClientList.Add(k);
                for (int i = 0; i < _staleClientList.Count; i++) _clientStats.Remove(_staleClientList[i]);
            }

            // 4. SAFETY GUARD: Singleplayer or Client Mode check (100% Read-Only)
            if (!isMultiplayerHost || _validClients.Count == 0)
            {
                if (_hasModifiedMemory) ResetSpeed5Unlocked();
                CurrentDelay = MpBaseDelay;
                string msg = "Singleplayer / Client Mode (Safe Read-Only | Unlocked 0.0s)";
                OnStatusUpdate(true, ProcessId, msg, currentSpeed + 1, 0, 0, 0, 0, EstPingFloor, MpBaseDelay, "UNLOCKED", !hostIsAdvancing, SessionState.SingleplayerOrClient, true);
                return;
            }

            // 5. SAFETY GUARD: Speed check (Standby if not on Speed 5)
            if (currentSpeed != 4)
            {
                if (_hasModifiedMemory) ResetSpeed5Unlocked();
                CurrentDelay = MpBaseDelay;
                string msg = string.Format("Speed {0} (Standby | MP Primed at {1:F1}ms)", currentSpeed + 1, MpBaseDelay * 1000f);
                OnStatusUpdate(true, ProcessId, msg, currentSpeed + 1, 0, 0, _validClients.Count, maxLagHours, EstPingFloor, MpBaseDelay, "STANDBY", !hostIsAdvancing, SessionState.MultiplayerHostPaused, true);
                return;
            }

            // =================================================================
            // AT THIS POINT ALL SAFETY CRITERIA ARE MET:
            // 1. In active campaign (hostHours >= 43800000)
            // 2. Local player is the MP Host (hostPlayerId != 0, clientCount > 1)
            // 3. At least one valid remote client is actively advancing
            // 4. Game is on Speed 5
            // =================================================================

            // Safely clear native lag recovery counter on CServerSession (if verified distinct from gamestate)
            if (IsValidUserPointer(pServer, 8) && pServer != pGamestate)
            {
                int lagCounter = ReadInt32(pServer + OFFSET_LAG_RECOVERY_COUNTER);
                if (lagCounter > 0) SafeWriteInt32(pServer + OFFSET_LAG_RECOVERY_COUNTER, 0);
            }

            // Safely clear speed-reduced flag on CGame
            if (IsValidUserPointer(pGame, 8))
            {
                byte speedFlag = ReadByte(pGame + OFFSET_SPEED_REDUCED);
                if (speedFlag != 0) SafeWriteByte(pGame + OFFSET_SPEED_REDUCED, 0);
            }

            // Primed pacing while paused
            if (!hostIsAdvancing)
            {
                CurrentDelay = MpBaseDelay;
                WriteSpeedDelay(CurrentDelay, now);
                _hasModifiedMemory = true;
                float daySecP = CurrentDelay * 24f;
                string msgP = string.Format("Speed 5 (PAUSED - Primed {0:F1}ms = {1:F2}s/d) | Clients: {2} | Lag: {3}h", CurrentDelay * 1000f, daySecP, _validClients.Count, maxLagHours);
                OnStatusUpdate(true, ProcessId, msgP, 5, CurrentDelay * 1000f, daySecP, _validClients.Count, maxLagHours, EstPingFloor, MpBaseDelay, "PAUSED", true, SessionState.MultiplayerHostPaused, false);
                return;
            }

            // -----------------------------------------------------------------
            // Dynamic Ping-Floor Estimation & Daily Equilibrium Adaptation
            // -----------------------------------------------------------------
            if (!_dayWindowStartHour.HasValue)
            {
                _dayWindowStartHour = hostHours;
                _windowLagSamples.Clear();
                _windowLagSamples.Add(maxLagHours);
                _windowMinLag = maxLagHours;
                _windowMaxLag = maxLagHours;
            }
            else
            {
                _windowLagSamples.Add(maxLagHours);
                if (hostHours >= _unpauseSettleHour)
                {
                    _windowMinLag = Math.Min(_windowMinLag, maxLagHours);
                }
                _windowMaxLag = Math.Max(_windowMaxLag, maxLagHours);

                int hoursInWindow = hostHours - _dayWindowStartHour.Value;
                if (hoursInWindow >= 24)
                {
                    float windowAvgLag = 0;
                    if (_windowLagSamples.Count > 0)
                    {
                        long sum = 0;
                        foreach (int s in _windowLagSamples) sum += s;
                        windowAvgLag = (float)sum / _windowLagSamples.Count;
                    }

                    _minLagHistory.Enqueue(_windowMinLag);
                    if (_minLagHistory.Count > 7) _minLagHistory.Dequeue();

                    float minSeen = 999;
                    foreach (float v in _minLagHistory) if (v < minSeen) minSeen = v;
                    EstPingFloor = Math.Max(1.0f, Math.Min(12.0f, minSeen));

                    // Triple-Metric Adaptation relative to learned ping floor
                    if (_windowMinLag <= EstPingFloor + 1 && _windowMaxLag <= EstPingFloor + 10 && windowAvgLag <= EstPingFloor + 3.5f)
                    {
                        MpBaseDelay = Math.Max(0.015f, MpBaseDelay - 0.0015f);
                    }
                    else if (_windowMinLag <= EstPingFloor + 3 && _windowMaxLag <= EstPingFloor + 12)
                    {
                        // Stable equilibrium across daily cycle; hold steady
                    }
                    else
                    {
                        float penalty = 0.0020f + 0.0005f * Math.Max(0, _windowMinLag - (EstPingFloor + 3));
                        MpBaseDelay = Math.Min(MaxDelay, MpBaseDelay + penalty);
                    }

                    _dayWindowStartHour = hostHours;
                    _windowLagSamples.Clear();
                    _windowLagSamples.Add(maxLagHours);
                    _windowMinLag = maxLagHours;
                    _windowMaxLag = maxLagHours;
                }
            }

            // -----------------------------------------------------------------
            // Dynamic Proportional Cushion & Emergency Brake
            // -----------------------------------------------------------------
            int lockstepCeil = Math.Max(10, Math.Min(18, (int)EstPingFloor + 8));
            int brakeFloor = Math.Max(21, lockstepCeil + 9);

            string mode;
            float targetDelay;

            if (maxLagHours <= lockstepCeil)
            {
                mode = "LOCKSTEP";
                targetDelay = MpBaseDelay;
            }
            else if (maxLagHours < brakeFloor)
            {
                mode = "CUSHION";
                targetDelay = Math.Min(MaxDelay, MpBaseDelay + 0.004f * (maxLagHours - lockstepCeil));
            }
            else
            {
                mode = "ANTI-DESYNC-BRAKE";
                targetDelay = Math.Min(MaxDelay, Math.Max(0.095f, MpBaseDelay + 0.006f * (maxLagHours - lockstepCeil)));
            }

            // Slew-rate limiting
            if (maxLagHours >= brakeFloor)
            {
                CurrentDelay = targetDelay;
            }
            else if (targetDelay > CurrentDelay)
            {
                float stepUp = (float)(0.030 * dt);
                CurrentDelay = Math.Min(targetDelay, CurrentDelay + stepUp);
            }
            else if (targetDelay < CurrentDelay)
            {
                float stepDown = (float)(0.025 * dt);
                CurrentDelay = Math.Max(targetDelay, CurrentDelay - stepDown);
            }

            if (maxLagHours <= lockstepCeil)
            {
                CurrentDelay = Math.Max(MpBaseDelay, CurrentDelay);
            }

            WriteSpeedDelay(CurrentDelay, now);
            _hasModifiedMemory = true;

            float daySeconds = CurrentDelay * 24f;
            string status = string.Format("Speed 5 ({0} {1:F1}ms/hr = {2:F2}s/d) | Clients: {3} | Lag: {4}h (Floor: {5:F1}h)",
                mode, CurrentDelay * 1000f, daySeconds, _validClients.Count, maxLagHours, EstPingFloor);

            OnStatusUpdate(true, ProcessId, status, 5, CurrentDelay * 1000f, daySeconds, _validClients.Count, maxLagHours, EstPingFloor, MpBaseDelay, mode, false, SessionState.MultiplayerHostActive, false);
        }

        private void WriteSpeedDelay(float delay, double now, bool force = false)
        {
            if (Speed5Address == 0 || !IsValidUserPointer(Speed5Address, 4)) return;

            if (force || !_lastWrittenDelay.HasValue || Math.Abs(delay - _lastWrittenDelay.Value) >= 0.0001f || (now - _lastWriteTime >= 1.0))
            {
                SafeWriteFloat(Speed5Address, delay);
                _lastWrittenDelay = delay;
                _lastWriteTime = now;
            }
        }

        public bool ResolveSpeed5Address()
        {
            if (BaseAddress == 0) return false;

            if (IsPointerDeref)
            {
                long ptrBase = ReadPtr(BaseAddress + RvaSpeed);
                if (IsValidUserPointer(ptrBase, 4))
                {
                    Speed5Address = ptrBase + 0x10;
                    return true;
                }
                Speed5Address = 0;
                return false;
            }
            else
            {
                long target = BaseAddress + RvaSpeed + 0x10;
                if (IsValidUserPointer(target, 4))
                {
                    Speed5Address = target;
                    return true;
                }
                Speed5Address = 0;
                return false;
            }
        }

        public void ScanPeSignatures(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            try
            {
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int peOffset = br.ReadInt32();
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    uint peSig = br.ReadUInt32();
                    if (peSig != 0x00004550) return; // 'PE\0\0'

                    fs.Seek(peOffset + 4 + 2, SeekOrigin.Begin);
                    short numSections = br.ReadInt16();
                    fs.Seek(peOffset + 4 + 16, SeekOrigin.Begin);
                    short optHeaderSize = br.ReadInt16();
                    int sectionTableOffset = peOffset + 4 + 20 + optHeaderSize;

                    for (int i = 0; i < numSections; i++)
                    {
                        fs.Seek(sectionTableOffset + (i * 40), SeekOrigin.Begin);
                        byte[] nameBytes = br.ReadBytes(8);
                        string secName = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

                        fs.Seek(sectionTableOffset + (i * 40) + 8, SeekOrigin.Begin);
                        int virtSize = br.ReadInt32();
                        int virtAddr = br.ReadInt32();
                        int rawSize = br.ReadInt32();
                        int rawAddr = br.ReadInt32();

                        if (secName == ".text")
                        {
                            fs.Seek(rawAddr, SeekOrigin.Begin);
                            byte[] code = br.ReadBytes(Math.Min(rawSize, 40000000));

                            // 1. Scan for GAME_SPEED_SECONDS: 48 [8B|8D] 05 (disp32) F3 0F 10 34 88
                            for (int p = 0; p < code.Length - 16; p++)
                            {
                                if (code[p] == 0x48 && (code[p + 1] == 0x8B || code[p + 1] == 0x8D) && code[p + 2] == 0x05)
                                {
                                    if (code[p + 7] == 0xF3 && code[p + 8] == 0x0F && code[p + 9] == 0x10 &&
                                        code[p + 10] == 0x34 && code[p + 11] == 0x88)
                                    {
                                        int disp = BitConverter.ToInt32(code, p + 3);
                                        long instrRva = virtAddr + p;
                                        long targetRva = instrRva + 7 + disp;
                                        if (targetRva > 0x1000)
                                        {
                                            RvaSpeed = targetRva;
                                            IsPointerDeref = (code[p + 1] == 0x8B);
                                            break;
                                        }
                                    }
                                }
                            }

                            // 2. Scan for CGamestate::_pInstance: 48 8B 2D (disp32) 48 8D 4C 24 30 E8
                            for (int p = 0; p < code.Length - 16; p++)
                            {
                                if (code[p] == 0x48 && code[p + 1] == 0x8B && code[p + 2] == 0x2D)
                                {
                                    if (code[p + 7] == 0x48 && code[p + 8] == 0x8D && code[p + 9] == 0x4C &&
                                        code[p + 10] == 0x24 && code[p + 11] == 0x30 && code[p + 12] == 0xE8)
                                    {
                                        int disp = BitConverter.ToInt32(code, p + 3);
                                        long instrRva = virtAddr + p;
                                        long targetRva = instrRva + 7 + disp;
                                        if (targetRva > 0x1000)
                                        {
                                            RvaGamestate = targetRva;
                                            break;
                                        }
                                    }
                                }
                            }

                            // 3. Scan for CGameApplication::sm_pInstance: 48 8B 0D (disp32) 48 8B 01 FF 90 88
                            for (int p = 0; p < code.Length - 16; p++)
                            {
                                if (code[p] == 0x48 && code[p + 1] == 0x8B && code[p + 2] == 0x0D)
                                {
                                    if (code[p + 7] == 0x48 && code[p + 8] == 0x8B && code[p + 9] == 0x01 &&
                                        code[p + 10] == 0xFF && code[p + 11] == 0x90 && code[p + 12] == 0x88)
                                    {
                                        int disp = BitConverter.ToInt32(code, p + 3);
                                        long instrRva = virtAddr + p;
                                        long targetRva = instrRva + 7 + disp;
                                        if (targetRva > 0x1000)
                                        {
                                            RvaApp = targetRva;
                                            break;
                                        }
                                    }
                                }
                            }

                            // 4. Scan for CServerSession::sm_pInstance:
                            // Supports both standard and direct dispatch signatures:
                            for (int p = 0; p < code.Length - 18; p++)
                            {
                                if (code[p] == 0x48 && code[p + 1] == 0x8B && code[p + 2] == 0x1D)
                                {
                                    bool isMatch = false;
                                    // Pattern A: 48 85 DB 74 ?? 48 8B CB E8
                                    if (code[p + 7] == 0x48 && code[p + 8] == 0x85 && code[p + 9] == 0xDB && code[p + 10] == 0x74 &&
                                        code[p + 12] == 0x48 && code[p + 13] == 0x8B && code[p + 14] == 0xCB && code[p + 15] == 0xE8)
                                    {
                                        isMatch = true;
                                    }
                                    // Pattern B: 48 8D 8B 38 05 00 00 E8
                                    else if (code[p + 7] == 0x48 && code[p + 8] == 0x8D && code[p + 9] == 0x8B &&
                                             code[p + 10] == 0x38 && code[p + 11] == 0x05 && code[p + 12] == 0x00 && code[p + 13] == 0x00)
                                    {
                                        isMatch = true;
                                    }

                                    if (isMatch)
                                    {
                                        int disp = BitConverter.ToInt32(code, p + 3);
                                        long instrRva = virtAddr + p;
                                        long targetRva = instrRva + 7 + disp;
                                        if (targetRva > 0x1000 && targetRva != RvaGamestate && targetRva != RvaApp)
                                        {
                                            RvaServerSession = targetRva;
                                            break;
                                        }
                                    }
                                }
                            }

                            break;
                        }
                    }
                }

                // Sanity verification of scanned RVAs against collision
                if (RvaGamestate == 0) RvaGamestate = DEFAULT_RVA_GAMESTATE;
                if (RvaApp == 0) RvaApp = DEFAULT_RVA_GAME_APP;
                if (RvaSpeed == 0) RvaSpeed = DEFAULT_RVA_GAME_SPEED_SECONDS;

                if (RvaServerSession == 0 || RvaServerSession == RvaGamestate || RvaServerSession == RvaApp)
                {
                    RvaServerSession = DEFAULT_RVA_SERVER_SESSION;
                }
            }
            catch
            {
                // Fall back to known static constants if binary scanning is blocked by permissions
                RvaSpeed = DEFAULT_RVA_GAME_SPEED_SECONDS;
                RvaGamestate = DEFAULT_RVA_GAMESTATE;
                RvaApp = DEFAULT_RVA_GAME_APP;
                RvaServerSession = DEFAULT_RVA_SERVER_SESSION;
            }
        }

        private static int FindProcessId(string processName)
        {
            try
            {
                string nameWithoutExt = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? processName.Substring(0, processName.Length - 4)
                    : processName;
                var procs = System.Diagnostics.Process.GetProcessesByName(nameWithoutExt);
                if (procs != null && procs.Length > 0)
                {
                    return procs[0].Id;
                }
            }
            catch { }

            IntPtr hSnap = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
            if (hSnap == IntPtr.Zero || hSnap == (IntPtr)(-1)) return 0;

            var pe = new Win32.PROCESSENTRY32W();
            pe.dwSize = (uint)Marshal.SizeOf(typeof(Win32.PROCESSENTRY32W));

            if (Win32.Process32FirstW(hSnap, ref pe))
            {
                do
                {
                    if (string.Equals(pe.szExeFile, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        Win32.CloseHandle(hSnap);
                        return (int)pe.th32ProcessID;
                    }
                } while (Win32.Process32NextW(hSnap, ref pe));
            }

            Win32.CloseHandle(hSnap);
            return 0;
        }

        private static bool GetModuleInfo(int pid, string moduleName, out long baseAddr, out string exePath)
        {
            baseAddr = 0;
            exePath = null;

            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                if (proc.MainModule != null)
                {
                    baseAddr = proc.MainModule.BaseAddress.ToInt64();
                    exePath = proc.MainModule.FileName;
                    if (baseAddr != 0) return true;
                }
            }
            catch { }

            IntPtr hSnap = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPMODULE | Win32.TH32CS_SNAPMODULE32, (uint)pid);
            if (hSnap == IntPtr.Zero || hSnap == (IntPtr)(-1)) return false;

            var me = new Win32.MODULEENTRY32W();
            me.dwSize = (uint)Marshal.SizeOf(typeof(Win32.MODULEENTRY32W));

            if (Win32.Module32FirstW(hSnap, ref me))
            {
                do
                {
                    if (string.Equals(me.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        baseAddr = me.modBaseAddr.ToInt64();
                        exePath = me.szExePath;
                        Win32.CloseHandle(hSnap);
                        return true;
                    }
                } while (Win32.Module32NextW(hSnap, ref me));
            }

            Win32.CloseHandle(hSnap);
            return false;
        }

        public long ReadPtr(long address)
        {
            if (!IsAttached || !IsValidUserPointer(address, 8)) return 0;
            byte[] buf = new byte[8];
            IntPtr read;
            if (Win32.ReadProcessMemory(ProcessHandle, new IntPtr(address), buf, 8, out read) && read.ToInt32() == 8)
            {
                return BitConverter.ToInt64(buf, 0);
            }
            return 0;
        }

        public int ReadInt32(long address)
        {
            if (!IsAttached || !IsValidUserPointer(address, 4)) return 0;
            byte[] buf = new byte[4];
            IntPtr read;
            if (Win32.ReadProcessMemory(ProcessHandle, new IntPtr(address), buf, 4, out read) && read.ToInt32() == 4)
            {
                return BitConverter.ToInt32(buf, 0);
            }
            return 0;
        }

        public byte ReadByte(long address)
        {
            if (!IsAttached || !IsValidUserPointer(address, 1)) return 0;
            byte[] buf = new byte[1];
            IntPtr read;
            if (Win32.ReadProcessMemory(ProcessHandle, new IntPtr(address), buf, 1, out read) && read.ToInt32() == 1)
            {
                return buf[0];
            }
            return 0;
        }

        public float ReadFloat(long address)
        {
            if (!IsAttached || !IsValidUserPointer(address, 4)) return 0f;
            byte[] buf = new byte[4];
            IntPtr read;
            if (Win32.ReadProcessMemory(ProcessHandle, new IntPtr(address), buf, 4, out read) && read.ToInt32() == 4)
            {
                return BitConverter.ToSingle(buf, 0);
            }
            return 0f;
        }

        public bool SafeWriteFloat(long address, float val)
        {
            if (!IsAttached || !IsValidUserPointer(address, 4)) return false;
            byte[] buf = BitConverter.GetBytes(val);
            IntPtr written;
            return Win32.WriteProcessMemory(ProcessHandle, new IntPtr(address), buf, 4, out written) && written.ToInt32() == 4;
        }

        public bool SafeWriteInt32(long address, int val)
        {
            if (!IsAttached || !IsValidUserPointer(address, 4)) return false;
            byte[] buf = BitConverter.GetBytes(val);
            IntPtr written;
            return Win32.WriteProcessMemory(ProcessHandle, new IntPtr(address), buf, 4, out written) && written.ToInt32() == 4;
        }

        public bool SafeWriteByte(long address, byte val)
        {
            if (!IsAttached || !IsValidUserPointer(address, 1)) return false;
            byte[] buf = new byte[] { val };
            IntPtr written;
            return Win32.WriteProcessMemory(ProcessHandle, new IntPtr(address), buf, 1, out written) && written.ToInt32() == 1;
        }

        private static double GetTime()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private void OnStatusUpdate(bool isAttached, int pid, string msg, int speed, float delayMs, float daySec, int clients, int lag, float pingFloor, float mpBase, string mode, bool isPaused, SessionState state, bool isReadOnly)
        {
            var handler = StatusUpdated;
            if (handler != null)
            {
                handler(this, new PacerStatusEventArgs
                {
                    IsAttached = isAttached,
                    ProcessId = pid,
                    StateMessage = msg,
                    CurrentSpeed = speed,
                    CurrentDelayMs = delayMs,
                    DaySeconds = daySec,
                    ConnectedClients = clients,
                    MaxLagHours = lag,
                    PingFloorHours = pingFloor,
                    MpBaseDelayMs = mpBase * 1000f,
                    Mode = mode,
                    IsPaused = isPaused,
                    State = state,
                    IsReadOnly = isReadOnly
                });
            }
        }
    }
}
