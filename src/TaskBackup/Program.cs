using NLog;
using System;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace TaskBackup
{
    class Program
    {
        private static Logger _logger;


        static void Main(string[] args)
        {
            _logger = LogManager.GetCurrentClassLogger();

            _logger.Warn("Inicio rotina de backup");

            try
            {
                var dataAtualNome = DateTime.Today.ToString("yyyyMMdd");

                var backupFileName = ConfigurationManager.AppSettings["BackupDirectory"] +
                    ConfigurationManager.AppSettings["BackupFileName"] +
                    dataAtualNome + ".bak";

                var backupZipFileName = ConfigurationManager.AppSettings["BackupDirectory"] +
                    ConfigurationManager.AppSettings["BackupFileName"] +
                    dataAtualNome + ".gz";

                
                var directoryBackupCleanup = new DirectoryBackupCleanup();

                directoryBackupCleanup.DirectoryCleanupBeforeBackup(ConfigurationManager.AppSettings["BackupDirectory"],
                    dataAtualNome);

                
                var backupSQLDatabase = new BackupSQLDatabase();
                backupSQLDatabase.BackupDatabase(ConfigurationManager.AppSettings["BackupDatabase"],
                    ConfigurationManager.AppSettings["BackupDatabaseName"],
                    ConfigurationManager.AppSettings["BackupDatabaseDescription"],
                    backupFileName);
                
                var zipFileBackup = new ZipFileBackup();
                zipFileBackup.ZipBackup(backupFileName, backupZipFileName);

                var ftpTransfBackup = new FTPTransfBackup();
                ftpTransfBackup.Transf(backupZipFileName);
                
                directoryBackupCleanup.DirectoryCleanupOlder(ConfigurationManager.AppSettings["BackupDirectory"],
                    dataAtualNome);

                ftpTransfBackup.CleanupBackupOlder(ConfigurationManager.AppSettings["BackupFileName"], dataAtualNome + ".gz");

            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "Aplicação não terminou seu ciclo corretamente");
            }
            finally
            {
                _logger.Warn("Final rotina de backup");
            }

        }

        public class BackupSQLDatabase
        {
            public void BackupDatabase(string databaseName, string backupName, string backupDescription, string backupFilename)
            {
                _logger.Info("Backup - Inicio");

                var connectionStringProd = ConfigurationManager.ConnectionStrings["imperioPrd"].ConnectionString;

                using (var con = new SqlConnection(connectionStringProd))
                {
                    con.FireInfoMessageEventOnUserErrors = true;
                    con.InfoMessage += OnInfoMessage;
                    con.Open();

                    string commandScript = string.Format(
                        "backup database {0} to disk = {1} with description = {2}, name = {3}, stats = 1",
                        QuoteIdentifier(databaseName),
                        QuoteString(backupFilename),
                        QuoteString(backupDescription),
                        QuoteString(backupName));

                    using (var cmd = new SqlCommand(commandScript, con))
                    {
                        cmd.CommandTimeout = 1000;

                        cmd.ExecuteNonQuery();
                    }
                    con.Close();
                    con.InfoMessage -= OnInfoMessage;
                    con.FireInfoMessageEventOnUserErrors = false;
                }

                _logger.Info("Backup - Fim");
            }

            private void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
            {
                foreach (SqlError info in e.Errors)
                {
                    if (info.Class > 10)
                    {
                        // TODO: treat this as a genuine error
                        _logger.Fatal(info);
                    }
                    else
                    {
                        // TODO: treat this as a progress message
                    }
                }
            }

            private string QuoteIdentifier(string name)
            {
                return "[" + name.Replace("]", "]]") + "]";
            }

            private string QuoteString(string text)
            {
                return "'" + text.Replace("'", "''") + "'";
            }
        }

        public class FTPTransfBackup
        {
            public void Transf(string backFileName)
            {

                _logger.Info("Tranf FTP - Zip Backup - Inicio");

                FileStream fs = null;
                Stream rs = null;

                try
                {
                    string file = backFileName;
                    string uploadFileName = new FileInfo(file).Name;
                    string uploadUrl = ConfigurationManager.AppSettings["FTPUrl"];
                    fs = new FileStream(file, FileMode.Open, FileAccess.Read);

                    string ftpUrl = string.Format("{0}/{1}", uploadUrl, uploadFileName);

                    var requestObj = FtpWebRequest.Create(ftpUrl) as FtpWebRequest;

                    requestObj.Method = WebRequestMethods.Ftp.UploadFile;
                    requestObj.Credentials = new NetworkCredential(ConfigurationManager.AppSettings["FTPUserName"],
                        ConfigurationManager.AppSettings["FTPPassword"]);
                    rs = requestObj.GetRequestStream();

                    byte[] buffer = new byte[8092];
                    int read = 0;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        rs.Write(buffer, 0, read);
                    }
                    rs.Flush();
                }
                catch (Exception ex)
                {
                    _logger.Fatal(ex, "File upload/transfer Failed");

                    throw ex;
                    
                }
                finally
                {
                    if (fs != null)
                    {
                        fs.Close();
                        fs.Dispose();
                    }

                    if (rs != null)
                    {
                        rs.Close();
                        rs.Dispose();
                    }
                }

                _logger.Info("Tranf FTP - Zip Backup - Fim");

            }


            public void CleanupBackupOlder(string nomeBackup, string nomeUltimoBackup)
            {

                _logger.Info("Limpeza FTP - Inicio");

                string url = ConfigurationManager.AppSettings["FTPUrl"];

                var request = (FtpWebRequest)WebRequest.Create(url);

                request.Method = WebRequestMethods.Ftp.ListDirectory;

                request.Credentials = new NetworkCredential(ConfigurationManager.AppSettings["FTPUserName"],
                        ConfigurationManager.AppSettings["FTPPassword"]);


                var response = (FtpWebResponse)request.GetResponse();

                Stream responseStream = response.GetResponseStream();

                var reader = new StreamReader(responseStream);
                string names = reader.ReadToEnd();

                reader.Close();
                response.Close();

                string[] arquivos = names.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var arquivo in arquivos)
                {
                    if (arquivo.Contains(nomeBackup))
                    {
                        if (!arquivo.Contains(nomeUltimoBackup))
                        {
                            DeleteFileOnServer(arquivo);
                        }
                    }
                }

                _logger.Info("Limpeza FTP - FIM");
            }


            private bool DeleteFileOnServer(string fileName)
            {

                string deleteUrl = ConfigurationManager.AppSettings["FTPUrl"];

                string ftpDeleteUrl = string.Format("{0}/{1}", deleteUrl, fileName);

                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpDeleteUrl);

                request.Method = WebRequestMethods.Ftp.DeleteFile;
                request.Credentials = new NetworkCredential(ConfigurationManager.AppSettings["FTPUserName"],
                        ConfigurationManager.AppSettings["FTPPassword"]);


                FtpWebResponse response = (FtpWebResponse)request.GetResponse();
                //Console.WriteLine("Delete status: {0}", response.StatusDescription);
                response.Close();

                return true;
            }
        }

        public class ZipFileBackup
        {
            public void ZipBackup(string backupFileName, string backupZipFileName)
            {
                _logger.Info("Zip Backup - Inicio");

                FileInfo fileToCompress = new FileInfo(backupFileName);

                using (FileStream originalFileStream = fileToCompress.OpenRead())
                {
                    if ((File.GetAttributes(fileToCompress.FullName) &
                       FileAttributes.Hidden) != FileAttributes.Hidden & fileToCompress.Extension != ".gz")
                    {
                        using (FileStream compressedFileStream = File.Create(backupZipFileName))
                        {
                            using (GZipStream compressionStream = new GZipStream(compressedFileStream,
                               CompressionMode.Compress))
                            {
                                originalFileStream.CopyTo(compressionStream);

                            }
                        }
                    }
                }

                _logger.Info("Zip Backup - Fim");
            }
        }

        public class DirectoryBackupCleanup
        {

            /// <summary>
            /// Limpa todos os arquivos anterios a execução do backup
            /// </summary>
            /// <param name="backupDirectory"></param>
            /// <param name="dataAtualNome"></param>
            public void DirectoryCleanupOlder(string backupDirectory, string dataAtualNome )
            {
                try
                {
                    _logger.Info("Limpeza Diretorio Antigos - Inicio");

                    var Dir = new DirectoryInfo(backupDirectory);
                    // Busca automaticamente todos os arquivos em todos os subdiretórios
                    FileInfo[] Files = Dir.GetFiles("*", SearchOption.AllDirectories);
                    foreach (FileInfo file in Files)
                    {
                        if (!file.Name.Contains(dataAtualNome))
                            file.Delete();
                    }

                    
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Erro na limpeza de diretório local");
                }
                finally
                {
                    _logger.Info("Limpeza Diretorio Antigos - FIm");
                }


            }

            public void DirectoryCleanupBeforeBackup(string backupDirectory, string dataAtualNome)
            {
                try
                {
                    _logger.Info("Limpeza de diretório - Inicio");

                    var Dir = new DirectoryInfo(backupDirectory);
                    // Busca automaticamente todos os arquivos em todos os subdiretórios
                    FileInfo[] Files = Dir.GetFiles("*", SearchOption.AllDirectories);
                    foreach (FileInfo file in Files)
                    {
                        if (file.Name.Contains(dataAtualNome))
                            file.Delete();
                    }

                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Erro na limpeza de diretório do ftp");
                }
                finally
                {
                    _logger.Info("Limpeza de diretório - Fim");

                }
                
            }
        }
        
    }
}
