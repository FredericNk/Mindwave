using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO.Ports;
using System.Threading;
using System.IO;
namespace NeuroSky
{
    enum Command { Sync = 0xAA, ExCode = 0x55 };
    enum Parser { PoorSignal = 0x02, Attention = 0x04, Mediation = 0x05, Blink = 0x16, Raw = 0x80, Eeg = 0x83 };

    /// <summary>
    /// Signals from mindwave mobile
    /// </summary>
    public struct Signal
    {
        /// <summary>
        /// Quality (0 is best)
        /// </summary>
        public int Quality;
        /// <summary>
        /// delta wave power(0.5 - 2.75Hz) 
        /// </summary>
        public int Delta;
        /// <summary>
        /// theta wave power(3.5 -6.75Hz)
        /// </summary>
        public int Theta;
        /// <summary>
        /// low-alpha wave power(7.5 - 9.25Hz)
        /// </summary>
        public int LowAlpha;
        /// <summary>
        /// high-alpha wave power(10 - 11.75Hz)
        /// </summary>
        public int HighAlpha;
        /// <summary>
        /// low-beta wave power(13 - 16.75Hz)
        /// </summary>
        public int LowBeta;
        /// <summary>
        /// high-beta wave power(18 - 29.75Hz)
        /// </summary>
        public int HighBeta;
        /// <summary>
        /// low-gamma wave power(31 - 39.75Hz)
        /// </summary>
        public int LowGamma;
        /// <summary>
        /// mid-gamma wave power(41 - 49.75Hz)
        /// </summary>
        public int MidGamma;
        /// <summary>
        /// Attention level (user's level of mental "focus" or "attention")
        /// (
        ///        0: unable calcurate,
        ///     1-20: strongly lowered,
        ///    20-40: reduced,
        ///    40-60: neutral,
        ///    60-80: silghtly elevated,
        ///   80-100: elevated
        ///  )
        /// </summary>
        public int Attention;
        /// <summary>
        /// Meditation level (user's mental "calmness" or "relaxation")
        /// (
        ///        0: unable calcurate,
        ///     1-20: strongly lowered,
        ///    20-40: reduced,
        ///    40-60: neutral,
        ///    60-80: silghtly elevated,
        ///   80-100: elevated
        ///  )
        /// </summary>
        public int Meditation;
        /// <summary>
        /// Blink Strength (0: no blink, 1-255 when blink event occurs(no unit))
        /// </summary>
        public int Blink;
    }

    public class MindwaveEventArgs : EventArgs
    {
        public Signal Data; 
    }

    public class MindwaveRawDataEventArgs : EventArgs
    {
        public short[] RawData;
    }

    public class Mindwave : IDisposable
    {
        Thread receiveThread;
        public delegate void MindwaveEventHandler(object sender, MindwaveEventArgs e);
        public event MindwaveEventHandler Updated;
        public delegate void MindwaveRawDataEventHandler(object sender, MindwaveRawDataEventArgs e);
        public event MindwaveRawDataEventHandler RawDataUpdated;

        // Serial port
        SerialPort serialPort;
        string portName = "";

        // Raw data handling
        int rawDataPointer = 0;
        short[] rawData;
        byte[] payload = new byte[256];
        public int RawDataSize { get; private set; }

