using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowerIndicatorLinkService
{
    public partial class PowerIndicatorLinkService : ServiceBase
    {
        private readonly string[] SPLIT_NEWLINE = new string[] { "\r\n" };
        private readonly string POWER_INDICATOR_HARDWARE_ID = "USB\\VID_239A&PID_8021&MI_00";
        private readonly byte[] SIGNAL_RELOAD = new byte[] { 0x04 };
        private readonly byte[] SIGNAL_BREAK = new byte[] { 0x03 };
        private readonly byte[] NEWLINE = Encoding.UTF8.GetBytes("\r\n");

        private SerialPort _serialPort = null;
        private bool _isStarted = false;
        private BlockingCollection<string> _serialBufferQueue = null;
        private string _serialBufferString = null;
        private Task _processingQueueTask = null;
        private CancellationTokenSource _processingQueueTaskToken = null;
        private bool _isDeviceReset = false;
        private bool _isDeviceMonitorOn = false;
        private Timer _suspendTimer = null;

        private EventLog _eventLog = null;

        public PowerIndicatorLinkService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            _eventLog = new EventLog("Application");
            _eventLog.Source = Process.GetCurrentProcess().ProcessName;

            if (!_isStarted)
            {
                _eventLog.WriteEntry("Started Power Indicator Link Service.", EventLogEntryType.Information);

                var portName = FindPowerIndicatorCOMPort();

                if (!string.IsNullOrEmpty(portName))
                {
                    _eventLog.WriteEntry($"Found serial port: {portName}.", EventLogEntryType.Information);

                    _suspendTimer = new Timer(SuspendAction, null, Timeout.Infinite, Timeout.Infinite);
                    SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
                    _serialBufferQueue = new BlockingCollection<string>();
                    _serialBufferString = "";
                    _processingQueueTaskToken = new CancellationTokenSource();
                    _processingQueueTask = Task.Factory.StartNew(SerialBufferQueueProcessing, _processingQueueTaskToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    _serialPort = new SerialPort(portName, 9600, Parity.None);
                    _serialPort.Open();
                    _serialPort.DtrEnable = true;
                    _serialPort.BaseStream.Flush();
                    _serialPort.DataReceived += _serialPort_DataReceived;

                    bool reset = PowerIndicatorReset();
                    _isStarted = true;

                    if (reset)
                    {
                        if (!PowerIndicatorStartMonitor())
                        {
                            _eventLog.WriteEntry("Could not start power monitoring.", EventLogEntryType.Error);
                            Stop();
                        }
                    }
                    else
                    {
                        _eventLog.WriteEntry("Device not responding.", EventLogEntryType.Error);
                        Stop();
                    }
                }
                else
                {
                    _eventLog.WriteEntry("Could not find the serial port.", EventLogEntryType.Error);
                    Stop();
                }
            }
        }

        protected override void OnStop()
        {
            if (_isStarted)
            {
                SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
                lock (_suspendTimer)
                {
                    _suspendTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                _suspendTimer.Dispose();
                _suspendTimer = null;
                _serialPort.DataReceived -= _serialPort_DataReceived;
                _serialPort.Close();
                _serialPort = null;
                _processingQueueTaskToken?.Cancel();
                try
                {
                    _processingQueueTask.Wait();
                }
                catch (AggregateException)
                {
                    // ignore
                }
                _processingQueueTaskToken = null;
                _processingQueueTask = null;
                _serialBufferString = null;
                _serialBufferQueue = null;
            }

            _eventLog.WriteEntry("Stopped Power Indicator Link Service.", EventLogEntryType.Information);
            _eventLog?.Close();
            _eventLog = null;
        }

        private string FindPowerIndicatorCOMPort()
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM WIN32_SerialPort"))
            {
                string[] portnames = SerialPort.GetPortNames();
                var ports = searcher.Get().Cast<ManagementBaseObject>().ToList();
                foreach (var port in ports)
                {
                    var pnpId = (port["PNPDeviceID"] as string).Split('\\');
                    if (pnpId.Length > 2)
                    {
                        var hardwareId = string.Join("\\", new string[] { pnpId[0], pnpId[1] });
                        if (hardwareId == POWER_INDICATOR_HARDWARE_ID)
                        {
                            return port["DeviceID"] as string;
                        }
                    }
                }

                return string.Empty;
            }
        }

        private void SerialBufferQueueProcessing()
        {
            while (!_processingQueueTaskToken.IsCancellationRequested)
            {
                string queueItem = _serialBufferQueue.Take(_processingQueueTaskToken.Token);

                if (_processingQueueTaskToken.IsCancellationRequested)
                {
                    return;
                }

                string buffer = _serialBufferString + queueItem;
                var bufferSplit = new Queue<string>(buffer.Split(SPLIT_NEWLINE, StringSplitOptions.None));

                if (_processingQueueTaskToken.IsCancellationRequested)
                {
                    return;
                }

                while (bufferSplit.Count > 1) // The one item should be an unfinished line
                {
                    var line = bufferSplit.Dequeue();

                    if (!string.IsNullOrEmpty(line))
                    {
                        PowerIndicatorParse(line);
                    }

                    if (_processingQueueTaskToken.IsCancellationRequested)
                    {
                        _serialBufferString = string.Join(SPLIT_NEWLINE[0], bufferSplit);
                        return;
                    }
                }

                _serialBufferString = bufferSplit.Last();
            }
        }

        private bool PowerIndicatorReset()
        {
            _isDeviceReset = false;
            _serialPort.Write(NEWLINE, 0, NEWLINE.Length);
            _serialPort.Write(SIGNAL_RELOAD, 0, SIGNAL_RELOAD.Length);
            Thread.Sleep(500);
            if (!_isDeviceReset)
            {
                _serialPort.Write(SIGNAL_BREAK, 0, SIGNAL_BREAK.Length);
                _serialPort.Write(SIGNAL_RELOAD, 0, SIGNAL_RELOAD.Length);
                Thread.Sleep(1000);

                if (!_isDeviceReset)
                {
                    return false;
                }
            }

            return true;
        }

        private bool PowerIndicatorStartMonitor()
        {
            var bytes = Encoding.ASCII.GetBytes("request,POWER_MONITOR\r\n");
            _serialPort.Write(bytes, 0, bytes.Length);
            Thread.Sleep(500);
            return _isDeviceMonitorOn;
        }

        private void PowerIndicatorParse(string line)
        {
            var splitLine = line.Split(',');

            _isDeviceReset = false;

            if (splitLine.Length == 2)
            {

                if (splitLine[0] == "event")
                {
                    if (splitLine[1] == "START")
                    {
                        _isDeviceReset = true;
                    }
                    else if (splitLine[1] == "POWER_LOST")
                    {
                        // Power lost action
                        _eventLog.WriteEntry("Mains power has been lost.", EventLogEntryType.Information);
                        lock (_suspendTimer)
                        {
                            _suspendTimer.Change(5000, Timeout.Infinite);
                        }
                    }
                    else if (splitLine[1] == "POWER_RESTORED")
                    {
                        // Power restored action
                        _eventLog.WriteEntry("Mains power has been restored.", EventLogEntryType.Information);
                        lock (_suspendTimer)
                        {
                            _suspendTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        }
                    }
                }
                else if (splitLine[0] == "response")
                {
                    if (splitLine[1] == "POWER_MONITOR_ON")
                    {
                        _isDeviceMonitorOn = true;
                    }
                }
            }

        }

        private void SuspendAction(object state)
        {
            lock (_suspendTimer)
            {
                var inactiveTime = GetInactiveTime();
                if (inactiveTime.HasValue && inactiveTime.Value.TotalSeconds > 10 || !inactiveTime.HasValue)
                {
                    _eventLog.WriteEntry("Suspending system.", EventLogEntryType.Warning);

                    SetSuspendState(false, false, false);
                }
                else
                {
                    _eventLog.WriteEntry("User is present, delaying suspend.", EventLogEntryType.Information);
                    _suspendTimer.Change(10000, Timeout.Infinite);
                }
            }
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                lock (_suspendTimer)
                {
                    _suspendTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
        }

        private void _serialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var data = _serialPort.ReadExisting();
            _serialBufferQueue.Add(data);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("Powrprof.dll", SetLastError = true)]
        static extern uint SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        public static TimeSpan? GetInactiveTime()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            if (GetLastInputInfo(ref info))
                return TimeSpan.FromMilliseconds(Environment.TickCount - info.dwTime);
            else
                return null;
        }
    }
}
