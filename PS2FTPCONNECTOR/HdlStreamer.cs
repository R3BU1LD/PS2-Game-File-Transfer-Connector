using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PS2FTPCONNECTOR
{
    public class HdlStreamer
    {
        // Events om live status, snelheid en logs terug te sturen naar het hoofdscherm
        public event Action<string> OnLog;
        public event Action<double, double, string> OnProgress;

        // 1. Controleer of de PS2 server luistert op poort 45061
        public bool CheckServerStatus(string ip)
        {
            try
            {
                using (TcpClient tcpClient = new TcpClient())
                {
                    var result = tcpClient.BeginConnect(ip, 45061, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(2000);
                    if (success && tcpClient.Connected)
                    {
                        tcpClient.EndConnect(result);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"NET_ERR: Handshake gefaald. Details: {ex.Message}");
            }
            return false;
        }

        // 2. De binaire handshake + ISO stream naar poort 45062
        public bool UploadIso(string ip, Ps2GameJob job)
        {
            try
            {
                FileInfo isoFile = new FileInfo(job.IsoPath);
                long totalBytes = isoFile.Length;
                uint totalSectors = (uint)(totalBytes / 2048);
                double totalMegabytes = totalBytes / (1024.0 * 1024.0);

                OnLog?.Invoke($">> PACKING BINARY HEADERS FOR: {job.Title}");

                // STAP A: Commando-poort 45061 (Aanmelding)
                using (TcpClient cmdClient = new TcpClient())
                {
                    cmdClient.Connect(ip, 45061);
                    using (NetworkStream cmdStream = cmdClient.GetStream())
                    using (BinaryWriter writer = new BinaryWriter(cmdStream))
                    using (BinaryReader reader = new BinaryReader(cmdStream))
                    {
                        writer.Write((uint)1);       // Command: INSTALL
                        writer.Write((uint)117);     // PayloadLength: 117 bytes
                        writer.Write((int)0);        // Result: 0

                        byte[] titleBytes = new byte[81];
                        Encoding.ASCII.GetBytes(job.Title).CopyTo(titleBytes, 0);
                        writer.Write(titleBytes);

                        string cleanDiscId = job.GameId.Replace("_", "-").Replace(".", "");
                        byte[] idBytes = new byte[11];
                        Encoding.ASCII.GetBytes(cleanDiscId).CopyTo(idBytes, 0);
                        writer.Write(idBytes);

                        string startupFname = job.GameId + ";1";
                        byte[] startupBytes = new byte[14];
                        Encoding.ASCII.GetBytes(startupFname).CopyTo(startupBytes, 0);
                        writer.Write(startupBytes);

                        writer.Write((byte)0x14);
                        writer.Write(totalSectors);
                        writer.Write((uint)0);
                        writer.Write((byte)0);
                        writer.Write((byte)0);

                        OnLog?.Invoke(">> HEADERS TRANSMITTED. AWAITING ACCESS GRANTED...");

                        uint respCmd = reader.ReadUInt32();
                        uint respLen = reader.ReadUInt32();
                        int respResult = reader.ReadInt32();

                        if (respResult != 0)
                        {
                            OnLog?.Invoke($"[HANDSHAKE_REFUSED] Error Code: {respResult}");
                            return false;
                        }

                        OnLog?.Invoke(">> ACCESS GRANTED! Partition ready on PS2.");
                    }
                }

                // STAP B: Data-poort 45062 (ISO overdracht)
                using (TcpClient dataClient = new TcpClient())
                {
                    dataClient.Connect(ip, 45062);
                    OnLog?.Invoke(">> DATA_LINK ESTABLISHED: Streaming clusters to 45062...");

                    using (NetworkStream netStream = dataClient.GetStream())
                    using (FileStream fileStream = new FileStream(job.IsoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        byte[] buffer = new byte[1024 * 64];
                        int bytesRead;
                        long totalBytesSent = 0;
                        Stopwatch transferTimer = Stopwatch.StartNew();

                        while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            netStream.Write(buffer, 0, bytesRead);
                            totalBytesSent += bytesRead;

                            double percentage = (double)totalBytesSent / totalBytes * 100.0;
                            double sec = transferTimer.Elapsed.TotalSeconds;
                            double speed = 0;
                            string eta = "CALCULATING...";

                            if (percentage > 0 && sec > 0.5)
                            {
                                double mbXfer = totalBytesSent / (1024.0 * 1024.0);
                                speed = mbXfer / sec;
                                TimeSpan rem = TimeSpan.FromSeconds((totalMegabytes - mbXfer) / speed);
                                eta = $"{rem.Hours:D2}:{rem.Minutes:D2}:{rem.Seconds:D2}";
                            }

                            OnProgress?.Invoke(percentage, speed, eta);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[STREAM_EXCEPTION] Critical network error: {ex.Message}");
                return false;
            }
        }

        // 3. Game ID uit de ISO vissen
        public string ExtractGameId(string isoPath)
        {
            try
            {
                using (FileStream fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[1024 * 1024 * 4];
                    string text = Encoding.ASCII.GetString(buffer, 0, fs.Read(buffer, 0, buffer.Length));
                    var match = Regex.Match(text, @"(SLES|SLUS|SCUS|SCDH|SLPM|SLPS|SCPM)_[0-9]{3}\.[0-9]{2}");
                    if (match.Success) return match.Value;
                }
            }
            catch { }
            return null;
        }
    }
}