        // Signal handling
        Signal signal = new Signal();

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="portName">Port Name(COM1, COM2, ...)</param>
        public Mindwave(string portName)
        {
            this.disposed = false;
            this.portName = portName;

            this.RawDataSize = 512;
            rawDataPointer = 0;
            rawData = new short[this.RawDataSize];
        }
        /// <summary>
        /// Connect to mindwave mobile
        /// </summary>
        public void Open()
        {
            try
            {
                this.serialPort = new SerialPort();
                this.serialPort.PortName = portName;
                this.serialPort.BaudRate = 57600;
                this.serialPort.Parity = System.IO.Ports.Parity.None;
                this.serialPort.DataBits = 8;
                this.serialPort.StopBits = System.IO.Ports.StopBits.One;
                this.serialPort.Handshake = System.IO.Ports.Handshake.None;
                this.serialPort.Encoding = Encoding.UTF8;
                this.serialPort.ReadBufferSize = 4096;
                // this.serialPort.NewLine = "\r";
                serialPort.Open();
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Start capturing
        /// </summary>
        public void Start()
        {
            receiveThread = new Thread(new ThreadStart(ContinuousRead));
            receiveThread.Start();
        }

        /// <summary>
        /// Stop capturing
        /// </summary>
        public void Stop()
        {
            if (receiveThread != null)
            {
                receiveThread.Abort();
                receiveThread.Join();
            }
        }

        /// <summary>
        /// Main loop
        /// </summary>
        private void ContinuousRead()
        {
            while (true)
            {
                // Synchronize on SYNC bytes
                Command c1 =  (Command)serialPort.ReadByte();
                if (c1 != Command.Sync)
                {
                    continue;
                }
                Command c2 =  (Command)serialPort.ReadByte();
                if (c2 != Command.Sync)
                {
                    continue;
                }

                int pLength = 0;
                while (true)
                {
                    pLength = serialPort.ReadByte();
                    if (pLength != 170)
                    {
                        break;
                    }
                }
                if (pLength > 169) continue;

                // read payload
                while (serialPort.BytesToRead < pLength)
                {
                    Thread.Sleep(1);
                }
                serialPort.Read(payload, 0, pLength);

                // calcurate checksum
                int checksum = 0;
                for (int i = 0; i < pLength; i++)
                {
                    checksum += payload[i];
                }
                checksum ^= 0xff;
                checksum &= 0xff;

                int c = serialPort.ReadByte();
                if (c != checksum)
                {
                    continue;
                }

                // Parse Payload
                bool completed = ParsePayload(payload, pLength);
                
                if (completed)
                {
                    MindwaveEventArgs e = new MindwaveEventArgs();
                    e.Data = signal;
                    OnUpdated(e);
                    signal = new Signal();
                }
            }
        }

        private bool ParsePayload(byte[] payload, int pLength)
        {
            int bytesParsed = 0;
            int code;
            int extendedCodeLevel;
            while (bytesParsed < pLength)
            {
                extendedCodeLevel = 0;
                while ((Command)payload[bytesParsed] == Command.ExCode)
                {
                    extendedCodeLevel++;
                    bytesParsed++;
                }
                code = payload[bytesParsed++];

                switch ((Parser)code)
                {
                    case Parser.PoorSignal:
                        signal.Quality = payload[bytesParsed++];
                        break;
                    case Parser.Attention:
                        signal.Attention = payload[bytesParsed++];
                        break;
                    case Parser.Mediation:
                        signal.Meditation = payload[bytesParsed++];
                        break;
                    case Parser.Blink:
                        signal.Blink = payload[bytesParsed++];
                        break;
                    case Parser.Eeg:
                        int p = bytesParsed + 1;
                        bytesParsed += payload[bytesParsed++]; // skip value must be 24
                        signal.Delta = (payload[p] << 16) | (payload[p+1] << 8) | payload[p+2];
                        signal.Theta = (payload[p+3] << 16) | (payload[p+4] << 8) | payload[p+5];
                        signal.LowAlpha = (payload[p+6] << 16) | (payload[p+7] << 8) | payload[p+8];
                        signal.HighAlpha = (payload[p+9] << 16) | (payload[p+10] << 8) | payload[p+11];
                        signal.LowBeta = (payload[p+12] << 16) | (payload[p+13] << 8) | payload[p+14];
                        signal.HighBeta = (payload[p+15] << 16) | (payload[p+16] << 8) | payload[p+17];
                        signal.LowGamma = (payload[p+18] << 16) | (payload[p+19] << 8) | payload[p+20];
                        signal.MidGamma = (payload[p+21] << 16) | (payload[p+22] << 8) | payload[p+23];
                        break;
                    case Parser.Raw:
                        int q = bytesParsed + 1;
                        bytesParsed += payload[bytesParsed++]; // skip value must be 2
                        short v = (short)((payload[q] << 8) | payload[q+1]);
                        SetRawData(v);
                        break;
                    default:
                        break;
                }

            }
            return (pLength == 32);
        }

        private void SetRawData(short data)
        {
            rawData[rawDataPointer++] = data;
            if (rawDataPointer == this.RawDataSize)
            {
                short[] raw = new short[this.RawDataSize];
                Array.Copy(rawData, raw, raw.Length);
                rawDataPointer = 0;

                MindwaveRawDataEventArgs e = new MindwaveRawDataEventArgs();
                e.RawData = raw;
                OnRawDataUpdated(e);
            }
        }

        protected virtual void OnUpdated(MindwaveEventArgs e)
        {
            if (Updated != null)
            {
                Updated(this, e);
            }
        }

        protected virtual void OnRawDataUpdated(MindwaveRawDataEventArgs e)
        {
            if (RawDataUpdated != null)
            {
                RawDataUpdated(this, e);
            }
        }

        public void Close()
        {
            this.serialPort.Close();
            this.serialPort.Dispose();
        }

        private bool disposed;

        ~Mindwave()
        {
            this.Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
            if (disposing)
            {
                this.Close();
                // release managed resources
            }
            // release unmanaged resources
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            this.Dispose(true);
        }
    }
}
