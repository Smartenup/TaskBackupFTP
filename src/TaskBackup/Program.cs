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

            _logger.Info("Backup Application - Begin");

            try
            {
                var settings = new BackupSettings();
                                
                var directoryBackupCleanup = new DirectoryCleanup();
                directoryBackupCleanup.DeleteFileAlreadyExists(settings.BackupDirectory, settings.CurrentDateName);
                
                new BackupSQLDatabase().BackupDatabase(settings.BackupDatabase, settings.BackupDatabaseName,
                    settings.BackupDatabaseDescription, settings.BackupFileNameFull);
                
                new ZipFile().Compress(settings.BackupFileNameFull, settings.BackupZipFileName);

                var ftpTransfBackup = new FTPTransf();
                ftpTransfBackup.Transf(settings.BackupZipFileName);
                
                directoryBackupCleanup.DirectoryCleanupOlder(settings.BackupDirectory, settings.CurrentDateName);

                ftpTransfBackup.CleanupBackupOlder(settings.BackupFileName, settings.CurrentDateName + ".gz");

            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "The application didn't end properly");
            }
            finally
            {
                _logger.Info("Backup Application - End");
            }

        }


        public class BackupSettings
        {
            public string BackupDirectory { get; private set; }
            public string BackupFileName { get; private set; }

            public string BackupDatabase { get; private set; }

            public string BackupDatabaseName { get; private set; }

            public string BackupDatabaseDescription {  get; private set; }

            public string CurrentDateName { get; private set; }

            public string BackupFileNameFull { get; private set; }

            public string BackupZipFileName { get; private set; }

            

            public BackupSettings()
            {
                BackupDirectory = ConfigurationManager.AppSettings["BackupDirectory"];
                BackupFileName = ConfigurationManager.AppSettings["BackupFileName"];
                BackupDatabase = ConfigurationManager.AppSettings["BackupDatabase"];
                BackupDatabaseName = ConfigurationManager.AppSettings["BackupDatabaseName"];
                BackupDatabaseDescription = ConfigurationManager.AppSettings["BackupDatabaseDescription"];

                CurrentDateName = DateTime.Today.ToString("yyyyMMdd");
                BackupFileNameFull = BackupDirectory + BackupFileName + CurrentDateName + ".bak";
                BackupZipFileName = BackupDirectory + BackupFileName + CurrentDateName + ".gz";
            }
        }

        public class BackupSQLDatabase
        {
            public void BackupDatabase(string databaseName, string backupName, string backupDescription, string backupFilename)
            {
                _logger.Info("Backup - Begin");

                var connectionStringProd = ConfigurationManager.ConnectionStrings["DataBase"].ConnectionString;

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

                _logger.Info("Backup - End");
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

        public class FTPTransf
        {
            public void Transf(string fileName)
            {

                _logger.Info("Transf FTP - Zip Backup - Begin");

                FileStream fs = null;
                Stream rs = null;

                try
                {
                    string file = fileName;
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

                _logger.Info("Transf FTP - Zip Backup - End");

            }


            public void CleanupBackupOlder(string nameBackup, string nameLastBackup)
            {

                _logger.Info("Cleanup FTP - Begin");

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
                    if (arquivo.Contains(nameBackup))
                    {
                        if (!arquivo.Contains(nameLastBackup))
                        {
                            DeleteFileOnServer(arquivo);
                        }
                    }
                }

                _logger.Info("Cleanup FTP - End");
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
               
                response.Close();

                return true;
            }
        }

        public class ZipFile
        {
            public void Compress(string fileName, string zipFileName)
            {
                _logger.Info("Zip - Begin");

                FileInfo fileToCompress = new FileInfo(fileName);

                using (FileStream originalFileStream = fileToCompress.OpenRead())
                {
                    if ((File.GetAttributes(fileToCompress.FullName) &
                       FileAttributes.Hidden) != FileAttributes.Hidden & fileToCompress.Extension != ".gz")
                    {
                        using (FileStream compressedFileStream = File.Create(zipFileName))
                        {
                            using (GZipStream compressionStream = new GZipStream(compressedFileStream,
                               CompressionMode.Compress))
                            {
                                originalFileStream.CopyTo(compressionStream);

                            }
                        }
                    }
                }

                _logger.Info("Zip - End");
            }
        }

        public class DirectoryCleanup
        {

            /// <summary>
            /// Cleaning all files before the current execution
            /// </summary>
            /// <param name="directory"></param>
            /// <param name="currentName">The file that cannot be deleted</param>
            public void DirectoryCleanupOlder(string directory, string currentName )
            {
                try
                {
                    _logger.Info("Cleaning Older Files - Begin");

                    var Dir = new DirectoryInfo(directory);
                    
                    FileInfo[] Files = Dir.GetFiles("*", SearchOption.AllDirectories);
                    foreach (FileInfo file in Files)
                    {
                        if (!file.Name.Contains(currentName))
                            file.Delete();
                    }

                    
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error cleaning local folder");
                }
                finally
                {
                    _logger.Info("Cleaning Older Files - End");
                }


            }

            /// <summary>
            /// Delete the file that already exists
            /// </summary>
            /// <param name="directory"></param>
            /// <param name="currentName">the file that will be delete</param>
            public void DeleteFileAlreadyExists(string directory, string currentName)
            {
                try
                {
                    _logger.Info("Delete file already exists - Begin");

                    var Dir = new DirectoryInfo(directory);
                  
                    FileInfo[] Files = Dir.GetFiles("*", SearchOption.AllDirectories);
                    foreach (FileInfo file in Files)
                    {
                        if (file.Name.Contains(currentName))
                            file.Delete();
                    }

                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Delete file already exists");
                }
                finally
                {
                    _logger.Info("Delete file already exists - End");
                }

            }
        }
        
    }
}
