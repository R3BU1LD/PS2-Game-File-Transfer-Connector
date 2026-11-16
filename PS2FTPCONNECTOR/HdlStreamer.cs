using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PS2FTPCONNECTOR
{
    public class HdlStreamer
    {
        public event Action<string>? OnLog;
        public event Action<double, double, string>? OnProgress;

        private TcpClient? _cmdClient;
        private NetworkStream? _cmdStream;

        // Officiële Commando's uit HDLGManClient.h
        private const uint CMD_SHUTDOWN = 0xFF;
        private const uint CMD_GET_VERSION = 0x01;
        private const uint CMD_SEND_VERSION = 0x02;
        private const uint CMD_PREP_GAME_INST = 0x10;
        private const uint CMD_INIT_GAME_READ = 0x12;
        private const uint CMD_CLOSE_GAME = 0x14;
        private const uint CMD_LOAD_GAME_LIST = 0x15;
        private const uint CMD_READ_GAME_LIST = 0x16;
        private const uint CMD_UPD_GAME_ENTRY = 0x19;
        private const uint CMD_INIT_OSD_RESOURCES = 0x20;
        private const uint CMD_WRITE_OSD_RESOURCES = 0x23;
        private const uint CMD_DEL_GAME_ENTRY = 0x1A;
        private const uint CMD_GET_GAME_PART_NAME = 0x1B;
        
        private const int PROTOCOL_VERSION = 0x0C;
        
        private const uint CMD_GET_FREE_SPACE = 0x1C;
        
        
        
        
        
       

        public bool IsConnected => _cmdClient != null && _cmdClient.Connected;

        public bool ConnectAndHandshake(string ip)
        {
            try
            {
                
                
                Disconnect();
                _cmdClient = new TcpClient();
                var res = _cmdClient.BeginConnect(ip, 45061, null, null);
                if (!res.AsyncWaitHandle.WaitOne(2500) || !_cmdClient.Connected)
                {
                    OnLog?.Invoke("[TIMEOUT] Unable to reach KPN port 45061.");
                    return false;
                }

                _cmdClient.EndConnect(res);
                _cmdStream = _cmdClient.GetStream();
                _cmdStream.ReadTimeout = 15000;
                _cmdStream.WriteTimeout = 15000;

                // 1. Request version (12-byte packet)
                SendPacket(CMD_GET_VERSION, null);
                var (hdrRes, payload) = GetResponse(4);
                int serverVer = (payload != null && payload.Length >= 4) ? BitConverter.ToInt32(payload, 0) : hdrRes;

                OnLog?.Invoke($">> SERVER VERSION: 0x{serverVer:X2}");
                if (serverVer != PROTOCOL_VERSION)
                {
                    OnLog?.Invoke("[ERROR] Invalid server version (verwacht 0x0C).");
                    return false;
                }

                // 2. Send back client version
                SendPacket(CMD_SEND_VERSION, BitConverter.GetBytes(PROTOCOL_VERSION));
                var (ackRes, _) = GetResponse(0);
                return ackRes == 0;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[NET_ERR] {ex.Message}");
                Disconnect();
                return false;
            }
        }

        public List<Ps2GameJob> LoadInstalledGames()
        {
            var list = new List<Ps2GameJob>();
            if (!IsConnected) return list;

            try
            {
                SendPacket(CMD_LOAD_GAME_LIST, null);
                var (count, _) = GetResponse(0);
                OnLog?.Invoke($">> PS2 HDD SCAN: {count} Games Found !");

                if (count > 0)
                {
                    SendPacket(CMD_READ_GAME_LIST, null);
                    var (readRes, _) = GetResponse(0);
                    if (readRes >= 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            byte[] entry = ReadExactBytes(213);

                            string partName = ExtractCleanString(entry, 0, 33, Encoding.ASCII);
                            string title = ExtractCleanString(entry, 33, 161, Encoding.UTF8);
                            string discId = ExtractCleanString(entry, 194, 11, Encoding.ASCII);
                            byte compatFlags = entry[205];
                            byte trType = entry[206];
                            byte trMode = entry[207];
                            byte discType = entry[208];
                            uint sectors = BitConverter.ToUInt32(entry, 209);

                            list.Add(new Ps2GameJob
                            {
                                PartName = partName,
                                GameId = discId,
                                Title = title,
                                CompatFlags = compatFlags,
                                TrType = trType,
                                TrMode = trMode,
                                DiscType = discType,
                                Sectors = sectors,
                                Status = "INSTALLED"
                            });

                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[LIST_ERR] {ex.Message}");
            }
            return list;
        }

        
        public string GetFreeSpaceFormatted()
        {
            if (!IsConnected) return "UNKNOWN";
            try
            {
                SendPacket(CMD_GET_FREE_SPACE, null);
                var (res, payload) = GetResponse(4);
                if (res >= 0 && payload != null && payload.Length >= 4)
                {
                    uint freeSectors512 = BitConverter.ToUInt32(payload, 0);
                    double freeKb = freeSectors512 / 2.0;

                    if (freeKb >= 1024 * 1024)
                        return $"{freeKb / (1024.0 * 1024.0):F2} GB";
                    if (freeKb >= 1024)
                        return $"{freeKb / 1024.0:F1} MB";
                    return $"{freeKb:F0} KB";
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[SPACE_ERR] {ex.Message}");
            }
            return "N/A";
        }

        public bool DeleteGame(string partName)
        {
            if (!IsConnected || string.IsNullOrEmpty(partName)) return false;
            try
            {
                byte[] payload = Encoding.ASCII.GetBytes(partName + "\0");
                SendPacket(CMD_DEL_GAME_ENTRY, payload);
                var (res, _) = GetResponse(0);
                return res >= 0;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[DEL_ERR] {ex.Message}");
                return false;
            }
        }

        public bool UpdateGameSettings(Ps2GameJob job)
        {
            if (!IsConnected || string.IsNullOrEmpty(job.PartName)) return false;
            try
            {
                byte[] transit = Array.Empty<byte>();
                Array.Resize(ref transit, 213);

                // 1. PartName (33 bytes)
                byte[] partBytes = Encoding.ASCII.GetBytes(job.PartName);
                Array.Copy(partBytes, 0, transit, 0, Math.Min(partBytes.Length, 32));

                // 2. GameTitle (161 bytes UTF-8)
                byte[] titleBytes = Encoding.UTF8.GetBytes(job.Title);
                Array.Copy(titleBytes, 0, transit, 33, Math.Min(titleBytes.Length, 160));

                // 3. DiscID (11 bytes)
                byte[] idBytes = Encoding.ASCII.GetBytes(job.GameId);
                Array.Copy(idBytes, 0, transit, 194, Math.Min(idBytes.Length, 10));

                // 4. Modes, Transfer Rate & DiscType (Bytes 205 t/m 208)
                byte[] meta = new byte[] { job.CompatFlags, job.TrType, job.TrMode, job.DiscType };
                Array.Copy(meta, 0, transit, 205, 4);

                // 5. Sectors (Bytes 209 t/m 212)
                BitConverter.GetBytes(job.Sectors).CopyTo(transit, 209);

                SendPacket(CMD_UPD_GAME_ENTRY, transit);
                var (res, _) = GetResponse(0);
                return res >= 0;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[UPD_ERR] {ex.Message}");
                return false;
            }
        }

        // Helper method to cleanly extract C-strings (up to the first \0) from a byte array
        private string ExtractCleanString(byte[] buffer, int offset, int maxLength, Encoding encoding)
        {
            int length = 0;
            while (length < maxLength && buffer[offset + length] != 0)
            {
                length++;
            }
            return encoding.GetString(buffer, offset, length).Trim();
        }

        public bool UploadIso(string ip, Ps2GameJob job)
        {
            if (!IsConnected && !ConnectAndHandshake(ip)) return false;

            TcpListener? dataListener = null;
            try
            {
                FileInfo isoFile = new FileInfo(job.IsoPath);
                long totalBytes = isoFile.Length;
                uint totalSectors = (uint)(totalBytes / 2048);
                double totalMegabytes = totalBytes / (1024.0 * 1024.0);

                // 1. Open port 45062 on the PC for PS2 callback
                dataListener = new TcpListener(IPAddress.Any, 45062);
                dataListener.Start();
                OnLog?.Invoke(">> PORT 45062 LISTENING FOR PS2 CALLBACK...");

                // 2. Build the exact 197-byte HDLGameInfo struct
                byte[] gameInfo = new byte[198];
                byte[] titleBytes = Encoding.UTF8.GetBytes(job.Title);
                Array.Copy(titleBytes, 0, gameInfo, 0, Math.Min(titleBytes.Length, 160));

                string cleanDiscId = job.GameId.Replace("_", "-").Replace(".", "");
                byte[] idBytes = Encoding.ASCII.GetBytes(cleanDiscId);
                Array.Copy(idBytes, 0, gameInfo, 161, Math.Min(idBytes.Length, 10));

                string startup = job.GameId + ";1";
                byte[] startupBytes = Encoding.ASCII.GetBytes(startup);
                Array.Copy(startupBytes, 0, gameInfo, 172, Math.Min(startupBytes.Length, 13));

                gameInfo[186] = 0x14; // SCECdPS2DVD
                BitConverter.GetBytes(totalSectors).CopyTo(gameInfo, 187);
                BitConverter.GetBytes((uint)0).CopyTo(gameInfo, 191);
                gameInfo[195] = 0;    // CompatibilityFlags
                gameInfo[196] = 0x40; // ATA_XFER_MODE_UDMA (4)
                // Note: TRMode (197) corresponds to a total of 198 bytes; let’s check that to be sure:
                Array.Resize(ref gameInfo, 198);
                gameInfo[197] = 4;

                SendPacket(CMD_PREP_GAME_INST, gameInfo);
                var (prepRes, _) = GetResponse(0);
                if (prepRes < 0)
                {
                    OnLog?.Invoke($"[PREP_REFUSED] PS2 Code: {prepRes} (Bestaat game al of HDD vol?)");
                    return false;
                }

                OnLog?.Invoke(">> PARTITION READY! Waiting for PS2 connection on 45062...");

                var acceptTask = dataListener.AcceptTcpClientAsync();
                if (!acceptTask.Wait(20000))
                {
                    OnLog?.Invoke("[TIMEOUT] PS2 failed to reconnect on 45062. Check Windows Firewall!");
                    return false;
                }

                // 3. Send the ISO data (Exact 1MB DMA blocks, similar to sp193’s HDLGManClient, for 7.4+ MB/s)
                using (TcpClient ps2Data = acceptTask.Result)
                {
                    Socket sock = ps2Data.Client;
                    sock.NoDelay = true;
                    sock.SendBufferSize = 1024 * 1024; // 1 MB TCP socket buffer

                    using (FileStream fs = new FileStream(job.IsoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan))
                    {
                        OnLog?.Invoke(">> DATA STREAM ACTIVE! Direct 1MB DMA-aligned socket transfer...");
                        byte[] buffer = new byte[1024 * 1024]; // Exactly 1 MB (512 PS2 sectors)
                        int bytesRead;
                        long totalSent = 0;
                        Stopwatch timer = Stopwatch.StartNew();
                        long lastUiUpdateMs = 0;

                        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            int sentInChunk = 0;
                            while (sentInChunk < bytesRead)
                            {
                                sentInChunk += sock.Send(buffer, sentInChunk, bytesRead - sentInChunk, SocketFlags.None);
                            }
                            totalSent += bytesRead;

                            long nowMs = timer.ElapsedMilliseconds;
                            if (nowMs - lastUiUpdateMs >= 250 || totalSent >= totalBytes)
                            {
                                lastUiUpdateMs = nowMs;
                                double pct = (double)totalSent / totalBytes * 100.0;
                                double sec = nowMs / 1000.0;
                                double speed = 0;
                                string eta = "CALCULATING...";

                                if (pct > 0 && sec > 0.5)
                                {
                                    double mbXfer = totalSent / (1024.0 * 1024.0);
                                    speed = mbXfer / sec;
                                    TimeSpan rem = TimeSpan.FromSeconds((totalMegabytes - mbXfer) / speed);
                                    eta = $"{rem.Hours:D2}:{rem.Minutes:D2}:{rem.Seconds:D2}";
                                }
                                OnProgress?.Invoke(pct, speed, eta);
                            }
                        }
                        sock.Shutdown(SocketShutdown.Send);
                    }
                }

                // 4. Close the game on the PS2
                OnLog?.Invoke(">> CLOSING GAME PARTITION ON PS2 (0x14)...");
                SendPacket(CMD_CLOSE_GAME, null);
                var (closeRes, _) = GetResponse(0);
                OnLog?.Invoke($">> CLOSE STATUS: {closeRes}");

                // 5. Install mandatory PS2 OSD icons (so that the game is visible)
                InstallDefaultOsdResources(cleanDiscId, job.Title);

                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[STREAM_EXCEPTION] {ex.Message}");
                return false;
            }
            finally
            {
                dataListener?.Stop();
            }
        }
        public bool DownloadIso(string ip, Ps2GameJob job, string savePath)
        {
            if (!IsConnected && !ConnectAndHandshake(ip)) return false;

            TcpListener? dataListener = null;
            try
            {
                long totalBytes = (long)job.Sectors * 2048;
                double totalMegabytes = totalBytes / (1024.0 * 1024.0);

                dataListener = new TcpListener(IPAddress.Any, 45062);
                dataListener.Start();
                OnLog?.Invoke(">> PORT 45062 LISTENING FOR PS2 BACKUP STREAM...");

                // Bouw IOInitReq struct (4 bytes sectors + 4 bytes offset + 33 bytes partition = 41 bytes)
                byte[] req = new byte[41];
                BitConverter.GetBytes(job.Sectors).CopyTo(req, 0);
                BitConverter.GetBytes((uint)0).CopyTo(req, 4);
                byte[] partBytes = Encoding.ASCII.GetBytes(job.PartName);
                Array.Copy(partBytes, 0, req, 8, Math.Min(partBytes.Length, 32));

                SendPacket(CMD_INIT_GAME_READ, req);
                var (initRes, _) = GetResponse(0);
                if (initRes < 0)
                {
                    OnLog?.Invoke($"[READ_REFUSED] PS2 Code: {initRes}");
                    return false;
                }

                OnLog?.Invoke(">> READ READY! Waiting for PS2 data connection....");
                var acceptTask = dataListener.AcceptTcpClientAsync();
                if (!acceptTask.Wait(20000))
                {
                    OnLog?.Invoke("[TIMEOUT] PS2 did not reconnect on 45062.");
                    return false;
                }

                using (TcpClient ps2Data = acceptTask.Result)
                using (NetworkStream dataStream = ps2Data.GetStream())
                using (FileStream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    OnLog?.Invoke($">> BACKUP ACTIVE: Save to{Path.GetFileName(savePath)}...");
                    byte[] buffer = new byte[1024 * 256];
                    long totalReceived = 0;
                    Stopwatch timer = Stopwatch.StartNew();

                    while (totalReceived < totalBytes)
                    {
                        int toRead = (int)Math.Min(buffer.Length, totalBytes - totalReceived);
                        int read = dataStream.Read(buffer, 0, toRead);
                        if (read <= 0) break;

                        fs.Write(buffer, 0, read);
                        totalReceived += read;

                        double pct = (double)totalReceived / totalBytes * 100.0;
                        double sec = timer.Elapsed.TotalSeconds;
                        double speed = 0;
                        string eta = "CALCULATING...";

                        if (pct > 0 && sec > 0.5)
                        {
                            double mbXfer = totalReceived / (1024.0 * 1024.0);
                            speed = mbXfer / sec;
                            TimeSpan rem = TimeSpan.FromSeconds((totalMegabytes - mbXfer) / speed);
                            eta = $"{rem.Hours:D2}:{rem.Minutes:D2}:{rem.Seconds:D2}";
                        }
                        OnProgress?.Invoke(pct, speed, eta);
                    }
                    fs.Flush();
                }

                OnLog?.Invoke(">> CLOSING READ SESSION ON PS2 (0x14)...");
                SendPacket(CMD_CLOSE_GAME, null);
                GetResponse(0);

                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[BACKUP_EXCEPTION] {ex.Message}");
                return false;
            }
            finally
            {
                dataListener?.Stop();
            }
        }
        private void InstallDefaultOsdResources(string cleanDiscId, string title)
        {
            try
            {
                byte[] idPayload = Encoding.ASCII.GetBytes(cleanDiscId + "\0");
                SendPacket(CMD_GET_GAME_PART_NAME, idPayload);
                var (partRes, partData) = GetResponse(33);
                if (partRes < 0 || partData == null) return;

                // Construct OSDResourceInitReq (4 + 65 + 65 + 11 + 33 = 178 bytes)
                byte[] osdReq = new byte[178];
                BitConverter.GetBytes(0).CopyTo(osdReq, 0); // UseSaveData = 0

                string line1 = title.Length > 16 ? title.Substring(0, 16) : title;
                byte[] l1Bytes = Encoding.UTF8.GetBytes(line1);
                Array.Copy(l1Bytes, 0, osdReq, 4, Math.Min(l1Bytes.Length, 64));

                Array.Copy(idPayload, 0, osdReq, 134, Math.Min(idPayload.Length, 10));
                Array.Copy(partData, 0, osdReq, 145, Math.Min(partData.Length, 32));

                SendPacket(CMD_INIT_OSD_RESOURCES, osdReq);
                if (GetResponse(0).Item1 >= 0)
                {
                    SendPacket(CMD_WRITE_OSD_RESOURCES, null);
                    GetResponse(0);
                    OnLog?.Invoke(">> OSD BROWSER ICONS SUCCESSFULLY WRITTEN!");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[OSD_WARN] Could not write icons: {ex.Message}");
            }
        }

        private void SendPacket(uint command, byte[]? payload)
        {
            if (_cmdStream is null)
                throw new InvalidOperationException("PS2 command stream is not initialized.");

            uint len = (uint)(payload?.Length ?? 0);
            byte[] packet = new byte[12 + len];
            BitConverter.GetBytes(command).CopyTo(packet, 0);
            BitConverter.GetBytes(len).CopyTo(packet, 4);
            BitConverter.GetBytes(0).CopyTo(packet, 8);
            if (len > 0 && payload is not null) Array.Copy(payload, 0, packet, 12, len);

            _cmdStream.Write(packet, 0, packet.Length);
            _cmdStream.Flush();
        }

        private (int, byte[]) GetResponse(int maxPayloadBytes)
        {
            byte[] hdr = ReadExactBytes(12);
            uint payloadLen = BitConverter.ToUInt32(hdr, 4);
            int result = BitConverter.ToInt32(hdr, 8);

            if (payloadLen > 0)
            {
                byte[] payload = ReadExactBytes((int)payloadLen);
                return (result, payload);
            }
            return (result, Array.Empty<byte>());
        }

        private byte[] ReadExactBytes(int count)
        {
            if (_cmdStream is null)
                throw new InvalidOperationException("PS2 command stream is not initialized.");

            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _cmdStream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new Exception("Connection terminated by PS2.");
                offset += read;
            }
            return buffer;
        }
        public bool PowerOffPs2()
        {
            if (!IsConnected) return false;
            try
            {
                SendPacket(CMD_SHUTDOWN, null);
                Disconnect();
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[PWR_ERR] {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                if (_cmdClient != null && _cmdClient.Connected)
                {
                    // Send a clean TCP shutdown signal (SD_BOTH) so that the PS2 releases the port immediately
                    _cmdClient.Client.Shutdown(SocketShutdown.Both);
                }
                _cmdStream?.Close();
                _cmdClient?.Close();
            }
            catch { }
            _cmdStream = null;
            _cmdClient = null;
        }


        public string? ExtractGameId(string isoPath)
        {
            try
            {
                using (FileStream fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[1024 * 1024 * 4];
                    int bytesRead = fs.Read(buffer, 0, buffer.Length);
                    string text = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    var match = Regex.Match(text, @"(SLES|SLUS|SCUS|SCDH|SLPM|SLPS|SCPM)_[0-9]{3}\.[0-9]{2}");
                    if (match.Success) return match.Value;
                }
            }
            catch { }
            return null;
        }
    }
}

