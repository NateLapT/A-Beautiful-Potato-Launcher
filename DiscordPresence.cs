// ---------------------------------------------------------------------------
//  "Playing DayZ - Via A Beautiful Potato Launcher" in the player's Discord
//  status while the game runs.
//
//  HOW: the Discord app on this PC listens on a named pipe,
//  \\.\pipe\discord-ipc-0 (up to -9). The launcher connects, says which
//  Discord application it is (an Application ID - public, NOT a token; there
//  is no login and nothing secret here), and sets an activity. Discord clears
//  it by itself when the pipe closes, so the status can never outlive the
//  launcher.
//
//  WHAT IT SAYS: the top line is always "Playing <the Discord application's
//  name>" - Discord takes that from the application, not from us. The line
//  under it is ours. It deliberately says nothing about WHICH server: a status
//  that names the server tells anyone watching a streamer where to find them.
//
//  Nothing happens when Discord is not running, or when the player has
//  turned it off in Settings.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ABeautifulPotatoLauncher
{
    internal static class DiscordPresence
    {
        /// <summary>The Discord application whose name is shown as "Playing ...".</summary>
        private const string ApplicationId = "1130955332663906304";

        private const string Details = "Via A Beautiful Potato Launcher";

        /// <summary>Optional art: an image uploaded under Rich Presence > Art Assets with this key.</summary>
        private const string LargeImage = "logo";

        /// <summary>
        /// Off until ApplicationId is an application named "DayZ": the top line
        /// shows that application's name, and this one is named after a bot.
        /// While false the status is never set and its setting is hidden.
        /// </summary>
        public static readonly bool Ready = false;

        private static int _running;            // one watcher at a time

        /// <summary>
        /// Shows the status until <paramref name="gameExe"/> has started and
        /// then exited (or never appears within five minutes). Returns at once;
        /// the work is on a background thread. Never throws.
        /// </summary>
        public static void ShowWhilePlaying(string gameExe, Action<string> log)
        {
            if (!Ready || !ServerStore.LoadDiscordStatus()) return;
            if (Interlocked.Exchange(ref _running, 1) == 1) return;

            new Thread(() =>
            {
                try { Run(Path.GetFileNameWithoutExtension(gameExe), log); }
                catch (Exception ex) { Log(log, "Discord status: " + ex.Message); }
                finally { Interlocked.Exchange(ref _running, 0); }
            }) { IsBackground = true, Name = "discord-presence" }.Start();
        }

        private static void Run(string processName, Action<string> log)
        {
            NamedPipeClientStream pipe = Connect();
            if (pipe == null) return;           // Discord is not running: nothing to do

            using (pipe)
            {
                var json = new JavaScriptSerializer();

                // Handshake, then the READY reply.
                Send(pipe, 0, json.Serialize(new Dictionary<string, object> { { "v", 1 }, { "client_id", ApplicationId } }));
                if (Receive(pipe) == null) return;

                var activity = new Dictionary<string, object>
                {
                    { "details", Details },
                    { "timestamps", new Dictionary<string, object> { { "start", DateTimeOffset.UtcNow.ToUnixTimeSeconds() } } },
                    { "assets", new Dictionary<string, object> { { "large_image", LargeImage }, { "large_text", "A Beautiful Potato Launcher" } } }
                };
                Send(pipe, 1, json.Serialize(new Dictionary<string, object>
                {
                    { "cmd", "SET_ACTIVITY" },
                    { "args", new Dictionary<string, object> { { "pid", Process.GetCurrentProcess().Id }, { "activity", activity } } },
                    { "nonce", Guid.NewGuid().ToString() }
                }));
                string reply = Receive(pipe);
                if (reply != null && reply.Contains("\"evt\":\"ERROR\""))
                {
                    Log(log, "Discord did not accept the status: " + reply);
                    return;
                }
                Log(log, "Discord status set.");

                // Wait for the game to appear, then for it to go.
                var appearBy = DateTime.UtcNow.AddMinutes(5);
                bool seen = false;
                while (true)
                {
                    bool running = Process.GetProcessesByName(processName).Length > 0;
                    if (running) seen = true;
                    if (seen && !running) break;
                    if (!seen && DateTime.UtcNow > appearBy) break;
                    Thread.Sleep(5000);
                }
            }
            Log(log, "Discord status cleared.");
        }

        private static NamedPipeClientStream Connect()
        {
            for (int i = 0; i < 10; i++)
            {
                var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.None);
                try { p.Connect(300); return p; }
                catch { p.Dispose(); }
            }
            return null;
        }

        // A frame is: int32 opcode, int32 length (little-endian), UTF-8 JSON.
        private static void Send(Stream s, int op, string payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(payload);
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            s.Write(frame, 0, frame.Length);
            s.Flush();
        }

        private static string Receive(Stream s)
        {
            var head = new byte[8];
            if (!ReadAll(s, head)) return null;
            int len = BitConverter.ToInt32(head, 4);
            if (len < 0 || len > 1 << 20) return null;
            var body = new byte[len];
            if (!ReadAll(s, body)) return null;
            return Encoding.UTF8.GetString(body);
        }

        private static bool ReadAll(Stream s, byte[] buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf, got, buf.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static void Log(Action<string> log, string line)
        {
            if (log == null) return;
            try { log(line); } catch { }
        }
    }
}
