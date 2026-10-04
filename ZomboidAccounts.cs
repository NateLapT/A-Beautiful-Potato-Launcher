// ---------------------------------------------------------------------------
//  Project Zomboid: which account the game's connect window opens with.
//
//  WHY THIS EXISTS
//    The game takes only two things on its command line - +connect host:port
//    and +password (the SERVER password). The account name, the account
//    password and Steam Relay are filled into its connect window from the
//    servers and accounts it has saved, in %USERPROFILE%\Zomboid\db\
//    ServerListSteam.db. A server can have any number of saved accounts, but
//    the window always takes the FIRST one - read from the game's own code:
//    Server.getUserName(), getPwd() and getUseSteamRelay() all return
//    accounts.get(0), and the accounts are loaded by "SELECT * FROM account
//    WHERE serverId = ?" with no ORDER BY, which is row-id order.
//
//    So a player with two accounts on one server got the first one every
//    time and retyped the other. Here the player picks the account in the
//    launcher, and just before the game starts that account is made the first
//    one for the server - by swapping its row id with the current first row,
//    which moves every column (saved password hash, relay, play time) with it.
//
//  WHAT IS NEVER DONE
//    Passwords are not read, shown or written. The game stores them as bcrypt
//    hashes, so a password typed into the launcher could not be stored anyway;
//    a new account is saved with its name only and the game asks for the
//    password once. And nothing is written while the game is running - the
//    launcher refuses to start it twice, which is the same moment this runs.
//
//  IF THE GAME CHANGES ITS DATABASE
//    The columns used are checked first. Anything unexpected and this simply
//    does nothing: the game opens its connect window as it always did. A copy
//    of the database is taken before every change.
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

                    return db.Query("SELECT id, serverId, username, password, isSavePassword, isUseSteamRelay, "
                                    + "lastLogon, timePlayed FROM account WHERE serverId = ?", target)
                             .Select(r => new ZomboidAccount
                             {
                                 Id = Sqlite.Long(r[0]),
                                 ServerId = Sqlite.Long(r[1]),
                                 Username = Convert.ToString(r[2]) ?? "",
                                 PasswordSaved = !string.IsNullOrEmpty(Convert.ToString(r[3]))
                                                 && Sqlite.Long(r[4]) != 0,
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
        /// Makes the game's connect window open with this account: the chosen
        /// saved account, or a new one by name, becomes the server's first
        /// account, with Steam Relay set as asked. The server is added to the
        /// game's list if it is not there yet.
        ///
        /// Returns false, having changed nothing, when the database cannot be
        /// used - the game then asks for everything itself, as it always did.
        /// </summary>
        public static bool PrepareJoin(string host, int port, string serverName,
                                       ZomboidAccount chosen, string newUsername, bool steamRelay,
                                       Action<string> log)
        {
            string problem;
            string path = DatabasePath;

            try
            {
                // The game makes this file the first time it lists servers. If
                // it has not, the launcher does not invent one - the game asks.
                if (!File.Exists(path))
                {
                    log("  The game has no saved servers yet, so it will ask for the account itself.");
                    return false;
                }

                // A copy before every change, so a surprise in a future game
                // version can be undone by hand.
                File.Copy(path, path + ".launcher-backup", true);

                using (var db = Sqlite.Open(path, readOnly: false))
                {
                    if (!SchemaFits(db, out problem))
                    {
                        log("  Saved accounts left alone: " + problem + ".");
                        return false;
                    }

                    db.Exec("BEGIN IMMEDIATE");
                    try
                    {
                        long server = TargetServer(db, host, port);
                        if (server == 0)
                        {
                            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                            db.Exec("INSERT INTO server (name, ip, port, serverPassword, description, lastOnline, lastDataUpdate) "
                                    + "VALUES (?, ?, ?, '', '', ?, ?)", serverName ?? host, host, port, now, now);
                            server = db.LastId;
                            log("  Added " + host + ":" + port + " to the game's server list.");
                        }

                        long accountId;
                        string name;
                        if (chosen != null)
                        {
                            accountId = chosen.Id;
                            name = chosen.Username;

                            // Saved under a duplicate entry for the same address:
                            // brought across to the one the window reads.
                            db.Exec("UPDATE account SET serverId = ? WHERE id = ?", server, accountId);
                        }
                        else
                        {
                            name = (newUsername ?? "").Trim();
                            var same = db.Query("SELECT id FROM account WHERE serverId = ? AND username = ?", server, name);
                            if (same.Count > 0) accountId = Sqlite.Long(same[0][0]);
                            else
                            {
                                // Name only. The password is the game's to ask for
                                // and to store - as a hash, which the launcher could
                                // not produce.
                                db.Exec("INSERT INTO account (serverId, username, password, isSavePassword, "
                                        + "isUseSteamRelay, authType, timePlayed) VALUES (?, ?, '', 1, ?, 1, 0)",
                                        server, name, steamRelay ? 1 : 0);
                                accountId = db.LastId;
                                log("  Saved a new account \"" + name + "\" for this server - the game asks for its password once.");
                            }
                        }

                        db.Exec("UPDATE account SET isUseSteamRelay = ? WHERE id = ?", steamRelay ? 1 : 0, accountId);

                        // FIRST, by row id. Swapping ids moves whole rows, so the
                        // saved password, play time and everything else stay with
                        // the account they belong to.
                        long first = Sqlite.Long(db.Query("SELECT MIN(id) FROM account WHERE serverId = ?", server)[0][0]);
                        if (first != accountId)
                        {
                            long parked = -1000000 - accountId;
                            db.Exec("UPDATE account SET id = ? WHERE id = ?", parked, accountId);
                            db.Exec("UPDATE account SET id = ? WHERE id = ?", accountId, first);
                            db.Exec("UPDATE account SET id = ? WHERE id = ?", first, parked);
                        }

                        // Read it back the way the game will.
                        var check = db.Query("SELECT username FROM account WHERE serverId = ?", server);
                        if (check.Count == 0 || !string.Equals(Convert.ToString(check[0][0]), name, StringComparison.Ordinal))
                            throw new InvalidOperationException("the account did not come out first");

                        db.Exec("COMMIT");
                    }
                    catch
                    {
                        try { db.Exec("ROLLBACK"); } catch { }
                        throw;
                    }
                }

                log("  The game's connect window will open with account \"" + (chosen != null ? chosen.Username : newUsername)
                    + "\", Steam Relay " + (steamRelay ? "on" : "off") + ".");
                return true;
            }
            catch (Exception ex)
            {
                log("  Could not set the account in the game's saved list (" + ex.Message
                    + ") - the game will ask for it.");
                return false;
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
    /// statement with parameters, read rows back. Values come back as long,
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
        private static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        private IntPtr _db;

        private Sqlite(IntPtr db) { _db = db; }

        public static Sqlite Open(string path, bool readOnly)
        {
            IntPtr db;
            // Never OpenCreate: this only ever edits a database the game made.
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

        public long LastId { get { return sqlite3_last_insert_rowid(_db); } }

        public void Exec(string sql, params object[] args) { Run(sql, args, false); }

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
