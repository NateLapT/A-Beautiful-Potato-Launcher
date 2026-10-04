// ---------------------------------------------------------------------------
//  Project Zomboid: the accounts the game has saved for a server - READ ONLY.
//
//  The game keeps its saved servers and accounts in %USERPROFILE%\Zomboid\db\
//  ServerListSteam.db: a "server" table, and an "account" table with any
//  number of accounts per server (username, password as a bcrypt hash, save
//  password, Steam Relay, last logon). The launcher's join window lists them
//  so the player can pick one; ZomboidJoinHelper then has the game's connect
//  window fill that account in.
//
//  Nothing here writes to that database. An earlier version reordered the
//  accounts so the chosen one came first, on the reading that the window
//  fills in the first saved account - true, but the window never finds the
//  server at all on a +connect start (see ZomboidJoinHelper), so the reorder
//  changed nothing the player could see. Passwords are never read.
//
//  SQLITE WITHOUT A DEPENDENCY
//    winsqlite3.dll ships with Windows 10 and 11 in System32 and exports the
//    ordinary sqlite3_* API, so the launcher stays one exe.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ZomboidAccount
    {
        public long Id;
        public long ServerId;
        public string Username = "";
        public bool PasswordSaved;
        public bool SteamRelay;
        public DateTime LastLogon = DateTime.MinValue;
        public long TimePlayed;
    }

    internal static class ZomboidAccounts
    {
        public static string DatabasePath
        {
            get { return DatabaseOverride ?? Path.Combine(Zomboid.UserFolder, "db", "ServerListSteam.db"); }
        }

        /// <summary>Points everything here at a copy instead - for testing only.</summary>
        internal static string DatabaseOverride;

        private static readonly string[] ServerColumns = { "id", "name", "ip", "port", "serverPassword" };
        private static readonly string[] AccountColumns =
            { "id", "serverId", "username", "password", "isSavePassword", "isUseSteamRelay", "authType", "lastLogon" };

        /// <summary>
        /// The saved accounts the game's connect window would choose from for
        /// this server, in the game's order (the first is the one it fills in).
        /// Null with a reason when the database cannot be used; an empty list
        /// when it can and there are none.
        /// </summary>
        public static List<ZomboidAccount> For(string host, int port, out string problem)
        {
            problem = null;
            if (!File.Exists(DatabasePath))
            {
                problem = "the game has no saved servers yet";
                return new List<ZomboidAccount>();
            }

            try
            {
                using (var db = Sqlite.Open(DatabasePath, readOnly: true))
                {
                    if (!SchemaFits(db, out problem)) return null;

                    long target = TargetServer(db, host, port);
                    if (target == 0) return new List<ZomboidAccount>();

                    return db.Query("SELECT id, serverId, username, password <> '', isSavePassword, isUseSteamRelay, "
                                    + "lastLogon, timePlayed FROM account WHERE serverId = ?", target)
                             .Select(r => new ZomboidAccount
                             {
                                 Id = Sqlite.Long(r[0]),
                                 ServerId = Sqlite.Long(r[1]),
                                 Username = Convert.ToString(r[2]) ?? "",
                                 PasswordSaved = Sqlite.Long(r[3]) != 0 && Sqlite.Long(r[4]) != 0,
                                 SteamRelay = Sqlite.Long(r[5]) != 0,
                                 LastLogon = ParseTime(Convert.ToString(r[6])),
                                 TimePlayed = Sqlite.Long(r[7])
                             })
                             .ToList();
                }
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// The server row the connect window will read for this address.
        ///
        /// The window walks the game's whole list and keeps the LAST entry whose
        /// ip and port match exactly (as text - a row saved under a hostname is
        /// not matched). The list is ordered most-recently-played first, so with
        /// duplicates that is the one played longest ago. 0 when there is none.
        /// </summary>
        private static long TargetServer(Sqlite db, string host, int port)
        {
            var rows = db.Query("SELECT s.id, MAX(a.lastLogon) FROM server s LEFT JOIN account a ON s.id = a.serverId "
                                + "WHERE s.ip = ? AND s.port = ? GROUP BY s.id "
                                + "ORDER BY MAX(a.lastLogon) DESC NULLS LAST, s.id", host, port);
            return rows.Count == 0 ? 0 : Sqlite.Long(rows[rows.Count - 1][0]);
        }

        private static bool SchemaFits(Sqlite db, out string problem)
        {
            problem = null;
            foreach (var table in new[] { new { Name = "server", Cols = ServerColumns },
                                          new { Name = "account", Cols = AccountColumns } })
            {
                var have = new HashSet<string>(
                    db.Query("PRAGMA table_info(" + table.Name + ")").Select(r => Convert.ToString(r[1])),
                    StringComparer.OrdinalIgnoreCase);
                var missing = table.Cols.Where(c => !have.Contains(c)).ToList();
                if (missing.Count > 0)
                {
                    problem = "the game's saved-server database has changed (" + table.Name + " is missing "
                              + string.Join(", ", missing) + ")";
                    return false;
                }
            }
            return true;
        }

        private static DateTime ParseTime(string v)
        {
            DateTime t;
            return DateTime.TryParse(v, out t) ? t : DateTime.MinValue;
        }
    }

    /// <summary>
    /// Just enough of SQLite, through Windows' own winsqlite3.dll: open, run a
    /// query with parameters, read rows back. Values come back as long,
    /// double, string or null.
    /// </summary>
    internal sealed class Sqlite : IDisposable
    {
        private const string Dll = "winsqlite3.dll";
        private const int OK = 0, ROW = 100, DONE = 101;
        private const int OpenReadOnly = 0x01, OpenReadWrite = 0x02;
        private static readonly IntPtr Transient = new IntPtr(-1);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern double sqlite3_column_double(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_bind_text(IntPtr stmt, int idx, byte[] text, int bytes, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long value);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_bind_null(IntPtr stmt, int idx);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        private IntPtr _db;

        private Sqlite(IntPtr db) { _db = db; }

        public static Sqlite Open(string path, bool readOnly)
        {
            IntPtr db;
            // Never OpenCreate: the database is the game's to make.
            int flags = readOnly ? OpenReadOnly : OpenReadWrite;
            int rc = sqlite3_open_v2(Utf8(path), out db, flags, IntPtr.Zero);
            if (rc != OK)
            {
                string why = db == IntPtr.Zero ? "code " + rc : Message(db);
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                throw new IOException("could not open the game's saved-server database: " + why);
            }
            sqlite3_busy_timeout(db, 3000);
            return new Sqlite(db);
        }

        public List<object[]> Query(string sql, params object[] args) { return Run(sql, args, true); }

        private List<object[]> Run(string sql, object[] args, bool collect)
        {
            IntPtr stmt;
            byte[] text = Utf8(sql);
            if (sqlite3_prepare_v2(_db, text, text.Length, out stmt, IntPtr.Zero) != OK)
                throw new InvalidOperationException(Message(_db));

            var rows = new List<object[]>();
            try
            {
                for (int i = 0; args != null && i < args.Length; i++) Bind(stmt, i + 1, args[i]);

                while (true)
                {
                    int rc = sqlite3_step(stmt);
                    if (rc == DONE) break;
                    if (rc != ROW) throw new InvalidOperationException(Message(_db));
                    if (!collect) continue;

                    int n = sqlite3_column_count(stmt);
                    var row = new object[n];
                    for (int c = 0; c < n; c++) row[c] = Column(stmt, c);
                    rows.Add(row);
                }
            }
            finally { sqlite3_finalize(stmt); }
            return rows;
        }

        private static void Bind(IntPtr stmt, int idx, object v)
        {
            if (v == null) { sqlite3_bind_null(stmt, idx); return; }
            if (v is string)
            {
                byte[] b = Encoding.UTF8.GetBytes((string)v);
                sqlite3_bind_text(stmt, idx, b, b.Length, Transient);
                return;
            }
            if (v is bool) { sqlite3_bind_int64(stmt, idx, (bool)v ? 1 : 0); return; }
            sqlite3_bind_int64(stmt, idx, Convert.ToInt64(v));
        }

        private static object Column(IntPtr stmt, int c)
        {
            switch (sqlite3_column_type(stmt, c))
            {
                case 1: return sqlite3_column_int64(stmt, c);
                case 2: return sqlite3_column_double(stmt, c);
                case 5: return null;
                case 4: return null;                // blobs (icons) are never needed here
                default:
                    IntPtr p = sqlite3_column_text(stmt, c);
                    int len = sqlite3_column_bytes(stmt, c);
                    if (p == IntPtr.Zero || len <= 0) return "";
                    var buf = new byte[len];
                    Marshal.Copy(p, buf, 0, len);
                    return Encoding.UTF8.GetString(buf);
            }
        }

        public static long Long(object v)
        {
            if (v == null) return 0;
            if (v is long) return (long)v;
            long r;
            return long.TryParse(Convert.ToString(v), out r) ? r : 0;
        }

        private static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes((s ?? "") + "\0"); }

        private static string Message(IntPtr db)
        {
            IntPtr p = sqlite3_errmsg(db);
            return p == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(p);
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; }
        }
    }
}
