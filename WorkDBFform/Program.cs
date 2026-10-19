using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WorkDBFform
{
    static class Program
    {
        private const string Usage =
            "Загрузка без окна:\r\n" +
            "  WorkDBFform.exe <папка или файлы> <база> [сервер] [/replace] [/all] [/quiet]\r\n\r\n" +
            "Пример:\r\n" +
            "  WorkDBFform.exe D:\\NAS \"ГНИ \\\"Слободзея\\\"\" localhost\r\n\r\n" +
            "  сервер — по умолчанию из WorkDBFform.exe.config или localhost\r\n" +
            "  по умолчанию таблицы, которые уже есть в базе, пропускаются\r\n" +
            "  /replace — заменять существующие таблицы, если DBF изменился\r\n" +
            "  /all     — вместе с /replace: заменять все, даже не изменившиеся\r\n" +
            "  /quiet — не показывать окно с итогом\r\n" +
            "  /gui   — открыть окно\r\n\r\n" +
            "Журнал пишется в WorkDBFform.log рядом с exe.\r\n" +
            "Без параметров берутся Folder и Database из WorkDBFform.exe.config; если они пустые — открывается окно.";

        /// <summary>
        /// Главная точка входа для приложения.
        /// «WorkDBFform.exe папка база [сервер]» или заполненный конфиг — загрузка без окна;
        /// /gui, только файлы (перетащенные на значок) или пустой конфиг — обычное окно.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            bool gui = args.Any(a => Is(a, "/gui"));
            args = args.Where(a => !Is(a, "/gui")).ToArray();

            if (!gui && args.Any(a => a == "/?" || a == "-?" || Is(a, "/help")))
            {
                MessageBox.Show(Usage, "WorkDBFform");
                return 0;
            }

            if (!gui && args.Length > 0)
            {
                var paths = new List<string>();
                var names = new List<string>();
                bool all = false, quiet = false;
                bool? replace = null;           // не указано — из конфига, по умолчанию существующие таблицы пропускаются
                foreach (string a in args)
                {
                    if (Is(a, "/all")) all = true;
                    else if (Is(a, "/replace")) replace = true;
                    else if (Is(a, "/keep")) replace = false;
                    else if (Is(a, "/quiet")) quiet = true;
                    else if (Directory.Exists(a) || File.Exists(a) || LooksLikePath(a)) paths.Add(a);
                    else names.Add(a);
                }
                if (paths.Count > 0 && names.Count > 0)
                    return RunWithoutWindow(paths, names[0], names.Count > 1 ? names[1] : ConfigServer(),
                        replace ?? Flag("Replace", false), !all && Flag("SkipUnchanged", true), !quiet && Flag("ShowResult", true));
                // только файлы/папки (перетащили на значок) — открываем окно с ними
            }
            else if (!gui)
            {
                string folder = Setting("Folder");
                string database = Setting("Database");
                if (folder.Length > 0 && database.Length > 0)
                    return RunWithoutWindow(folder.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0), database,
                        ConfigServer(), Flag("Replace", false), Flag("SkipUnchanged", true), Flag("ShowResult", true));
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1(args));
            return 0;
        }

        private static bool Is(string arg, string flag)
        {
            return arg.Equals(flag, StringComparison.OrdinalIgnoreCase) || arg.Equals("-" + flag.Substring(1), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Похоже на путь, даже если его нет (опечатка в папке не должна стать именем базы).</summary>
        private static bool LooksLikePath(string a)
        {
            return a.Contains(":\\") || a.StartsWith("\\\\") || a.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase);
        }

        private static string Setting(string key)
        {
            return (ConfigurationManager.AppSettings[key] ?? "").Trim();
        }

        private static string ConfigServer()
        {
            string server = Setting("Server");
            return server.Length > 0 ? server : "localhost";
        }

        /// <summary>true/false (а также 1/0, да/нет) из appSettings; пусто — значение по умолчанию.</summary>
        private static bool Flag(string key, bool byDefault)
        {
            string v = Setting(key).ToLowerInvariant();
            if (v == "true" || v == "1" || v == "да") return true;
            if (v == "false" || v == "0" || v == "нет") return false;
            return byDefault;
        }

        /// <summary>Загрузка без окна; журнал — в WorkDBFform.log. Код выхода: 0 — без ошибок, 1 — есть ошибки, 2 — не запустилось.</summary>
        private static int RunWithoutWindow(IEnumerable<string> paths, string database, string server, bool replace, bool skipUnchanged, bool showResult)
        {
            var list = paths.ToList();
            string logPath;
            int code;
            string text;
            using (var log = OpenLog(out logPath))
            {
                Action<string> write = line =>
                {
                    log.WriteLine(line);
                    log.Flush();
                };
                write(string.Format("\r\n==== {0:dd.MM.yyyy HH:mm:ss} | {1} | сервер {2}, база {3} ====",
                    DateTime.Now, string.Join("; ", list), server, database));
                try
                {
                    var files = DbfLoader.CollectFiles(list, write);
                    if (files.Count == 0)
                    {
                        write("Нет файлов для загрузки.");
                        code = 2;
                        text = "Нет DBF-файлов для загрузки.\r\nПроверьте путь: " + string.Join("; ", list);
                    }
                    else
                    {
                        var opt = new LoadOptions { Server = server, Database = database, Replace = replace, SkipUnchanged = skipUnchanged };
                        var loader = new DbfLoader(opt, write);
                        code = loader.Run(files, CancellationToken.None) == 0 ? 0 : 1;
                        text = SummaryText(loader.Summary);
                    }
                }
                catch (Exception e)
                {
                    write("ОШИБКА: " + e.Message);
                    code = 2;
                    text = "Загрузка не выполнена:\r\n" + e.Message;
                }
            }
            if (showResult)
                ShowResult(string.Format("Сервер: {0}, база: {1}\r\n\r\n{2}\r\n\r\nЖурнал: {3}", server, database, text, logPath),
                    code == 0 ? MessageBoxIcon.Information : code == 1 ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
            return code;
        }

        private static string SummaryText(LoadSummary s)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Загрузка DBF завершена за {0:0} с.\r\n\r\n", s.Elapsed.TotalSeconds);
            sb.AppendFormat("Файлов: {0}\r\n", s.Files);
            sb.AppendFormat("Загружено: {0} (строк: {1:N0})\r\n", s.Loaded, s.Rows);
            if (s.Unchanged > 0)
                sb.AppendFormat("Без изменений, пропущено: {0}\r\n", s.Unchanged);
            if (s.Kept > 0)
                sb.AppendFormat("Таблица уже была, не тронута: {0}\r\n", s.Kept);
            sb.AppendFormat("С ошибками: {0}", s.Failed);
            if (s.FailedTables.Count > 0)
            {
                sb.Append("\r\n    ").Append(string.Join(", ", s.FailedTables.Take(10)));
                if (s.FailedTables.Count > 10)
                    sb.AppendFormat(" и ещё {0}", s.FailedTables.Count - 10);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Окно с итогом. Только на рабочем столе пользователя (задание Планировщика «для всех пользователей»
        /// его не покажет) и само закрывается через 30 минут, чтобы задание не зависало.
        /// </summary>
        private static void ShowResult(string text, MessageBoxIcon icon)
        {
            if (!Environment.UserInteractive)
                return;
            var t = new Thread(() => MessageBox.Show(text, "Загрузка DBF в SQL Server", MessageBoxButtons.OK, icon,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly)) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromMinutes(30));
        }

        /// <summary>WorkDBFform.log рядом с exe, а если туда нельзя писать — в %LOCALAPPDATA%\WorkDBFform.</summary>
        private static StreamWriter OpenLog(out string path)
        {
            try
            {
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkDBFform.log");
                return new StreamWriter(path, true, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDBFform");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "WorkDBFform.log");
                return new StreamWriter(path, true, Encoding.UTF8);
            }
        }
    }
}
