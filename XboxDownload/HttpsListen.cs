﻿using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace XboxDownload
{
    class HttpsListen
    {
        private readonly Form1 parentForm;
        private readonly X509Certificate2 certificate = null;
        Socket socket = null;

        public HttpsListen(Form1 parentForm)
        {
            this.parentForm = parentForm;
            this.certificate = new X509Certificate2(Properties.Resources.XboxDownload);
        }

        public void Listen()
        {
            IPEndPoint ipe = new IPEndPoint(Properties.Settings.Default.ListenIP == 0 ? IPAddress.Parse(Properties.Settings.Default.LocalIP) : IPAddress.Any, 443);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(ipe);
                socket.Listen(100);
            }
            catch (SocketException ex)
            {
                parentForm.Invoke(new Action(() =>
                {
                    parentForm.pictureBox1.Image = Properties.Resources.Xbox3;
                    MessageBox.Show(String.Format("启用HTTPS服务失败!\n错误信息: {0}", ex.Message), "启用HTTPS服务失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
                return;
            }

            X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            X509Certificate2 certificate = new X509Certificate2(Properties.Resources.Xbox下载助手);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            store.Close();

            while (Form1.bServiceFlag)
            {
                try
                {
                    Socket mySocket = socket.Accept();
                    ThreadPool.QueueUserWorkItem(TcpThread, mySocket);
                }
                catch { }
            }
        }

        private void TcpThread(object obj)
        {
            Socket mySocket = (Socket)obj;
            if (mySocket.Connected)
            {
                mySocket.SendTimeout = 30000;
                mySocket.ReceiveTimeout = 30000;
                using (SslStream ssl = new SslStream(new NetworkStream(mySocket), false))
                {
                    try
                    {
                        ssl.AuthenticateAsServer(this.certificate, false, SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls, false);
                        ssl.WriteTimeout = 30000;
                        ssl.ReadTimeout = 30000;
                        if (ssl.IsAuthenticated)
                        {
                            while (mySocket.Connected && mySocket.Poll(3000000, SelectMode.SelectRead))
                            {
                                Byte[] _receive = new Byte[4096];
                                int _num = ssl.Read(_receive, 0, _receive.Length);
                                string _buffer = Encoding.ASCII.GetString(_receive, 0, _num);
                                Match result = Regex.Match(_buffer, @"(?<method>GET|POST|OPTIONS) (?<path>[^\s]+)");
                                if (!result.Success)
                                {
                                    mySocket.Close();
                                    continue;
                                }
                                string _method = result.Groups["method"].Value;
                                string _filePath = Regex.Replace(result.Groups["path"].Value.Trim(), @"^https?://[^/]+", "");
                                result = Regex.Match(_buffer, @"Host:(.+)");
                                if (!result.Success)
                                {
                                    mySocket.Close();
                                    continue;
                                }
                                string _domainName = result.Groups[1].Value.Trim();
                                string _tmpPath = Regex.Replace(_filePath, @"\?.+$", ""), _localPath = null;
                                if (Properties.Settings.Default.LocalUpload)
                                {
                                    if (File.Exists(Properties.Settings.Default.LocalPath + _tmpPath))
                                        _localPath = Properties.Settings.Default.LocalPath + _tmpPath;
                                    else if (File.Exists(Properties.Settings.Default.LocalPath + "\\" + Path.GetFileName(_tmpPath)))
                                        _localPath = Properties.Settings.Default.LocalPath + "\\" + Path.GetFileName(_tmpPath);
                                }
                                string _extension = Path.GetExtension(_tmpPath).ToLowerInvariant();
                                if (Properties.Settings.Default.LocalUpload && !string.IsNullOrEmpty(_localPath))
                                {
                                    if (Form1.bRecordLog) parentForm.SaveLog("本地上传", _localPath, ((IPEndPoint)mySocket.RemoteEndPoint).Address.ToString());
                                    using (FileStream fs = new FileStream(_localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                    {
                                        using (BinaryReader br = new BinaryReader(fs))
                                        {
                                            string _contentRange = null, _status = "200 OK";
                                            long _fileLength = br.BaseStream.Length, _startPosition = 0;
                                            long _endPosition = _fileLength;
                                            result = Regex.Match(_buffer, @"Range: bytes=(?<StartPosition>\d+)(-(?<EndPosition>\d+))?");
                                            if (result.Success)
                                            {
                                                _startPosition = long.Parse(result.Groups["StartPosition"].Value);
                                                if (_startPosition > br.BaseStream.Length) _startPosition = 0;
                                                if (!string.IsNullOrEmpty(result.Groups["EndPosition"].Value))
                                                    _endPosition = long.Parse(result.Groups["EndPosition"].Value) + 1;
                                                _contentRange = "bytes " + _startPosition + "-" + (_endPosition - 1) + "/" + _fileLength;
                                                _status = "206 Partial Content";
                                            }

                                            StringBuilder sb = new StringBuilder();
                                            sb.Append("HTTP/1.1 " + _status + "\r\n");
                                            sb.Append("Content-Type: " + System.Web.MimeMapping.GetMimeMapping(_tmpPath) + "\r\n");
                                            sb.Append("Content-Length: " + (_endPosition - _startPosition) + "\r\n");
                                            if (_contentRange != null) sb.Append("Content-Range: " + _contentRange + "\r\n");
                                            sb.Append("Accept-Ranges: bytes\r\n\r\n");

                                            Byte[] _headers = Encoding.ASCII.GetBytes(sb.ToString());
                                            ssl.Write(_headers);

                                            br.BaseStream.Position = _startPosition;
                                            int _size = 4096;
                                            while (Form1.bServiceFlag && mySocket.Connected)
                                            {
                                                long _remaining = _endPosition - br.BaseStream.Position;
                                                byte[] _response = new byte[_remaining <= _size ? _remaining : _size];
                                                br.Read(_response, 0, _response.Length);
                                                ssl.Write(_response);
                                                if (_remaining <= _size) break;
                                            }
                                            ssl.Flush();
                                        }
                                    }
                                }
                                else
                                {
                                    bool bFileNotFound = true;
                                    switch (_domainName)
                                    {
                                        case "api1.origin.com":
                                        case "api2.origin.com":
                                        case "api3.origin.com":
                                        case "api4.origin.com":
                                            string ip = ClassWeb.HostToIP(_domainName, "114.114.114.114");
                                            if (!string.IsNullOrEmpty(ip))
                                            {
                                                bool decode = false;
                                                if (_filePath.StartsWith("/ecommerce2/downloadURL"))
                                                {
                                                    decode = true;
                                                    if (Properties.Settings.Default.EACDN)
                                                    {
                                                        _filePath = Regex.Replace(_filePath, @"&cdnOverride=[^&]+", "");
                                                        _filePath += "&cdnOverride=akamai";
                                                    }
                                                    if (Properties.Settings.Default.EAProtocol)
                                                    {
                                                        _filePath = Regex.Replace(_filePath, @"&https=[^&]+", "");
                                                        //_filePath += "&https=false"; 
                                                    }
                                                    _buffer = Regex.Replace(_buffer, @"^" + _method + " .+", _method + " " + _filePath + " HTTP/1.1");
                                                }
                                                string _url = "https://" + _domainName + _filePath;
                                                Uri uri = new Uri(_url);
                                                SocketPackage socketPackage = ClassWeb.SslRequest(uri, Encoding.ASCII.GetBytes(_buffer), ip, decode);
                                                if (string.IsNullOrEmpty(socketPackage.Err))
                                                {
                                                    bFileNotFound = false;
                                                    if (Form1.bRecordLog)
                                                    {
                                                        Match m1 = Regex.Match(socketPackage.Headers, @"^HTTP[^\s]+\s([^\s]+)");
                                                        if (m1.Success) parentForm.SaveLog("HTTP " + m1.Groups[1].Value, _url, ((IPEndPoint)mySocket.RemoteEndPoint).Address.ToString());
                                                        if (_filePath.StartsWith("/ecommerce2/downloadURL"))
                                                        {
                                                            m1 = Regex.Match(socketPackage.Html, @"<url>(?<url>.+)</url>");
                                                            if (m1.Success) parentForm.SaveLog("下载地址", m1.Groups["url"].Value, ((IPEndPoint)mySocket.RemoteEndPoint).Address.ToString(), 0x008000);
                                                        }
                                                    }
                                                    string str = socketPackage.Headers;
                                                    str = Regex.Replace(str, @"(Content-Encoding|Transfer-Encoding|Content-Length): .+\r\n", "");
                                                    str = Regex.Replace(str, @"\r\n\r\n", "\r\nContent-Length: " + socketPackage.Buffer.Length + "\r\n\r\n");
                                                    Byte[] _headers = Encoding.ASCII.GetBytes(str);
                                                    ssl.Write(_headers);
                                                    ssl.Write(socketPackage.Buffer);
                                                    ssl.Flush();
                                                }
                                            }
                                            break;
                                    }
                                    if (bFileNotFound)
                                    {
                                        string _url = "https://" + _domainName + _filePath;
                                        if (Form1.bRecordLog) parentForm.SaveLog("HTTP 404", _url, ((IPEndPoint)mySocket.RemoteEndPoint).Address.ToString());
                                        Byte[] _response = Encoding.ASCII.GetBytes("File not found.");
                                        StringBuilder sb = new StringBuilder();
                                        sb.Append("HTTP/1.1 404 Not Found\r\n");
                                        sb.Append("Content-Type: text/html\r\n");
                                        sb.Append("Content-Length: " + _response.Length + "\r\n\r\n");
                                        Byte[] _headers = Encoding.ASCII.GetBytes(sb.ToString());
                                        ssl.Write(_headers);
                                        ssl.Write(_response);
                                        ssl.Flush();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            if (mySocket.Connected)
            {
                try
                {
                    mySocket.Shutdown(SocketShutdown.Both);
                }
                catch { }
            }
            mySocket.Close();
            mySocket.Dispose();
        }

        public void Close()
        {
            if (socket != null)
            {
                socket.Close();
                socket.Dispose();
                socket = null;

                X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                foreach (var item in store.Certificates)
                {
                    if (item.SubjectName.Name == "CN=Xbox下载助手")
                    {
                        store.Remove(item);
                    }
                }
                store.Close();
            }
        }
    }
}
