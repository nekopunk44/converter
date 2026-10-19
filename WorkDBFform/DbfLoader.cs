using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WorkDBFform
{
    /// <summary>Куда и как загружать.</summary>
    class LoadOptions
    {
        public string Server = "localhost";
        public string Database = "";
        public Encoding Encoding = Encoding.GetEncoding(866);   // DOS-кириллица
        public bool Replace;                // пересоздавать уже существующие таблицы
        public bool SkipUnchanged;          // при Replace не трогать таблицы, чей DBF не менялся с прошлой загрузки
    }

    /// <summary>Файл в списке загрузки: ANKF1.DBF -> таблица dbo.ANKF1.</summary>
    class DbfFileItem
    {
        public string Name;
        public string Path;
    }

    class LoadStats
    {
        public int Loaded, Deleted, Empty, Bad;
    }

    /// <summary>Итог всего запуска.</summary>
    class LoadSummary
    {
        public int Files, Loaded, Unchanged, Kept, Failed;
        public long Rows;
        public List<string> FailedTables = new List<string>();
        public TimeSpan Elapsed;
        public bool Cancelled;
    }

    /// <summary>
    /// Загрузка DBF-файлов в SQL Server: один файл = одна таблица dbo.ИМЯ_ФАЙЛА.
    /// Все столбцы допускают NULL; удалённые и полностью пустые записи пропускаются;
    /// в конце число загруженных строк сверяется с COUNT(*) в таблице.
    /// </summary>
    class DbfLoader
    {
        private const int Batch = 10000;    // строк в одной порции SqlBulkCopy

        private readonly LoadOptions options;
        private readonly Action<string> log;

        /// <summary>Итог последнего вызова Run.</summary>
        public LoadSummary Summary { get; private set; }

        public DbfLoader(LoadOptions options, Action<string> log)
        {
            this.options = options;
            this.log = log;
        }

        // ---------------------------- список файлов ----------------------------

        /// <summary>Имя таблицы по имени файла или null, если в имени есть недопустимые символы (например TIPIM---.DBF).</summary>
        public static string TableName(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            return Regex.IsMatch(stem, "^[A-Za-z0-9_]+$") ? stem.ToUpperInvariant() : null;
        }

        /// <summary>Все .dbf в папке и вложенных папках (сначала файлы папки, затем подпапки).</summary>
        public static IEnumerable<string> FindDbf(string folder)
        {
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(folder);
                dirs = Directory.GetDirectories(folder);
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
                if (string.Equals(Path.GetExtension(f), ".dbf", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            foreach (string d in dirs)
                foreach (string f in FindDbf(d))
                    yield return f;
        }

        /// <summary>DBF из папок и файлов по порядку; имена с недопустимыми символами и повторы имён пропускаются.</summary>
        public static List<DbfFileItem> CollectFiles(IEnumerable<string> paths, Action<string> log)
        {
            var result = new List<DbfFileItem>();
            var byName = new Dictionary<string, string>();
            foreach (string p in paths)
            {
                IEnumerable<string> found;
                if (Directory.Exists(p))
                    found = FindDbf(p);
                else if (File.Exists(p))
                    found = new[] { p };
                else
                {
                    log("  не найдена папка или файл: " + p);
                    continue;
                }
                foreach (string path in found)
                {
                    string name = TableName(path);
                    if (name == null)
                    {
                        log("  не загружаю (имя с недопустимыми символами, похоже на копию): " + path);
                        continue;
                    }
                    string first;
                    if (byName.TryGetValue(name, out first))
                    {
                        log(string.Format("  не загружаю (такое имя уже есть): {0}  [взят: {1}]", path, first));
                        continue;
                    }
                    byName[name] = path;
                    result.Add(new DbfFileItem { Name = name, Path = path });
                }
            }
            result.Sort((a, b) => NaturalCompare(a.Name, b.Name));
            log("Найдено DBF для загрузки: " + result.Count);
            return result;
        }

        /// <summary>Сравнение «по-человечески»: NACH2 раньше NACH10.</summary>
        public static int NaturalCompare(string a, string b)
        {
            var pa = Regex.Split(a, @"(\d+)");
            var pb = Regex.Split(b, @"(\d+)");
            for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
            {
                int c;
                bool da = pa[i].Length > 0 && char.IsDigit(pa[i][0]);
                bool db = pb[i].Length > 0 && char.IsDigit(pb[i][0]);
                if (da && db)
                {
                    string ta = pa[i].TrimStart('0'), tb = pb[i].TrimStart('0');
                    c = ta.Length != tb.Length ? ta.Length.CompareTo(tb.Length) : string.CompareOrdinal(ta, tb);
                }
                else
                    c = string.CompareOrdinal(pa[i], pb[i]);
                if (c != 0)
                    return c;
            }
            return pa.Length.CompareTo(pb.Length);
        }

        // ---------------------------- типы ----------------------------

        private static int DecimalPrecision(DbfField f)
        {
            return Math.Min(Math.Max(f.Length, f.DecimalCount + 1), 38);
        }

        public static string SqlType(DbfField f)
        {
            int n = f.Length, d = f.DecimalCount;
            switch (f.Type)
            {
                case 'C':
                case 'V':
                    return n > 4000 ? "nvarchar(max)" : "nvarchar(" + Math.Max(n, 1) + ")";
                case 'L': return "bit";
                case 'D': return "date";
                case 'T':
                case '@': return "datetime2";
                case 'N':
                case 'F':
                    if (d > 0) return "decimal(" + DecimalPrecision(f) + "," + d + ")";
                    if (n <= 4) return "smallint";
                    if (n <= 9) return "int";
                    if (n <= 18) return "bigint";
                    return "decimal(" + Math.Min(n, 38) + ",0)";
                case 'I':
                case '+': return "int";
                case 'O': return "float";
                case 'B': return n == 8 ? "float" : "nvarchar(max)";
                case 'Y': return "decimal(19,4)";
                case 'M':
                case 'G':
                case 'P': return "nvarchar(max)";
                default: return "nvarchar(255)";
            }
        }

        private static Type ClrType(DbfField f)
        {
            switch (f.Type)
            {
                case 'L': return typeof(bool);
                case 'D':
                case 'T':
                case '@': return typeof(DateTime);
                case 'N':
                case 'F':
                    if (f.DecimalCount > 0) return typeof(decimal);
                    if (f.Length <= 4) return typeof(short);
                    if (f.Length <= 9) return typeof(int);
                    if (f.Length <= 18) return typeof(long);
                    return typeof(decimal);
                case 'I':
                case '+': return typeof(int);
                case 'O': return typeof(double);
                case 'B': return f.Length == 8 ? typeof(double) : typeof(string);
                case 'Y': return typeof(decimal);
                default: return typeof(string);
            }
        }

        /// <summary>Число из поля N/F -> значение под тип столбца; null, если не помещается.</summary>
        public static object ToColumnValue(DbfField f, decimal v)
        {
            try
            {
                if (f.DecimalCount > 0)
                {
                    v = Math.Round(v, Math.Min(f.DecimalCount, 28), MidpointRounding.AwayFromZero);
                    int intDigits = DecimalPrecision(f) - f.DecimalCount;
                    if (intDigits <= 28 && Math.Abs(v) >= Pow10(intDigits))
                        return null;
                    return v;
                }
                v = decimal.Truncate(v);
                if (f.Length <= 4) return checked((short)v);
                if (f.Length <= 9) return checked((int)v);
                if (f.Length <= 18) return checked((long)v);
                return v;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        private static decimal Pow10(int n)
        {
            decimal r = 1;
            for (int i = 0; i < n; i++)
                r *= 10;
            return r;
        }

        private static string Q(string name)
        {
            return "[" + name.Replace("]", "]]") + "]";
        }

        // ---------------------------- подключение ----------------------------

        /// <summary>Вход под текущим пользователем Windows.</summary>
        private static string ConnectionString(LoadOptions o)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = o.Server,
                InitialCatalog = o.Database,
                IntegratedSecurity = true,
                ConnectTimeout = 15,
                ApplicationName = "WorkDBFform",
                TrustServerCertificate = true,
            };
            return b.ConnectionString;
        }

        // ---------------------------- загрузка ----------------------------

        /// <summary>Загружает файлы по очереди; возвращает число таблиц с ошибками.</summary>
        public int Run(IList<DbfFileItem> files, CancellationToken ct)
        {
            var summary = Summary = new LoadSummary { Files = files.Count };
            var con = new SqlConnection(ConnectionString(options));
            con.Open();
            log(string.Format("Подключено: сервер {0}, база {1}, SQL Server {2}", options.Server, options.Database, con.ServerVersion));
            EnsureHistory(con);

            int fails = 0, done = 0, unchanged = 0;
            var all = Stopwatch.StartNew();
            try
            {
                foreach (var item in files)
                {
                    if (ct.IsCancellationRequested)
                    {
                        log("Остановлено пользователем.");
                        summary.Cancelled = true;
                        break;
                    }
                    var t0 = Stopwatch.StartNew();
                    string status;
                    LoadStats st = null;
                    try
                    {
                        if (con.State != ConnectionState.Open)
                        {
                            con.Close();
                            con.Open();
                        }
                        st = LoadOne(con, item, ct, out status);
                    }
                    catch (OperationCanceledException)
                    {
                        log(string.Format("{0,-12} остановлено пользователем, таблица не изменена", item.Name));
                        summary.Cancelled = true;
                        break;
                    }
                    catch (Exception e)         // одна таблица не должна останавливать остальные
                    {
                        string msg = e.Message.Replace("\r", " ").Replace("\n", " ");
                        status = "ОШИБКА: " + (msg.Length > 300 ? msg.Substring(0, 300) : msg);
                        st = null;
                    }
                    if (status.StartsWith("ОШИБКА"))
                    {
                        fails++;
                        summary.FailedTables.Add(item.Name);
                    }
                    else if (status == UnchangedStatus)
                        unchanged++;
                    else if (status == KeptStatus)
                        summary.Kept++;
                    else if (st != null)
                    {
                        summary.Loaded++;
                        summary.Rows += st.Loaded;
                    }
                    string time = string.Format("{0:0.0} с", t0.Elapsed.TotalSeconds);
                    log(string.Format("{0,-12} {1,-8} {2}", item.Name, time, st == null ? status : string.Format(
                        "загружено {0}, удалённых пропущено {1}, пустых пропущено {2}, битых значений {3}  -> {4}",
                        st.Loaded, st.Deleted, st.Empty, st.Bad, status)));
                    done++;
                }
            }
            finally
            {
                con.Dispose();
            }
            summary.Unchanged = unchanged;
            summary.Failed = fails;
            summary.Elapsed = all.Elapsed;
            log(string.Format("\r\nГотово за {0:0} с. Таблиц: {1}, без изменений пропущено: {2}, с ошибками: {3}",
                all.Elapsed.TotalSeconds, done, unchanged, fails));
            return fails;
        }

        // ---------------------------- какие файлы уже загружены ----------------------------

        private const string History = "[dbo].[_DbfLoadHistory]";
        private const string UnchangedStatus = "без изменений с прошлой загрузки, пропущена";
        private const string KeptStatus = "пропущена (таблица уже есть; заменить — /replace или галочка «Пересоздавать таблицы»)";

        /// <summary>Служебная таблица: какой DBF (размер и время изменения) загружен в какую таблицу.</summary>
        private static void EnsureHistory(SqlConnection con)
        {
            using (var cmd = new SqlCommand(
                "IF OBJECT_ID(N'dbo._DbfLoadHistory', N'U') IS NULL CREATE TABLE " + History + " (" +
                "TableName sysname NOT NULL PRIMARY KEY, FilePath nvarchar(1000) NOT NULL, FileStamp varchar(200) NOT NULL, " +
                "RowsLoaded int NOT NULL, LoadedAt datetime2(0) NOT NULL)", con))
                cmd.ExecuteNonQuery();
        }

        /// <summary>Размер и время изменения DBF и его memo-файла (.FPT/.DBT); изменились — таблицу надо перезагрузить.</summary>
        private static string FileStamp(string path)
        {
            var parts = new List<string>();
            foreach (string f in Directory.EnumerateFiles(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".*"))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".dbf" || ext == ".fpt" || ext == ".dbt")
                {
                    var fi = new FileInfo(f);
                    parts.Add(ext + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks);
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        private static string LoadedStamp(SqlConnection con, string name)
        {
            using (var cmd = new SqlCommand("SELECT FileStamp FROM " + History + " WHERE TableName = @n", con))
            {
                cmd.Parameters.AddWithValue("@n", name);
                return cmd.ExecuteScalar() as string;
            }
        }

        private static void SaveStamp(SqlConnection con, SqlTransaction tx, DbfFileItem item, string stamp, int rows)
        {
            using (var cmd = new SqlCommand(
                "DELETE FROM " + History + " WHERE TableName = @n; " +
                "INSERT INTO " + History + " (TableName, FilePath, FileStamp, RowsLoaded, LoadedAt) VALUES (@n, @p, @s, @r, SYSDATETIME())",
                con, tx))
            {
                cmd.Parameters.AddWithValue("@n", item.Name);
                cmd.Parameters.AddWithValue("@p", item.Path);
                cmd.Parameters.AddWithValue("@s", stamp);
                cmd.Parameters.AddWithValue("@r", rows);
                cmd.ExecuteNonQuery();
            }
        }

        // ---------------------------- одна таблица ----------------------------

        private LoadStats LoadOne(SqlConnection con, DbfFileItem item, CancellationToken ct, out string status)
        {
            string table = "[dbo]." + Q(item.Name);
            string stamp = FileStamp(item.Path);        // до чтения: если файл поменяется во время загрузки, в следующий раз он перезагрузится

            bool exists;
            using (var cmd = new SqlCommand("SELECT OBJECT_ID(@n, N'U')", con))
            {
                cmd.Parameters.AddWithValue("@n", "dbo." + Q(item.Name));
                exists = !(cmd.ExecuteScalar() is DBNull);
            }
            if (exists && !options.Replace)
            {
                status = KeptStatus;
                return null;
            }
            if (exists && options.SkipUnchanged && stamp == LoadedStamp(con, item.Name))
            {
                status = UnchangedStatus;
                return null;
            }

            using (var dbf = new DbfReader(item.Path, options.Encoding))
            {
                var fields = dbf.Fields;
                if (fields.Count == 0)
                    throw new InvalidDataException("в файле нет полей");
                var st = new LoadStats();

                using (var tx = con.BeginTransaction())
                {
                    if (exists)
                        Exec(con, tx, "DROP TABLE " + table);
                    Exec(con, tx, "CREATE TABLE " + table + " (\n" + string.Join(",\n",
                        fields.Select(f => "    " + Q(f.Name) + " " + SqlType(f) + " NULL")) + "\n)");

                    var dt = new DataTable();
                    using (var bulk = new SqlBulkCopy(con, SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock, tx))
                    {
                        bulk.DestinationTableName = table;
                        bulk.BulkCopyTimeout = 0;
                        for (int i = 0; i < fields.Count; i++)
                        {
                            dt.Columns.Add("c" + i, ClrType(fields[i]));
                            bulk.ColumnMappings.Add(i, i);
                        }
                        foreach (var vals in dbf.Records())
                        {
                            if (IsEmpty(vals))
                            {
                                st.Empty++;
                                continue;
                            }
                            var row = dt.NewRow();
                            for (int i = 0; i < vals.Length; i++)
                                row[i] = vals[i] ?? DBNull.Value;
                            dt.Rows.Add(row);
                            st.Loaded++;
                            if (dt.Rows.Count >= Batch)
                            {
                                ct.ThrowIfCancellationRequested();
                                bulk.WriteToServer(dt);
                                dt.Clear();
                            }
                        }
                        ct.ThrowIfCancellationRequested();
                        if (dt.Rows.Count > 0)
                            bulk.WriteToServer(dt);
                    }

                    int inDb;
                    using (var cmd = new SqlCommand("SELECT COUNT(*) FROM " + table, con, tx))
                    {
                        cmd.CommandTimeout = 0;
                        inDb = (int)cmd.ExecuteScalar();
                    }
                    if (inDb == st.Loaded)
                        SaveStamp(con, tx, item, stamp, st.Loaded);
                    tx.Commit();
                    Finish(dbf, st);
                    status = inDb == st.Loaded ? "OK" : string.Format("ОШИБКА: в таблице {0}, загружено {1}", inDb, st.Loaded);
                    return st;
                }
            }
        }

        private static void Finish(DbfReader dbf, LoadStats st)
        {
            st.Deleted = dbf.HeaderCount - dbf.Yielded;
            st.Bad = dbf.BadValues;
        }

        private static bool IsEmpty(object[] vals)
        {
            foreach (var v in vals)
            {
                if (v == null) continue;
                var s = v as string;
                if (s != null && s.Length == 0) continue;
                return false;
            }
            return true;
        }

        private static void Exec(SqlConnection con, SqlTransaction tx, string sql)
        {
            using (var cmd = new SqlCommand(sql, con, tx))
            {
                cmd.CommandTimeout = 0;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
