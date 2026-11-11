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
        public event Action<string> OnLog;
        public event Action<double, double, string> OnProgress;

        private TcpClient _cmdClient;
        private NetworkStream _cmdStream;

        // Officiële Commando's uit HDLGManClient.h
        private const uint CMD_GET_VERSION = 0x01;
        private const uint CMD_SEND_VERSION = 0x02;
        private const uint CMD_PREP_GAME_INST = 0x10;
        private const uint CMD_CLOSE_GAME = 0x14;
        private const uint CMD_LOAD_GAME_LIST = 0x15;
        private const uint CMD_READ_GAME_LIST = 0x16;
        private const uint CMD_GET_GAME_PART_NAME = 0x1B;
        private const uint CMD_GET_FREE_SPACE = 0x1C;
        private const uint CMD_INIT_OSD_RESOURCES = 0x20;
        private const uint CMD_WRITE_OSD_RESOURCES = 0x23;
        private const int PROTOCOL_VERSION = 0x0C;

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
                    OnLog?.Invoke("[TIMEOUT] Kon poort 45061 niet bereiken.");
                    return false;
                }

                _cmdClient.EndConnect(res);
                _cmdStream = _cmdClient.GetStream();
                _cmdStream.ReadTimeout = 15000;
                _cmdStream.WriteTimeout = 15000;

                // 1. Vraag versie op (12-byte pakket)
                SendPacket(CMD_GET_VERSION, null);
                var (hdrRes, payload) = GetResponse(4);
                int serverVer = (payload != null && payload.Length >= 4) ? BitConverter.ToInt32(payload, 0) : hdrRes;

                OnLog?.Invoke($">> SERVER VERSION: 0x{serverVer:X2}");
                if (serverVer != PROTOCOL_VERSION)
                {
                    OnLog?.Invoke("[ERROR] Ongeldige serverversie (verwacht 0x0C).");
                    return false;
                }

                // 2. Stuur client versie terug
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
                OnLog?.Invoke($">> PS2 HDD SCAN: {count} spellen gevonden.");

                if (count > 0)
                {
                    SendPacket(CMD_READ_GAME_LIST, null);
                    var (readRes, _) = GetResponse(0);
                    if (readRes >= 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            byte[] entry = ReadExactBytes(213);

                            // Lees de strings uit en kap ze keihard af bij de ALLEREERSTE null-byte (\0)
                            string partName = ExtractCleanString(entry, 0, 33, Encoding.ASCII);
                            string title = ExtractCleanString(entry, 33, 161, Encoding.UTF8);
                            string discId = ExtractCleanString(entry, 194, 11, Encoding.ASCII);

                            list.Add(new Ps2GameJob { GameId = discId, Title = title, Status = "INSTALLED" });
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

        // Hulpmethode om C-strings (tot de eerste \0) schoon uit een byte-array te halen
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

            TcpListener dataListener = null;
            try
            {
                FileInfo isoFile = new FileInfo(job.IsoPath);
                long totalBytes = isoFile.Length;
                uint totalSectors = (uint)(totalBytes / 2048);
                double totalMegabytes = totalBytes / (1024.0 * 1024.0);

                // 1. Open poort 45062 op de PC voor PS2 callback
                dataListener = new TcpListener(IPAddress.Any, 45062);
                dataListener.Start();
                OnLog?.Invoke(">> PORT 45062 LISTENING FOR PS2 CALLBACK...");

                // 2. Bouw de exacte 197-byte HDLGameInfo struct
                byte[] gameInfo = new byte[197];
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
                // Let op: TRMode (197) hoort bij 198 bytes totaal, laten we die voor zekerheid checken:
                Array.Resize(ref gameInfo, 198);
                gameInfo[197] = 4;

                SendPacket(CMD_PREP_GAME_INST, gameInfo);
                var (prepRes, _) = GetResponse(0);
                if (prepRes < 0)
                {
                    OnLog?.Invoke($"[PREP_REFUSED] PS2 Code: {prepRes} (Bestaat game al of HDD vol?)");
                    return false;
                }

                OnLog?.Invoke(">> PARTITION READY! Wachten op PS2 verbinding op 45062...");

                var acceptTask = dataListener.AcceptTcpClientAsync();
                if (!acceptTask.Wait(20000))
                {
                    OnLog?.Invoke("[TIMEOUT] PS2 verbond niet terug op 45062. Controleer Windows Firewall!");
                    return false;
                }

                // 3. Stuur de ISO data
                using (TcpClient ps2Data = acceptTask.Result)
                using (NetworkStream dataStream = ps2Data.GetStream())
                using (FileStream fs = new FileStream(job.IsoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    OnLog?.Invoke(">> DATA STREAM ACTIVE! Verzenden van ISO clusters...");
                    byte[] buffer = new byte[1024 * 256]; // 256KB blokken
                    int bytesRead;
                    long totalSent = 0;
                    Stopwatch timer = Stopwatch.StartNew();

                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dataStream.Write(buffer, 0, bytesRead);
                        totalSent += bytesRead;

                        double pct = (double)totalSent / totalBytes * 100.0;
                        double sec = timer.Elapsed.TotalSeconds;
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
                    dataStream.Flush();
                    ps2Data.Client.Shutdown(SocketShutdown.Send);
                }

                // 4. Sluit de game op de PS2
                OnLog?.Invoke(">> CLOSING GAME PARTITION ON PS2 (0x14)...");
                SendPacket(CMD_CLOSE_GAME, null);
                var (closeRes, _) = GetResponse(0);
                OnLog?.Invoke($">> CLOSE STATUS: {closeRes}");

                // 5. Installeer verplichte PS2 OSD-iconen (zodat het spel zichtbaar is)
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

        private void InstallDefaultOsdResources(string cleanDiscId, string title)
        {
            try
            {
                byte[] idPayload = Encoding.ASCII.GetBytes(cleanDiscId + "\0");
                SendPacket(CMD_GET_GAME_PART_NAME, idPayload);
                var (partRes, partData) = GetResponse(33);
                if (partRes < 0 || partData == null) return;

                // Bouw OSDResourceInitReq (4 + 65 + 65 + 11 + 33 = 178 bytes)
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
                OnLog?.Invoke($"[OSD_WARN] Kon iconen niet schrijven: {ex.Message}");
            }
        }

        private void SendPacket(uint command, byte[] payload)
        {
            uint len = (uint)(payload?.Length ?? 0);
            byte[] packet = new byte[12 + len];
            BitConverter.GetBytes(command).CopyTo(packet, 0);
            BitConverter.GetBytes(len).CopyTo(packet, 4);
            BitConverter.GetBytes(0).CopyTo(packet, 8);
            if (len > 0) Array.Copy(payload, 0, packet, 12, len);

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
            return (result, null);
        }

        private byte[] ReadExactBytes(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _cmdStream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new Exception("Verbinding verbroken door PS2.");
                offset += read;
            }
            return buffer;
        }

        public void Disconnect()
        {
            try
            {
                if (_cmdClient != null && _cmdClient.Connected)
                {
                    // Stuur een netjes TCP-afsluitsignaal (SD_BOTH) zodat de PS2 direct de poort vrijgeeft
                    _cmdClient.Client.Shutdown(SocketShutdown.Both);
                }
                _cmdStream?.Close();
                _cmdClient?.Close();
            }
            catch { }
            _cmdStream = null;
            _cmdClient = null;
        }


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

