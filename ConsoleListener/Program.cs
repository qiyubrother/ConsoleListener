﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml;
using System.Linq;
using System.Xml.Linq;
using System.Reflection;
using System.Diagnostics;

namespace ConsoleListener
{
    class Program
    {
        [DllImport ("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern void OutputDebugString(string message);
        static string logFileName = "ConsoleListener.log";

		#region SetWindowPos
		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetWindowPos(
	        IntPtr hWnd,
	        IntPtr hWndInsertAfter,
	        int x,
	        int y,
	        int cx,
	        int cy,
	        int uFlags);
		private const int HWND_TOPMOST = -1;
		private const int SWP_NOMOVE = 0x0002;
		private const int SWP_NOSIZE = 0x0001;
		#endregion
		static void Main(string[] args)
        {
            if (File.Exists("app.xml"))
            {
                var s = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "app.xml"));
                var doc = new XmlDocument();
                doc.LoadXml(s);
                var nodes = doc.SelectNodes("config/out");
                if (nodes.Count > 0)
                {
                    var a = nodes[0].Attributes["fileName"];
                    if (a != null)
                    {
                        logFileName = a.Value;
                    }
                }
                nodes = doc.SelectNodes("config/color");
                if (nodes.Count > 0)
                {
                    var fc = nodes[0].Attributes["foregroundColor"];
                    if (fc != null)
                    {
                        if (Enum.TryParse<ConsoleColor>(fc.Value, out ConsoleColor c))
                        {
                            Console.ForegroundColor = c;
                        }
                    }
                    var bc = nodes[0].Attributes["backgroundColor"];
                    if (bc != null)
                    {
                        if (Enum.TryParse<ConsoleColor>(bc.Value, out ConsoleColor c))
                        {
                            Console.BackgroundColor = c;
                        }
                    }
                }

				nodes = doc.SelectNodes("config/style");
				if (nodes.Count > 0)
				{
					var a = nodes[0].Attributes["topMost"];
					if (a != null)
					{
                        if (a.Value == "true")
                        {
                            IntPtr hWnd = Process.GetCurrentProcess().MainWindowHandle;
							SetWindowPos(hWnd,
								new IntPtr(HWND_TOPMOST),
								0, 0, 0, 0,
								SWP_NOMOVE | SWP_NOSIZE);

						}
					}
				}
			}
            var tw = new StreamWriter(logFileName);
            DbgView dv = new DbgView(tw);
            dv.Start();
            Console.ReadLine();
        }
    }

    public class DbgView : IDisposable
    {
        public DbgView(TextWriter writer)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            this.writer = writer;
        }

        public void Start()
        {
            lock (this.lockObj)
            {
                if (this.listenerThread == null)
                {
                    this.listenerThread = new Thread(ListenerThread) { IsBackground = true };
                    this.listenerThread.Start();
                }
            }
        }

        public void Stop()
        {
            lock (this.lockObj)
            {
                if (this.listenerThread != null)
                {
                    this.listenerThread.Interrupt();
                    this.listenerThread.Join(10 * 1000);
                    this.listenerThread = null;
                }
            }
        }

        public void Dispose()
        {
            this.Stop();
        }

        private void ListenerThread(object state)
        {
            EventWaitHandle bufferReadyEvent = null;
            EventWaitHandle dataReadyEvent = null;
            MemoryMappedFile memoryMappedFile = null;
            List<string> ignoreContaintList = new List<string>();   /* 必须忽略 */
            List<string> containtContaintList = new List<string>(); /* 必须包含 */
            if (File.Exists("app.xml"))
            {
                var s = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "app.xml"));
                var doc = new XmlDocument();
                doc.LoadXml(s);
                var nodes = doc.SelectNodes("config/ignore/add");
                foreach (XmlNode node in nodes)
                {
                    var a = node.Attributes["Containt"];
                    if (a != null && a.Value.Trim() != string.Empty)
                    {
                        ignoreContaintList.Add(a.Value);
                    }
                }
                nodes = doc.SelectNodes("config/containt/add");
                foreach (XmlNode node in nodes)
                {
                    var a = node.Attributes["Containt"];
                    if (a != null && a.Value.Trim() != string.Empty)
                    {
                        if (a.Value.Trim() == "*")
                        {
                            // 如果包含任意，则忽略所有。
                            containtContaintList.Clear();
                            break;
                        }
                        containtContaintList.Add(a.Value);
                    }
                }
            }
            try
            {
                bool createdNew;
                var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                var eventSecurity = new EventWaitHandleSecurity();
                eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(everyone, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));

                bufferReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_BUFFER_READY", out createdNew, eventSecurity);
                if (!createdNew) throw new Exception("Some DbgView already running");

                dataReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_DATA_READY", out createdNew, eventSecurity);
                if (!createdNew) throw new Exception("Some DbgView already running");

                var memoryMapSecurity = new MemoryMappedFileSecurity();
                memoryMapSecurity.AddAccessRule(new AccessRule<MemoryMappedFileRights>(everyone, MemoryMappedFileRights.ReadWrite, AccessControlType.Allow));
                memoryMappedFile = MemoryMappedFile.CreateNew("DBWIN_BUFFER", 4096, MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.None, memoryMapSecurity, System.IO.HandleInheritability.None);

                bufferReadyEvent.Set();
                writer.WriteLine($"[ConsoleListener] Started. Press enter key to exit.");
                Console.WriteLine($"[ConsoleListener] Started. Press enter key to exit.");
                using (var accessor = memoryMappedFile.CreateViewAccessor())
                {
                    byte[] buffer = new byte[4096];
                    while (dataReadyEvent.WaitOne())
                    {
                        accessor.ReadArray<byte>(0, buffer, 0, buffer.Length);
                        int processId = BitConverter.ToInt32(buffer, 0);
                        int terminator = Array.IndexOf<byte>(buffer, 0, 4);
                        string msg = Encoding.Default.GetString(buffer, 4, (terminator < 0 ? buffer.Length : terminator) - 4);
                        if (ignoreContaintList.Count > 0)
                        {
                            if (!ignoreContaintList.Any(x=>msg.Contains(x)))
                            {
                                if (containtContaintList.Count == 0 || containtContaintList.Any(x => msg.Contains(x)))
                                {
                                    writer.Write("[{0:00000}] {1}", processId, msg);
                                    Console.WriteLine("[{0:00000}] {1}", processId, msg);
                                    writer.Flush();
                                }
                            }
                        }
                        else if (ignoreContaintList.Count == 0)
                        {
                            if (containtContaintList.Count == 0 || containtContaintList.Any(x => msg.Contains(x)))
                            {
                                writer.Write("[{0:00000}] {1}", processId, msg);
                                Console.WriteLine("[{0:00000}] {1}", processId, msg);
                                writer.Flush();
                            }
                        }
                        bufferReadyEvent.Set();
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
                writer.WriteLine("[ConsoleListener] Stopped.");
                Console.WriteLine("[ConsoleListener] Stopped.");
            }
            catch (Exception e)
            {
                writer.WriteLine("[ConsoleListener] Error: " + e.Message);
            }
            finally
            {
                foreach (var disposable in new IDisposable[] { bufferReadyEvent, dataReadyEvent, memoryMappedFile })
                {
                    if (disposable != null) disposable.Dispose();
                }
            }
        }

        private Thread listenerThread;
        private readonly object lockObj = new object();
        private readonly TextWriter writer;
    }
}
