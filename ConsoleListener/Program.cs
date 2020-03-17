﻿using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ConsoleListener
{
    class Program
    {
        [DllImport ("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern void OutputDebugString(string message);
        static void Main(string[] args)
        {
            var tw = new StreamWriter("debug.log");
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
                this.writer.WriteLine("[ConsoleListener] Started.");
                Console.WriteLine("[ConsoleListener] Started.");
                using (var accessor = memoryMappedFile.CreateViewAccessor())
                {
                    byte[] buffer = new byte[4096];
                    while (dataReadyEvent.WaitOne())
                    {
                        accessor.ReadArray<byte>(0, buffer, 0, buffer.Length);
                        int processId = BitConverter.ToInt32(buffer, 0);
                        int terminator = Array.IndexOf<byte>(buffer, 0, 4);
                        string msg = Encoding.Default.GetString(buffer, 4, (terminator < 0 ? buffer.Length : terminator) - 4);
                        writer.Write("[{0:00000}] {1}", processId, msg);
                        Console.WriteLine("[{0:00000}] {1}", processId, msg);
                        writer.Flush();
                        bufferReadyEvent.Set();
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
                this.writer.WriteLine("[ConsoleListener] Stopped.");
                Console.WriteLine("[ConsoleListener] Stopped.");
            }
            catch (Exception e)
            {
                this.writer.WriteLine("[ConsoleListener] Error: " + e.Message);
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
