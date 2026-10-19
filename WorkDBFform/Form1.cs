using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WorkDBFform
{
    public partial class Form1 : Form
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDBFform", "settings.txt");

        private class FileRow
        {
            public DbfFileItem Item;
            public string Count;
        }

        private readonly List<FileRow> files = new List<FileRow>();
        private CancellationTokenSource cts;
        private string lastFolder = "";

        private bool startedFromCommandLine;

        public Form1(string[] args)
        {
            InitializeComponent();
            txtServer.Text = "localhost";
            txtDatabase.Text = "ГНИ \"Слободзея\"";
            LoadSettings();
            if (args != null && args.Length > 0)
                ApplyCommandLine(args);
        }

        private const string Usage =
            "Запуск:  WorkDBFform.exe <папка или файлы> <база> [сервер] [/replace]\r\n\r\n" +
            "Пример:  WorkDBFform.exe D:\\NAS \"ГНИ \\\"Слободзея\\\"\" localhost /replace\r\n\r\n" +
            "Если указаны папка (или файлы) и база, загрузка начинается сразу.\r\n" +
            "Сервер по умолчанию — из прошлого запуска (или localhost).\r\n" +
            "/replace — пересоздать таблицы, которые уже есть.";

        /// <summary>
        /// WorkDBFform.exe &lt;папки/файлы...&gt; &lt;база&gt; [сервер] [/replace]:
        /// существующие пути — что загружать, остальное по порядку — база и сервер.
        /// </summary>
        private void ApplyCommandLine(string[] args)
        {
            var paths = new List<string>();
            var names = new List<string>();
            bool missing = false;
            foreach (string a in args)
            {
                if (a == "/?" || a == "-?" || a.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(Usage, Text);
                    return;
                }
                if (a.Equals("/replace", StringComparison.OrdinalIgnoreCase) || a.Equals("-replace", StringComparison.OrdinalIgnoreCase))
                    chkReplace.Checked = true;
                else if (Directory.Exists(a) || File.Exists(a))
                    paths.Add(a);
                else if (a.Contains(":\\") || a.StartsWith("\\\\") || a.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Не найдена папка или файл: " + a);
                    missing = true;
                }
                else
                    names.Add(a);
            }
            if (paths.Count > 0)
            {
                lastFolder = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(Path.GetFullPath(paths[0]));
                AddPaths(paths);
            }
            if (names.Count > 0)
                txtDatabase.Text = names[0];
            if (names.Count > 1)
                txtServer.Text = names[1];
            if (names.Count > 2)
            {
                Log("Лишние параметры: " + string.Join(" ", names.Skip(2)) + "\r\n" + Usage);
                missing = true;
            }

            // папка и база заданы — загрузка начинается сразу после открытия окна
            if (names.Count > 0 && files.Count > 0 && !missing)
            {
                startedFromCommandLine = true;
                Shown += (s, e) => btnStart.PerformClick();
            }
        }

        // ---------------------------- список файлов ----------------------------

        private void btnAddFiles_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Файлы DBF (*.dbf)|*.dbf|Все файлы (*.*)|*.*";
                dlg.Multiselect = true;
                dlg.Title = "Выберите DBF-файлы (можно несколько)";
                if (Directory.Exists(lastFolder))
                    dlg.InitialDirectory = lastFolder;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    lastFolder = Path.GetDirectoryName(dlg.FileNames[0]);
                    AddPaths(dlg.FileNames);
                }
            }
        }

        private void btnAddFolder_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Папка с DBF-файлами (вложенные папки тоже просматриваются)";
                if (Directory.Exists(lastFolder))
                    dlg.SelectedPath = lastFolder;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    lastFolder = dlg.SelectedPath;
                    AddPaths(new[] { dlg.SelectedPath });
                }
            }
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            files.Clear();
            RefreshList();
        }

        private void Files_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && cts == null)
                e.Effect = DragDropEffects.Copy;
        }

        private void Files_DragDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && cts == null)
                AddPaths(paths);
        }

        private void lvFiles_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && cts == null)      // Delete — убрать выбранные файлы из списка
            {
                foreach (ListViewItem it in lvFiles.SelectedItems)
                    files.Remove((FileRow)it.Tag);
                RefreshList();
            }
        }

        /// <summary>Добавляет файлы и папки; повторы имён таблиц и «плохие» имена пропускает с сообщением в журнале.</summary>
        private void AddPaths(IEnumerable<string> paths)
        {
            var byName = files.ToDictionary(r => r.Item.Name, r => r.Item.Path);
            int added = 0;
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (string p in paths)
                {
                    IEnumerable<string> found;
                    if (Directory.Exists(p))
                        found = DbfLoader.FindDbf(p);
                    else if (File.Exists(p))
                        found = new[] { p };
                    else
                        continue;
                    foreach (string path in found)
                    {
                        string name = DbfLoader.TableName(path);
                        if (name == null)
                        {
                            Log("  не добавлен (имя с недопустимыми символами, похоже на копию): " + path);
                            continue;
                        }
                        string first;
                        if (byName.TryGetValue(name, out first))
                        {
                            if (!string.Equals(Path.GetFullPath(first), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                                Log(string.Format("  не добавлен (такое имя уже есть): {0}  [взят: {1}]", path, first));
                            continue;
                        }
                        byName[name] = path;
                        files.Add(new FileRow { Item = new DbfFileItem { Name = name, Path = path }, Count = HeaderCount(path) });
                        added++;
                    }
                }
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            files.Sort((a, b) => DbfLoader.NaturalCompare(a.Item.Name, b.Item.Name));
            RefreshList();
            if (added > 0)
                Log(string.Format("Добавлено файлов: {0}, всего в списке: {1}", added, files.Count));
        }

        private static string HeaderCount(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var h = new byte[8];
                    return fs.Read(h, 0, 8) == 8 ? BitConverter.ToUInt32(h, 4).ToString("N0") : "?";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "?";
            }
        }

        private void RefreshList()
        {
            lvFiles.BeginUpdate();
            lvFiles.Items.Clear();
            foreach (var r in files)
                lvFiles.Items.Add(new ListViewItem(new[] { r.Item.Name, r.Count, r.Item.Path }) { Tag = r });
            lvFiles.EndUpdate();
            lblFilesCount.Text = "Файлов: " + files.Count;
        }

        // ---------------------------- загрузка ----------------------------

        private async void btnStart_Click(object sender, EventArgs e)
        {
            var opt = new LoadOptions
            {
                Server = txtServer.Text.Trim(),
                Database = txtDatabase.Text.Trim(),
                Replace = chkReplace.Checked,
            };
            if (files.Count == 0)
            {
                MessageBox.Show(this, "Добавьте DBF-файлы или папку.", Text);
                return;
            }
            if (opt.Server.Length == 0 || opt.Database.Length == 0)
            {
                MessageBox.Show(this, "Укажите сервер и базу данных, куда записывать.", Text);
                return;
            }
            SaveSettings();

            var items = files.Select(r => r.Item).ToList();
            cts = new CancellationTokenSource();
            SetBusy(true);
            Log(string.Format("\r\n файлов: {1} | сервер {2}, база {3}",
                DateTime.Now, items.Count, opt.Server, opt.Database));
            try
            {
                int fails = await Task.Run(() => new DbfLoader(opt, Log).Run(items, cts.Token));
                if (!startedFromCommandLine)
                    MessageBox.Show(this, fails == 0 ? "Готово, ошибок нет." : "Готово. Таблиц с ошибками: " + fails + " (подробности в журнале).",
                        Text, MessageBoxButtons.OK, fails == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Log("ОШИБКА: " + ex.Message);
                if (!startedFromCommandLine)
                    MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                cts.Dispose();
                cts = null;
            }
        }

        private void SetBusy(bool busy)
        {
            btnStart.Enabled = btnAddFiles.Enabled = btnAddFolder.Enabled = btnClear.Enabled = !busy;
            txtServer.Enabled = txtDatabase.Enabled = chkReplace.Enabled = !busy;
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (cts != null)
            {
                if (MessageBox.Show(this, "Идёт загрузка. Прервать и выйти? Недогруженная таблица не будет создана.", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                cts.Cancel();
            }
            SaveSettings();
        }

        private void txtLog_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.A && e.Control)
                txtLog.SelectAll();
        }

        private void Log(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Log), line);
                return;
            }
            txtLog.AppendText(line + "\r\n");
        }

        // ---------------------------- настройки ----------------------------

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return;
                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0)
                        continue;
                    string key = line.Substring(0, i), value = line.Substring(i + 1);
                    switch (key)
                    {
                        case "server": txtServer.Text = value; break;
                        case "database": txtDatabase.Text = value; break;
                        case "replace": chkReplace.Checked = value == "1"; break;
                        case "folder": lastFolder = value; break;
                    }
                }
            }
            catch (Exception)
            {
                // испорченный файл настроек — работаем со значениями по умолчанию
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[]
                {
                    "server=" + txtServer.Text.Trim(),
                    "database=" + txtDatabase.Text.Trim(),
                    "replace=" + (chkReplace.Checked ? "1" : "0"),
                    "folder=" + lastFolder,
                }, Encoding.UTF8);
            }
            catch (Exception)
            {
                // настройки не критичны
            }
        }
    }
}
