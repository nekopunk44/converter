using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WorkDBFform
{
    /// <summary>Описание поля из заголовка DBF.</summary>
    class DbfField
    {
        public string Name;
        public char Type;
        public int Length;
        public int DecimalCount;
        public int Offset;          // смещение поля внутри записи (после байта-признака удаления)
    }

    /// <summary>
    /// Чтение DBF напрямую из файла, без ODBC (по правилам библиотеки dbfread).
    /// Битые числа, даты и логические значения превращаются в null и считаются в BadValues.
    /// Записи, помеченные на удаление, пропускаются и считаются в Yielded/HeaderCount.
    /// </summary>
    class DbfReader : IDisposable
    {
        private readonly FileStream file;
        private readonly Encoding encoding;
        private readonly MemoFile memo;

        public string FilePath { get; private set; }
        public int Version { get; private set; }
        public int HeaderCount { get; private set; }      // число записей по заголовку (вместе с удалёнными)
        public int HeaderLength { get; private set; }
        public int RecordLength { get; private set; }
        public List<DbfField> Fields { get; private set; }
        public int BadValues { get; private set; }        // битые значения, заменённые на null
        public int Yielded { get; private set; }          // прочитано неудалённых записей

        public DbfReader(string path, Encoding encoding)
        {
            FilePath = path;
            this.encoding = encoding;
            Fields = new List<DbfField>();
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            try
            {
                byte[] h = new byte[32];
                if (file.Read(h, 0, 32) < 32)
                    throw new InvalidDataException("файл слишком короткий для DBF");
                Version = h[0];
                HeaderCount = (int)BitConverter.ToUInt32(h, 4);
                HeaderLength = BitConverter.ToUInt16(h, 8);
                RecordLength = BitConverter.ToUInt16(h, 10);

                int offset = 0;
                byte[] d = new byte[32];
                while (true)
                {
                    int sep = file.ReadByte();
                    if (sep == -1 || sep == 0x0D || sep == 0x0A)
                        break;
                    d[0] = (byte)sep;
                    if (file.Read(d, 1, 31) < 31)
                        break;
                    int nameLen = Array.IndexOf(d, (byte)0, 0, 11);
                    var f = new DbfField
                    {
                        Name = encoding.GetString(d, 0, nameLen < 0 ? 11 : nameLen).Trim(),
                        Type = (char)d[11],
                        Length = d[16],
                        DecimalCount = d[17],
                        Offset = offset,
                    };
                    if (f.Type == 'C')          // Clipper: длина текста > 255 хранится как length + decimal * 256
                    {
                        f.Length |= f.DecimalCount << 8;
                        f.DecimalCount = 0;
                    }
                    offset += f.Length;
                    if (f.Type == '0')      // служебное поле Visual FoxPro (_NullFlags), в таблицу не идёт
                        continue;
                    Fields.Add(f);
                }

                if (HasMemoFields())
                    memo = MemoFile.Open(path, Version);   // нет файла .FPT/.DBT -> memo-поля будут NULL
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private bool HasMemoFields()
        {
            foreach (var f in Fields)
                if (f.Type == 'M' || f.Type == 'G' || f.Type == 'P' || (f.Type == 'B' && f.Length != 8))
                    return true;
            return false;
        }

        /// <summary>Все неудалённые записи; значения в порядке Fields.</summary>
        public IEnumerable<object[]> Records()
        {
            int dataLength = Math.Max(RecordLength - 1, 0);
            foreach (var f in Fields)
                dataLength = Math.Max(dataLength, f.Offset + f.Length);
            byte[] buf = new byte[dataLength];

            Yielded = 0;
            BadValues = 0;
            file.Position = HeaderLength;
            while (true)
            {
                int sep = file.ReadByte();
                if (sep == -1 || sep == 0x1A)         // конец данных
                    yield break;
                if (sep != ' ')                       // '*' — запись удалена
                {
                    file.Seek(RecordLength - 1, SeekOrigin.Current);
                    continue;
                }
                if (ReadFull(buf) < buf.Length)       // оборванная последняя запись
                    yield break;

                var values = new object[Fields.Count];
                for (int i = 0; i < Fields.Count; i++)
                    values[i] = Parse(Fields[i], buf);
                Yielded++;
                yield return values;
            }
        }

        private int ReadFull(byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = file.Read(buf, total, buf.Length - total);
                if (n <= 0)
                    break;
                total += n;
            }
            return total;
        }

        private object Bad()
        {
            BadValues++;
            return null;
        }

        private object Parse(DbfField f, byte[] rec)
        {
            int o = f.Offset, n = f.Length;
            switch (f.Type)
            {
                case 'C':
                case 'V':
                    return Text(rec, o, n);
                case 'N':
                case 'F':
                    return ParseNumber(f, Ascii(rec, o, n));
                case 'D':
                    return ParseDate(Ascii(rec, o, n));
                case 'L':
                    {
                        char c = (char)rec[o];
                        if ("TtYy".IndexOf(c) >= 0) return true;
                        if ("FfNn".IndexOf(c) >= 0) return false;
                        if (c == '?' || c == ' ') return null;
                        return Bad();
                    }
                case 'T':
                case '@':
                    return ParseTimestamp(rec, o, n);
                case 'I':
                case '+':
                    return n == 4 ? (object)BitConverter.ToInt32(rec, o) : Bad();
                case 'O':
                    return n == 8 ? NotNan(BitConverter.ToDouble(rec, o)) : Bad();
                case 'B':
                    if (n == 8)
                        return NotNan(BitConverter.ToDouble(rec, o));
                    return ParseMemo(rec, o, n);
                case 'Y':
                    return n == 8 ? (object)(BitConverter.ToInt64(rec, o) / 10000m) : Bad();
                case 'M':
                case 'G':
                case 'P':
                    return ParseMemo(rec, o, n);
                default:
                    return Text(rec, o, n);
            }
        }

        private static object NotNan(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? null : (object)v;
        }

        private string Text(byte[] buf, int offset, int count)
        {
            return encoding.GetString(buf, offset, count).Replace("\0", "").Trim();
        }

        private static string Ascii(byte[] buf, int offset, int count)
        {
            return Encoding.ASCII.GetString(buf, offset, count);
        }

        private static readonly char[] PyWhitespace = { ' ', '\t', '\n', '\r', '\v', '\f' };

        private object ParseNumber(DbfField f, string s)
        {
            s = s.Trim(PyWhitespace).Trim('*');      // '*' — переполнение поля в DBF
            if (s.Trim(PyWhitespace).Length == 0)
                return null;
            decimal v;
            if (!decimal.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return Bad();
            return DbfLoader.ToColumnValue(f, v) ?? Bad();
        }

        private object ParseDate(string s)
        {
            int y, m, d;
            if (s.Length >= 8 && ParseInt(s.Substring(0, 4), out y) && ParseInt(s.Substring(4, 2), out m)
                && ParseInt(s.Substring(6, 2), out d)
                && y >= 1 && y <= 9999 && m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
                return new DateTime(y, m, d);
            if (s.Trim(' ', '0').Length == 0)      // пробелы/нули — пустая дата
                return null;
            return Bad();
        }

        private static bool ParseInt(string s, out int v)
        {
            return int.TryParse(s.Trim(PyWhitespace), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
        }

        private object ParseTimestamp(byte[] rec, int o, int n)
        {
            if (n != 8)
                return Bad();
            if (Ascii(rec, o, n).Trim(PyWhitespace).Length == 0)
                return null;
            long day = BitConverter.ToUInt32(rec, o);
            long msec = BitConverter.ToUInt32(rec, o + 4);
            if (day == 0)
                return null;
            try
            {
                return new DateTime(1, 1, 1).AddDays(day - 1721426).AddMilliseconds(msec);   // юлианский день 1721426 = 01.01.0001
            }
            catch (ArgumentOutOfRangeException)
            {
                return Bad();
            }
        }

        private object ParseMemo(byte[] rec, int o, int n)
        {
            long index;
            if (n == 4)
                index = BitConverter.ToUInt32(rec, o);
            else
            {
                string s = Ascii(rec, o, n).Trim(' ', '\0');
                if (s.Length == 0)
                    return null;
                if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out index))
                    return Bad();
            }
            if (index <= 0 || memo == null)
                return null;
            byte[] data;
            try
            {
                data = memo.Read(index);
            }
            catch (IOException)
            {
                return Bad();
            }
            if (data == null)
                return null;
            return encoding.GetString(data).Replace("\0", "").Trim();
        }

        public void Dispose()
        {
            file.Dispose();
            if (memo != null)
                memo.Dispose();
        }
    }

    /// <summary>Файл memo-полей: .FPT (FoxPro) или .DBT (dBase III / IV).</summary>
    class MemoFile : IDisposable
    {
        private enum Kind { Fpt, Dbase3, Dbase4 }

        private readonly FileStream file;
        private readonly Kind kind;
        private readonly int blockSize;

        private MemoFile(FileStream file, Kind kind, int blockSize)
        {
            this.file = file;
            this.kind = kind;
            this.blockSize = blockSize;
        }

        /// <summary>Ищет рядом с DBF файл .FPT или .DBT; null, если его нет.</summary>
        public static MemoFile Open(string dbfPath, int dbVersion)
        {
            string dir = Path.GetDirectoryName(dbfPath);
            string stem = Path.GetFileNameWithoutExtension(dbfPath);
            foreach (string ext in new[] { ".fpt", ".dbt" })
            {
                string memoPath = FindIgnoreCase(dir, stem + ext);
                if (memoPath == null)
                    continue;
                var fs = new FileStream(memoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] h = new byte[32];
                fs.Read(h, 0, 32);
                if (ext == ".fpt")
                {
                    int bs = (h[6] << 8) | h[7];          // размер блока, big-endian
                    return new MemoFile(fs, Kind.Fpt, bs > 0 ? bs : 64);
                }
                if (dbVersion == 0x83)
                    return new MemoFile(fs, Kind.Dbase3, 512);
                int bs4 = BitConverter.ToUInt16(h, 20);
                return new MemoFile(fs, Kind.Dbase4, bs4 > 0 ? bs4 : 512);
            }
            return null;
        }

        private static string FindIgnoreCase(string dir, string fileName)
        {
            string exact = Path.Combine(dir, fileName);
            if (File.Exists(exact))
                return exact;
            foreach (string p in Directory.EnumerateFiles(dir))
                if (string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        public byte[] Read(long index)
        {
            long pos = index * blockSize;
            if (pos >= file.Length)
                throw new IOException("ссылка за пределы memo-файла");
            file.Position = pos;
            if (kind == Kind.Fpt)
            {
                byte[] h = ReadBytes(8);
                long length = ((long)h[4] << 24) | ((long)h[5] << 16) | ((long)h[6] << 8) | h[7];
                return ReadBytes(length);
            }
            if (kind == Kind.Dbase4)
            {
                byte[] h = ReadBytes(8);
                if (h[0] == 0xFF && h[1] == 0xFF && h[2] == 0x08 && h[3] == 0x00)
                {
                    long length = BitConverter.ToUInt32(h, 4);
                    return ReadBytes(Math.Max(length - 8, 0));
                }
                file.Position = pos;
            }
            // dBase III: текст до символа 0x1A
            var ms = new MemoryStream();
            byte[] block = new byte[512];
            while (true)
            {
                int n = file.Read(block, 0, block.Length);
                if (n <= 0)
                    return ms.ToArray();
                int end = Array.IndexOf(block, (byte)0x1A, 0, n);
                ms.Write(block, 0, end >= 0 ? end : n);
                if (end >= 0)
                    return ms.ToArray();
            }
        }

        private byte[] ReadBytes(long count)
        {
            if (count < 0 || file.Position + count > file.Length)
                throw new IOException("memo-файл обрывается");
            byte[] buf = new byte[count];
            int total = 0;
            while (total < count)
            {
                int n = file.Read(buf, total, (int)count - total);
                if (n <= 0)
                    throw new IOException("memo-файл обрывается");
                total += n;
            }
            return buf;
        }

        public void Dispose()
        {
            file.Dispose();
        }
    }
}
