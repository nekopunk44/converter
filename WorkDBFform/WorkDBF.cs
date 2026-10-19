using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.Odbc;
using System.Data;
using System.Windows.Forms;

namespace WorkDBFform
{
    class WorkDBF
    {
        private OdbcConnection Conn = null;
        public DataTable Execute(string Command)
        {
            DataTable dt = null;
            if (Conn != null)
            {
                try
                {
                    Conn.ConnectionString = @"Driver={Microsoft dBASE Driver (*.dbf)}";
                    Conn.Open();
                    dt = new DataTable();
                    System.Data.Odbc.OdbcCommand oCmd = Conn.CreateCommand();
                    oCmd.CommandText = Command;
                    dt.Load(oCmd.ExecuteReader());
                    Conn.Close();
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message);
                }
            }
            return dt;
        }
        public DataTable GetAll(string DB_path)
        {
            return Execute("SELECT * FROM " + DB_path);
        }

        public WorkDBF()
        {
            this.Conn = new System.Data.Odbc.OdbcConnection();
            Conn.ConnectionString = @"Driver={Microsoft dBase  Driver (*.dbf)};" +
                   "SourceType=DBF;Exclusive=No;" +
                   "Collate=Machine;NULL=NO;DELETED=NO;" +
                   "BACKGROUNDFETCH=NO;";
        }
    }
}
