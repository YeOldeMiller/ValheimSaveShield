﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Net;
using System.Windows.Documents;
using ModernWpf;
using System.Timers;

namespace ValheimSaveShield
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        //private static string defaultBackupFolder = LocalLow + "\\IronGate\\Valheim\\backups";
        private static string defaultBackupFolder = $@"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\AppData\LocalLow\IronGate\Valheim\backups";
        private static string backupDirPath;
        //private static string defaultSaveFolder = LocalLow + "\\IronGate\\Valheim";
        private static string defaultSaveFolder = $@"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\AppData\LocalLow\IronGate\Valheim";
        private static string saveDirPath;
        private List<SaveBackup> listBackups;
        private Boolean suppressLog;
        private Color defaultTextColor;
        private FileSystemWatcher worldWatcher;
        private FileSystemWatcher charWatcher;

        private Dictionary<string, SaveTimer> saveTimers;
        //private System.Timers.Timer saveTimer;
        private DateTime lastUpdateCheck;
        //private SaveFile charSaveForBackup;
        private System.Windows.Forms.NotifyIcon notifyIcon;
        private WindowState storedWindowState;

        private Thread ftpDirectorySync = null;

        public enum LogType
        {
            Normal,
            Success,
            Error
        }

        private bool IsBackupCurrent {
            get {
                if (!Directory.Exists($@"{saveDirPath}\worlds"))
                {
                    return false;
                }
                var worlds = Directory.GetFiles($@"{saveDirPath}\worlds", "*.db");
                foreach (string world in worlds)
                {
                    SaveFile save = new SaveFile(world);
                    if (!save.BackedUp)
                    {
                        return false;
                    }
                }
                var characters = Directory.GetFiles($@"{saveDirPath}\characters", "*.fch");
                foreach (string character in characters)
                {
                    SaveFile save = new SaveFile(character);
                    if (!save.BackedUp)
                    {
                        return false;
                    }
                }
                return true;
            }
            set
            {
                if (value)
                {
                    lblStatus.ToolTip = "Backed Up";
                    lblStatus.Content = FindResource("StatusOK");
                    btnBackup.IsEnabled = false;
                    btnBackup.Content = FindResource("SaveGrey");
                }
                else
                {
                    lblStatus.ToolTip = "Not Backed Up";
                    lblStatus.Content = FindResource("StatusNo");
                    btnBackup.IsEnabled = true;
                    btnBackup.Content = FindResource("Save");
                }
            }
        }

        ~MainWindow()
        {
            if (ftpDirectorySync != null)
            {
                ftpDirectorySync.Abort();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            suppressLog = false;
            if (Properties.Settings.Default.CreateLogFile)
            {
                System.IO.File.WriteAllText("log.txt", "");
            }
            defaultTextColor = ((SolidColorBrush)txtLog.Foreground).Color;
            txtLog.IsReadOnly = true;
            txtLog.Document.Blocks.Clear();
            logMessage($"Version {typeof(MainWindow).Assembly.GetName().Version}");
            if (Properties.Settings.Default.UpgradeRequired)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpgradeRequired = false;
                Properties.Settings.Default.Save();
            }

            if (Properties.Settings.Default.SaveFolder.Length == 0)
            {
                logMessage("Save folder not set; reverting to default.");
                Properties.Settings.Default.SaveFolder = defaultSaveFolder;
                Properties.Settings.Default.Save();
            }
            else if (!Directory.Exists(Properties.Settings.Default.SaveFolder) && !Properties.Settings.Default.SaveFolder.Equals(defaultSaveFolder))
            {
                logMessage("Save folder (" + Properties.Settings.Default.SaveFolder + ") not found; reverting to default.");
                Properties.Settings.Default.SaveFolder = defaultSaveFolder;
                Properties.Settings.Default.Save();
            }
            if (Properties.Settings.Default.BackupFolder.Length == 0)
            {
                logMessage("Backup folder not set; reverting to default.");
                Properties.Settings.Default.BackupFolder = defaultBackupFolder;
                Properties.Settings.Default.Save();
            }
            else if (!Directory.Exists(Properties.Settings.Default.BackupFolder) && !Properties.Settings.Default.BackupFolder.Equals(defaultBackupFolder))
            {
                logMessage($"Backup folder {Properties.Settings.Default.BackupFolder}) not found; reverting to default.");
                Properties.Settings.Default.BackupFolder = defaultBackupFolder;
                Properties.Settings.Default.Save();
            }

            saveDirPath = Properties.Settings.Default.SaveFolder;
            txtSaveFolder.Text = saveDirPath;
            backupDirPath = Properties.Settings.Default.BackupFolder;
            txtBackupFolder.Text = backupDirPath;

            txtFtpImport.Text = "ftp://" + Properties.Settings.Default.FtpIpAddress + ":" + Properties.Settings.Default.FtpPort + "/" + Properties.Settings.Default.FtpFilePath;

            // start the directory syncing if user has the correct settings for it
            syncDirectoriesAsync();

            chkCreateLogFile.IsChecked = Properties.Settings.Default.CreateLogFile;

            saveTimers = new Dictionary<string, SaveTimer>();
            /*saveTimer = new System.Timers.Timer();
            saveTimer.Interval = 2000;
            saveTimer.AutoReset = false;
            saveTimer.Elapsed += OnSaveTimerElapsed;*/

            worldWatcher = new FileSystemWatcher();
            if (Directory.Exists($@"{saveDirPath}\worlds"))
            {
                worldWatcher.Path = $@"{saveDirPath}\worlds";
            } else
            {
                logMessage($@"Folder {saveDirPath}\worlds does not exist. Please set the correct location of your save files.", LogType.Error);
            }

            // Watch for changes in LastWrite times.
            worldWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName;

            // Only watch .db files.
            worldWatcher.Filter = "*.db";

            // Add event handlers.
            worldWatcher.Changed += OnSaveFileChanged;
            worldWatcher.Created += OnSaveFileChanged;
            worldWatcher.Renamed += OnSaveFileChanged;

            charWatcher = new FileSystemWatcher();
            if (Directory.Exists($@"{saveDirPath}\characters"))
            {
                charWatcher.Path = $@"{saveDirPath}\characters";
            }
            else
            {
                Directory.CreateDirectory($@"{saveDirPath}\characters");
                //logMessage($@"Folder {saveDirPath}\characters does not exist. Please set the correct location of your save files.", LogType.Error);
            }

            // Watch for changes in LastWrite and file creation times.
            charWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName;

            // Only watch .db files.
            charWatcher.Filter = "*.fch";

            // Add event handlers.
            charWatcher.Changed += OnSaveFileChanged;
            charWatcher.Created += OnSaveFileChanged;
            charWatcher.Renamed += OnSaveFileChanged;

            listBackups = new List<SaveBackup>();

            ((MenuItem)dataBackups.ContextMenu.Items[0]).Click += deleteMenuItem_Click;

            dataBackups.CanUserDeleteRows = false;

            notifyIcon = new System.Windows.Forms.NotifyIcon();
            notifyIcon.BalloonTipText = "VSS has been minimized. Click the tray icon to restore.";
            notifyIcon.BalloonTipClicked += NotifyIcon_Click;
            notifyIcon.Text = "Valheim Save Shield";
            this.notifyIcon.Icon = ValheimSaveShield.Properties.Resources.vss;
            notifyIcon.Click += NotifyIcon_Click;
            storedWindowState = WindowState.Normal;
        }

        private void NotifyIcon_Click(object sender, EventArgs e)
        {
            Show();
            WindowState = storedWindowState;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtLog.IsReadOnly = true;
            //logMessage("Current save date: " + File.GetLastWriteTime(saveDirPath + "\\profile.sav").ToString());
            //logMessage("Backups folder: " + backupDirPath);
            //logMessage("Save folder: " + saveDirPath);
            loadBackups();
            bool autoBackup = Properties.Settings.Default.AutoBackup;
            chkAutoBackup.IsChecked = autoBackup;
            txtBackupMins.Text = Properties.Settings.Default.BackupMinutes.ToString();
            txtBackupLimit.Text = Properties.Settings.Default.BackupLimit.ToString();
            chkAutoCheckUpdate.IsChecked = Properties.Settings.Default.AutoCheckUpdate;

            if (!worldWatcher.Path.Equals(""))
            {
                worldWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
            }
            if (!charWatcher.Path.Equals(""))
            {
                charWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
            }

            if (Properties.Settings.Default.AutoCheckUpdate)
            {
                checkForUpdate();
            }
            if (IsBackupCurrent) IsBackupCurrent = true;
        }

        private void loadBackups()
        {
            try
            {
                if (!Directory.Exists(backupDirPath))
                {
                    logMessage("Backups folder not found, creating...");
                    Directory.CreateDirectory(backupDirPath);
                    Directory.CreateDirectory($@"{backupDirPath}\worlds");
                    Directory.CreateDirectory($@"{backupDirPath}\characters");
                }
                else
                {
                    if (!Directory.Exists($@"{backupDirPath}\worlds"))
                    {
                        Directory.CreateDirectory($@"{backupDirPath}\worlds");
                    }
                    if (!Directory.Exists($@"{backupDirPath}\characters"))
                    {
                        Directory.CreateDirectory($@"{backupDirPath}\characters");
                    }
                }

                dataBackups.ItemsSource = null;
                listBackups.Clear();
                Dictionary<long, string> backupWorldNames = getBackupNames("World");
                Dictionary<long, bool> backupWorldKeeps = getBackupKeeps("World");
                string[] worldBackups = Directory.GetDirectories(backupDirPath + "\\worlds");
                foreach (string w in worldBackups)
                {
                    string name = w.Replace($@"{backupDirPath}\worlds", "");
                    string[] backupDirs = Directory.GetDirectories(w);
                    foreach (string backupDir in backupDirs)
                    {
                        SaveBackup backup = new SaveBackup($@"{backupDir}\{name}.db");
                        if (backupWorldNames.ContainsKey(backup.SaveDate.Ticks))
                        {
                            backup.Label = backupWorldNames[backup.SaveDate.Ticks];
                        }
                        if (backupWorldKeeps.ContainsKey(backup.SaveDate.Ticks))
                        {
                            backup.Keep = backupWorldKeeps[backup.SaveDate.Ticks];
                        }

                        backup.Updated += saveUpdated;

                        listBackups.Add(backup);
                    }
                }

                Dictionary<long, string> backupCharNames = getBackupNames("Character");
                Dictionary<long, bool> backupCharKeeps = getBackupKeeps("Character");
                string[] charBackups = Directory.GetDirectories($@"{backupDirPath}\characters");
                foreach (string c in charBackups)
                {
                    string name = c.Replace($@"{backupDirPath}\characters", "");
                    string[] backupDirs = Directory.GetDirectories(c);
                    foreach (string backupDir in backupDirs)
                    {
                        SaveBackup backup = new SaveBackup($@"{backupDir}\{name}.fch");
                        if (backupCharNames.ContainsKey(backup.SaveDate.Ticks))
                        {
                            backup.Label = backupCharNames[backup.SaveDate.Ticks];
                        }
                        if (backupCharKeeps.ContainsKey(backup.SaveDate.Ticks))
                        {
                            backup.Keep = backupCharKeeps[backup.SaveDate.Ticks];
                        }

                        backup.Updated += saveUpdated;

                        listBackups.Add(backup);
                    }
                }
                listBackups.Sort();
                dataBackups.ItemsSource = listBackups;
                if (listBackups.Count > 0)
                {
                    //logMessage("Last backup save date: " + listBackups[listBackups.Count - 1].SaveDate.ToString());
                }
            }
            catch (Exception ex)
            {
                logMessage($"Error loading backups: {ex.Message}", LogType.Error);
            }
        }

        private void saveUpdated(object sender, UpdatedEventArgs args)
        {
            if (args.FieldName.Equals("Label"))
            {
                updateSavedLabels();
            }
            else if (args.FieldName.Equals("Keep"))
            {
                updateSavedKeeps();
            }
        }

        private void loadBackups(Boolean verbose)
        {
            Boolean oldVal = suppressLog;
            suppressLog = !verbose;
            loadBackups();
            suppressLog = oldVal;
        }

        public void logMessage(string msg)
        {
            logMessage(msg, defaultTextColor);
        }

        public void logMessage(string msg, LogType lt)
        {
            //Color color = Colors.White;
            Color color = defaultTextColor;
            if (lt == LogType.Success)
            {
                color = Color.FromRgb(50, 200, 50);
            }
            else if (lt == LogType.Error)
            {
                color = Color.FromRgb(200, 50, 50);
            }
            logMessage(msg, color);
        }

        public void logMessage(string msg, Color color)
        {
            if (!suppressLog)
            {
                //txtLog.Text = txtLog.Text + Environment.NewLine + DateTime.Now.ToString() + ": " + msg;
                Run run = new Run(DateTime.Now.ToString() + ": " + msg);
                run.Foreground = new SolidColorBrush(color);
                Paragraph paragraph = new Paragraph(run);
                paragraph.Margin = new Thickness(0);
                txtLog.Document.Blocks.Add(paragraph);
                if (msg.Contains("\n"))
                {
                    lblLastMessage.Content = msg.Split('\n')[0];
                } else
                {
                    lblLastMessage.Content = msg;
                }
                lblLastMessage.Foreground = new SolidColorBrush(color);
                if (color.Equals(defaultTextColor))
                {
                    lblLastMessage.FontWeight = FontWeights.Normal;
                }
                else
                {
                    lblLastMessage.FontWeight = FontWeights.Bold;
                }
            }
            if (Properties.Settings.Default.CreateLogFile)
            {
                StreamWriter writer = System.IO.File.AppendText("log.txt");
                writer.WriteLine(DateTime.Now.ToString() + ": " + msg);
                writer.Close();
            }
        }

        private void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            string[] worlds = Directory.GetFiles($@"{saveDirPath}\worlds", "*.db");
            foreach (string save in worlds)
            {
                doBackup(save);
            }
            if (!Directory.Exists($@"{saveDirPath}\characters"))
            {
                Directory.CreateDirectory($@"{saveDirPath}\characters");
            }
            string[] characters = Directory.GetFiles($@"{saveDirPath}\characters", "*.fch");
            foreach (string save in characters)
            {
                doBackup(save);
            }
            this.IsBackupCurrent = this.IsBackupCurrent;
            //doBackup();
        }

        private void doBackup(string savepath)
        {
            SaveFile save = new SaveFile(savepath);
            if (!save.BackedUp)
            {
                try
                {
                    SaveBackup backup = save.PerformBackup();
                    if (backup != null)
                    {
                        listBackups.Add(backup);
                        checkBackupLimits();
                        dataBackups.Items.Refresh();
                        this.IsBackupCurrent = this.IsBackupCurrent;
                        logMessage($"Backup of {backup.Type.ToLower()} {backup.Name} completed!", LogType.Success);
                    }
                    else
                    {
                        logMessage($"Backup of {save.Type.ToLower()} {save.Name} failed!", LogType.Error);
                    }
                }
                catch (Exception ex)
                {
                    logMessage($"Error attempting backup of {savepath}: {ex.Message}");
                }
            }
        }

        private Boolean isValheimRunning()
        {
            Process[] pname = Process.GetProcessesByName("valheim");
            return pname.Length > 0;
        }
        private Boolean isValheimServerRunning()
        {
            Process[] pname = Process.GetProcessesByName("valheim_server");
            return pname.Length > 0;
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (isValheimRunning())
            {
                logMessage("Exit the game before restoring a save backup.", LogType.Error);
                return;
            }

            if (dataBackups.SelectedItem == null)
            {
                logMessage("Choose a backup to restore from the list!", LogType.Error);
                return;
            }
            SaveBackup selectedBackup = (SaveBackup)dataBackups.SelectedItem;
            if (selectedBackup.Type.Equals("World") && isValheimServerRunning())
            {
                logMessage("Stop the game server before restoring a world backup.", LogType.Error);
                return;
            }
            if (selectedBackup.Active)
            {
                logMessage("That backup is already active. No need to restore.");
                return;
            }
            if (File.Exists(selectedBackup.ActivePath))
            {
                //check if active save is backed up
                SaveFile save = new SaveFile(selectedBackup.ActivePath);
                if (!save.BackedUp)
                {
                    doBackup(save.FullPath);
                }
            }
            worldWatcher.EnableRaisingEvents = false;
            charWatcher.EnableRaisingEvents = false;
            //File.Copy(selectedBackup.FullPath, selectedBackup.ActivePath);
            selectedBackup.Restore();
            dataBackups.Items.Refresh();
            btnRestore.IsEnabled = false;
            btnRestore.Content = FindResource("RestoreGrey");
            logMessage(selectedBackup.Name+" backup restored!", LogType.Success);
            worldWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
            charWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
        }

        private void ChkAutoBackup_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.AutoBackup = chkAutoBackup.IsChecked.HasValue ? chkAutoBackup.IsChecked.Value : false;
            Properties.Settings.Default.Save();
            worldWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
            charWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
        }

        private void OnSaveFileChanged(object source, FileSystemEventArgs e)
        {
            // Specify what is done when a file is changed, created, or deleted.
            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    if (e.FullPath.EndsWith(".old")) return;
                    if (Properties.Settings.Default.AutoBackup)
                    {
                        SaveFile save = new SaveFile(e.FullPath);
                        if (!saveTimers.ContainsKey(e.FullPath))
                        {
                            var saveTimer = new SaveTimer(save);
                            saveTimer.Interval = 1000;
                            saveTimer.AutoReset = false;
                            saveTimer.Elapsed += OnSaveTimerElapsed;
                            saveTimer.Enabled = true;
                            saveTimers.Add(e.FullPath, saveTimer);
                        }
                        else
                        {
                            saveTimers[e.FullPath].Interval = 1000;
                        }
                    }
                    else
                    {
                        
                    }
                }
                catch (Exception ex)
                {
                    logMessage($"{ex.GetType()} setting save file timer: {ex.Message}({ex.StackTrace})");
                }
            });
        }

        private void OnSaveTimerElapsed(Object source, ElapsedEventArgs e)
        {
            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    var timer = (SaveTimer)source;
                    if (timer.Save.NeedsBackedUp)
                    //if (charSaveForBackup != null && charSaveForBackup.NeedsBackedUp)
                    {
                        doBackup(timer.Save.FullPath);
                    }
                    else
                    {
                        this.IsBackupCurrent = false;
                        dataBackups.Items.Refresh();
                        TimeSpan span = (timer.Save.BackupDueTime - DateTime.Now);
                        logMessage($"Save change detected, but {span.Minutes + Math.Round(span.Seconds / 60.0, 2)} minutes left until next backup is due.");
                    }
                    //var timer = saveTimers[e.Save.FullPath];
                    saveTimers.Remove(timer.Save.FullPath);
                    timer.Dispose();
                }
                catch (Exception ex)
                {
                    logMessage($"{ex.GetType()} processing save file change: {ex.Message} ({ex.StackTrace})");
                }
            });
        }

        private void TxtBackupMins_LostFocus(object sender, RoutedEventArgs e)
        {
            updateBackupMins();
        }

        private void TxtBackupMins_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                updateBackupMins();
            }
        }

        private void updateBackupMins()
        {
            string txt = txtBackupMins.Text;
            int mins;
            bool valid = false;
            if (txt.Length > 0)
            {
                if (int.TryParse(txt, out mins))
                {
                    valid = true;
                }
                else
                {
                    mins = Properties.Settings.Default.BackupMinutes;
                }
            }
            else
            {
                mins = Properties.Settings.Default.BackupMinutes;
            }
            if (mins != Properties.Settings.Default.BackupMinutes)
            {
                Properties.Settings.Default.BackupMinutes = mins;
                Properties.Settings.Default.Save();
            }
            if (!valid)
            {
                txtBackupMins.Text = Properties.Settings.Default.BackupMinutes.ToString();
            }
        }

        private void TxtBackupLimit_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                updateBackupLimit();
            }
        }

        private void TxtBackupLimit_LostFocus(object sender, RoutedEventArgs e)
        {
            updateBackupLimit();
        }

        private void updateBackupLimit()
        {
            string txt = txtBackupLimit.Text;
            int num;
            bool valid = false;
            if (txt.Length > 0)
            {
                if (int.TryParse(txt, out num))
                {
                    valid = true;
                }
                else
                {
                    num = Properties.Settings.Default.BackupLimit;
                }
            }
            else
            {
                num = 0;
            }
            if (num != Properties.Settings.Default.BackupLimit)
            {
                Properties.Settings.Default.BackupLimit = num;
                Properties.Settings.Default.Save();
            }
            if (!valid)
            {
                txtBackupLimit.Text = Properties.Settings.Default.BackupLimit.ToString();
            }
        }

        private void checkBackupLimits()
        {
            if (Properties.Settings.Default.BackupLimit > 0)
            {
                listBackups.Sort();
                Dictionary<string, Dictionary<string, List<SaveBackup>>> backups = new Dictionary<string, Dictionary<string, List<SaveBackup>>>();
                foreach (SaveBackup backup in listBackups)
                {
                    if (!backups.ContainsKey(backup.Type))
                    {
                        backups.Add(backup.Type, new Dictionary<string, List<SaveBackup>>());
                    }
                    if (!backups[backup.Type].ContainsKey(backup.Name))
                    {
                        backups[backup.Type].Add(backup.Name, new List<SaveBackup>());
                    }
                    backups[backup.Type][backup.Name].Add(backup);
                }
                List<SaveBackup> removeBackups = new List<SaveBackup>();
                foreach (string backupType in backups.Keys)
                {
                    foreach (string saveName in backups[backupType].Keys)
                    {
                        if (backups[backupType][saveName].Count > Properties.Settings.Default.BackupLimit)
                        {
                            int delNum = backups[backupType][saveName].Count - Properties.Settings.Default.BackupLimit;
                            for (int i = 0; i < backups[backupType][saveName].Count && delNum > 0; i++)
                            {
                                SaveBackup backup = backups[backupType][saveName][i];
                                if (!backup.Keep && !backup.Active)
                                {
                                    logMessage($"Deleting excess backup {backup.Label} ({backup.SaveDate})");
                                    Directory.Delete(backup.Folder, true);
                                    removeBackups.Add(backup);
                                    delNum--;
                                }
                            }
                        }
                    }
                }

                for (int i=0; i < removeBackups.Count; i++)
                {
                    listBackups.Remove(removeBackups[i]);
                }
            }
        }

        private Dictionary<long, string> getBackupNames(string type)
        {
            Dictionary<long, string> names = new Dictionary<long, string>();
            string savedString = "";
            if (type.Equals("World")) {
                savedString = Properties.Settings.Default.WorldBackupLabel;
            } else
            {
                savedString = Properties.Settings.Default.CharBackupLabel;
            }
            string[] savedNames = savedString.Split(',');
            for (int i = 0; i < savedNames.Length; i++)
            {
                string[] vals = savedNames[i].Split('=');
                if (vals.Length == 2)
                {
                    names.Add(long.Parse(vals[0]), System.Net.WebUtility.UrlDecode(vals[1]));
                }
            }
            return names;
        }

        private Dictionary<long, bool> getBackupKeeps(string type)
        {
            Dictionary<long, bool> keeps = new Dictionary<long, bool>();
            string savedString = "";
            if (type.Equals("World")) {
                savedString = Properties.Settings.Default.WorldBackupKeep;
            } else
            {
                savedString = Properties.Settings.Default.CharBackupKeep;
            }
            string[] savedKeeps = savedString.Split(',');
            for (int i = 0; i < savedKeeps.Length; i++)
            {
                string[] vals = savedKeeps[i].Split('=');
                if (vals.Length == 2)
                {
                    keeps.Add(long.Parse(vals[0]), bool.Parse(vals[1]));
                }
            }
            return keeps;
        }

        private void DataBackups_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Column.Header.ToString().Equals("Name") || 
                e.Column.Header.ToString().Equals("Type") ||
                e.Column.Header.ToString().Equals("SaveDate") ||
                e.Column.Header.ToString().Equals("Active")) e.Cancel = true;
        }

        private void DataBackups_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Column.Header.ToString().Equals("Name") && e.EditAction == DataGridEditAction.Commit)
            {
                SaveBackup sb = (SaveBackup)e.Row.Item;
                if (sb.Label.Equals(""))
                {
                    sb.Label = sb.SaveDate.Ticks.ToString();
                }
            }
        }

        private void updateSavedLabels()
        {
            List<string> savedWorldLabels = new List<string>();
            List<string> savedCharLabels = new List<string>();
            for (int i = 0; i < listBackups.Count; i++)
            {
                SaveBackup s = listBackups[i];
                if (!s.Label.Equals(s.DefaultLabel))
                {
                    if (s.Type.Equals("World"))
                    {
                        savedWorldLabels.Add(s.SaveDate.Ticks + "=" + System.Net.WebUtility.UrlEncode(s.Label));
                    }
                    else
                    {
                        savedCharLabels.Add(s.SaveDate.Ticks + "=" + System.Net.WebUtility.UrlEncode(s.Label));
                    }
                }
                else
                {
                }
            }
            if (savedWorldLabels.Count > 0)
            {
                Properties.Settings.Default.WorldBackupLabel = string.Join(",", savedWorldLabels.ToArray());
            }
            else
            {
                Properties.Settings.Default.WorldBackupLabel = "";
            }
            if (savedCharLabels.Count > 0)
            {
                Properties.Settings.Default.CharBackupLabel = string.Join(",", savedCharLabels.ToArray());
            }
            else
            {
                Properties.Settings.Default.CharBackupLabel = "";
            }
            Properties.Settings.Default.Save();
        }

        private void updateSavedKeeps()
        {
            List<string> savedWorldKeeps = new List<string>();
            List<string> savedCharKeeps = new List<string>();
            for (int i = 0; i < listBackups.Count; i++)
            {
                SaveBackup s = listBackups[i];
                if (s.Keep)
                {
                    if (s.Type.Equals("World"))
                    {
                        savedWorldKeeps.Add(s.SaveDate.Ticks + "=True");
                    } else
                    {
                        savedCharKeeps.Add(s.SaveDate.Ticks + "=True");
                    }
                }
            }
            if (savedWorldKeeps.Count > 0)
            {
                Properties.Settings.Default.WorldBackupKeep = string.Join(",", savedWorldKeeps.ToArray());
            }
            else
            {
                Properties.Settings.Default.WorldBackupKeep = "";
            }
            if (savedCharKeeps.Count > 0)
            {
                Properties.Settings.Default.CharBackupKeep = string.Join(",", savedCharKeeps.ToArray());
            }
            else
            {
                Properties.Settings.Default.CharBackupKeep = "";
            }
            Properties.Settings.Default.Save();
        }

        private void DataBackups_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MenuItem deleteMenu = ((MenuItem)dataBackups.ContextMenu.Items[0]);
            if (e.AddedItems.Count > 0)
            {
                SaveBackup selectedBackup = (SaveBackup)(dataBackups.SelectedItem);
                if (selectedBackup.Active)
                {
                    btnRestore.IsEnabled = false;
                    btnRestore.Content = FindResource("RestoreGrey");
                }
                else
                {
                    btnRestore.IsEnabled = true;
                    btnRestore.Content = FindResource("Restore");
                }

                deleteMenu.IsEnabled = true;
            }
            else
            {
                deleteMenu.IsEnabled = false;
                btnRestore.IsEnabled = false;
                btnRestore.Content = FindResource("RestoreGrey");
            }
        }

        private void deleteMenuItem_Click(object sender, System.EventArgs e)
        {
            SaveBackup save = (SaveBackup)dataBackups.SelectedItem;
            ModernMessageBox mmbConfirm = new ModernMessageBox(this);
            var confirmResult = mmbConfirm.Show($"Are you sure to delete backup \"{save.Label}\" ({save.SaveDate.ToString()})?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirmResult == MessageBoxResult.Yes)
            {
                if (save.Keep)
                {
                    mmbConfirm = new ModernMessageBox(this);
                    confirmResult = mmbConfirm.Show($"This backup is marked for keeping. Are you SURE to delete backup \"{save.Label}\" ({save.SaveDate.ToString()})?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (confirmResult != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
                if (save.Active)
                {
                    this.IsBackupCurrent = false;
                }
                if (Directory.Exists(save.Folder))
                {
                    Directory.Delete(save.Folder, true);
                }
                listBackups.Remove(save);
                dataBackups.Items.Refresh();
                logMessage($"Backup \"{save.Label}\" ({save.SaveDate}) deleted.");
            }
        }

        private void checkForUpdate()
        {
            new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;

                try
                {
                    WebClient client = new WebClient();
                    string source = client.DownloadString("https://github.com/Razzmatazzz/ValheimSaveShield/releases/latest");
                    string title = Regex.Match(source, @"\<title\b[^>]*\>\s*(?<Title>[\s\S]*?)\</title\>", RegexOptions.IgnoreCase).Groups["Title"].Value;
                    string remoteVer = Regex.Match(source, @"Valheim Save Shield (?<Version>([\d.]+)?)", RegexOptions.IgnoreCase).Groups["Version"].Value;

                    Version remoteVersion = new Version(remoteVer);
                    Version localVersion = typeof(MainWindow).Assembly.GetName().Version;

                    this.Dispatcher.Invoke(() =>
                    {
                        //do stuff in here with the interface
                        if (localVersion.CompareTo(remoteVersion) == -1)
                        {
                            ModernMessageBox mmbConfirm = new ModernMessageBox(this);
                            var confirmResult = mmbConfirm.Show("There is a new version available. Would you like to open the download page?", "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                            if (confirmResult == MessageBoxResult.Yes)
                            {
                                Process.Start("https://github.com/Razzmatazzz/ValheimSaveShield/releases/latest");
                                System.Environment.Exit(1);
                            }
                        } else
                        {
                            //logMessage("No new version found.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    this.Dispatcher.Invoke(() =>
                    {
                        logMessage($"Error checking for new version: {ex.Message}", LogType.Error);
                    });
                }
            }).Start();
            lastUpdateCheck = DateTime.Now;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            //any cleanup to do before exit
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            notifyIcon.Dispose();
            notifyIcon = null;
            if (ftpDirectorySync != null)
            {
                ftpDirectorySync.Abort();
                ftpDirectorySync = null;
            }
        }

        private void BtnBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog openFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
            openFolderDialog.SelectedPath = backupDirPath;
            openFolderDialog.Description = "Select the folder where you want your backups kept.";
            System.Windows.Forms.DialogResult result = openFolderDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                string folderName = openFolderDialog.SelectedPath;
                if (folderName.Equals(saveDirPath))
                {
                    ModernMessageBox mmbConfirm = new ModernMessageBox(this);
                    mmbConfirm.Show("Please select a folder other than the game's save folder.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);
                    return;
                }
                if (folderName.Equals(backupDirPath))
                {
                    return;
                }
                if (listBackups.Count > 0)
                {
                    ModernMessageBox mmbConfirm = new ModernMessageBox(this);
                    var confirmResult = mmbConfirm.Show("Do you want to move your backups to this new folder?", "Move Backups", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (confirmResult == MessageBoxResult.Yes)
                    {
                        CopyFolder(new DirectoryInfo(backupDirPath), new DirectoryInfo(folderName));
                        List<String> backupFolders = Directory.GetDirectories(backupDirPath).ToList();
                        foreach (string file in backupFolders)
                        {
                            /*string subFolderName = file.Substring(file.LastIndexOf(@"\"));
                            Directory.CreateDirectory(folderName + subFolderName);
                            Directory.SetCreationTime(folderName + subFolderName, Directory.GetCreationTime(file));
                            Directory.SetLastWriteTime(folderName + subFolderName, Directory.GetCreationTime(file));
                            foreach (string filename in Directory.GetFiles(file))
                            {
                                File.Copy(filename, filename.Replace(backupDirPath, folderName));
                            }*/
                            Directory.Delete(file, true);
                        }
                    }
                }
                txtBackupFolder.Text = folderName;
                backupDirPath = folderName;
                if (!Directory.Exists($@"{backupDirPath}\worlds"))
                {
                    Directory.CreateDirectory($@"{backupDirPath}\worlds");
                }
                if (!Directory.Exists($@"{backupDirPath}\characters"))
                {
                    Directory.CreateDirectory($@"{backupDirPath}\characters");
                }
                Properties.Settings.Default.BackupFolder = folderName;
                Properties.Settings.Default.Save();
                loadBackups();
            }
        }

        public static void CopyFolder(DirectoryInfo source, DirectoryInfo target)
        {
            foreach (DirectoryInfo dir in source.GetDirectories())
                CopyFolder(dir, target.CreateSubdirectory(dir.Name));
            foreach (FileInfo file in source.GetFiles())
                file.CopyTo(System.IO.Path.Combine(target.FullName, file.Name));
        }

        private void DataBackups_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.Column.Header.Equals("DefaultLabel")) {
                e.Cancel = true;
            } 
            else if (e.Column.Header.Equals("FileName"))
            {
                e.Cancel = true;
            }
            else if (e.Column.Header.Equals("FullPath"))
            {
                e.Cancel = true;
            }
            else if (e.Column.Header.Equals("Folder"))
            {
                e.Cancel = true;
            }
            else if (e.Column.Header.Equals("ActivePath"))
            {
                e.Cancel = true;
            }
            else if (e.Column.Header.Equals("SaveDate"))
            {
                //e.Column.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            }
        }

        private void btnAppUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (lastUpdateCheck.AddMinutes(10) < DateTime.Now)
            {
                checkForUpdate();
            }
            else
            {
                TimeSpan span = (lastUpdateCheck.AddMinutes(10) - DateTime.Now);
                logMessage($"Please wait {span.Minutes} minutes, {span.Seconds} seconds before checking for update.");
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            //need to call twice for some reason
            dataBackups.CancelEdit();
            dataBackups.CancelEdit();
        }

        private void chkCreateLogFile_Click(object sender, RoutedEventArgs e)
        {
            bool newValue = chkCreateLogFile.IsChecked.HasValue ? chkCreateLogFile.IsChecked.Value : false;
            if (newValue & !Properties.Settings.Default.CreateLogFile)
            {
                System.IO.File.WriteAllText("log.txt", DateTime.Now.ToString() + ": Version " + typeof(MainWindow).Assembly.GetName().Version + "\r\n");
            }
            Properties.Settings.Default.CreateLogFile = newValue;
            Properties.Settings.Default.Save();
        }

        private void chkAutoCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            bool newValue = chkAutoCheckUpdate.IsChecked.HasValue ? chkAutoCheckUpdate.IsChecked.Value : false;
            Properties.Settings.Default.AutoCheckUpdate = newValue;
            Properties.Settings.Default.Save();
        }

        private void btnSaveFolder_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog openFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
            openFolderDialog.SelectedPath = saveDirPath;
            openFolderDialog.Description = "Select where your Valheim saves are stored";
            System.Windows.Forms.DialogResult result = openFolderDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                string folderName = openFolderDialog.SelectedPath;
                if (folderName.Equals(backupDirPath))
                {
                    ModernMessageBox mmbWarn = new ModernMessageBox(this);
                    mmbWarn.Show("Please select a folder other than the backup folder.",
                                     "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);
                    return;
                }
                if (folderName.Equals(saveDirPath))
                {
                    return;
                }
                if (!Directory.Exists($@"{folderName}\worlds"))
                {
                    ModernMessageBox mmbWarn = new ModernMessageBox(this);
                    mmbWarn.Show("Please select the folder where your Valheim save files are located. This folder should contain both a \"worlds\" and a \"characters\" folder.",
                                     "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);
                    return;
                }
                if (!Directory.Exists($@"{folderName}\characters"))
                {
                    Directory.CreateDirectory($@"{folderName}\characters");
                }
                txtSaveFolder.Text = folderName;
                saveDirPath = folderName;
                worldWatcher.Path = $@"{saveDirPath}\worlds";
                charWatcher.Path = $@"{saveDirPath}\characters";
                worldWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
                charWatcher.EnableRaisingEvents = Properties.Settings.Default.AutoBackup;
                Properties.Settings.Default.SaveFolder = folderName;
                Properties.Settings.Default.Save();
            }
        }

        private void btnFtpImport_Click(object sender, RoutedEventArgs e)
        {
            FtpSettingsWindow ftpWin = new FtpSettingsWindow();
            ftpWin.Owner = this;
            if ((bool)ftpWin.ShowDialog())
            {
                txtFtpImport.Text = "ftp://" + Properties.Settings.Default.FtpIpAddress + ":" + Properties.Settings.Default.FtpPort + "/" + Properties.Settings.Default.FtpFilePath;
                if (ftpDirectorySync == null)
                {
                    syncDirectoriesAsync();
                }
            }
            // Only do something if user clicks OK
            /*if (GetFtpSettings())
            {
                if (ftpDirectorySync == null)
                {
                    syncDirectoriesAsync();
                }
            }*/
        }

        private void syncDirectoriesAsync()
        {
            
            // asynchronously sync local directory with ftp
            ftpDirectorySync = new Thread(() => {
                try
                {
                    while (ftpDirectorySync != null)
                    {
                        if (Properties.Settings.Default.FtpIpAddress.Length == 0
                            || Properties.Settings.Default.FtpPort.Length == 0
                            || Properties.Settings.Default.FtpFilePath.Length == 0
                            || Properties.Settings.Default.SaveFolder.Length == 0
                            || Properties.Settings.Default.FtpUsername.Length == 0
                            || Properties.Settings.Default.FtpPassword.Length == 0
                        )
                        {
                            System.Diagnostics.Debug.WriteLine("exiting sync thread");
                            ftpDirectorySync = null;
                            break;
                        }

                        System.Diagnostics.Debug.WriteLine("re-syncing");
                        int syncstatus = SynchronizeDirectories.remoteSync(
                            Properties.Settings.Default.FtpIpAddress,
                            Properties.Settings.Default.FtpPort,
                            '/' + Properties.Settings.Default.FtpFilePath,
                            Properties.Settings.Default.SaveFolder + "\\worlds",
                            Properties.Settings.Default.FtpUsername,
                            Properties.Settings.Default.FtpPassword
                        );
                        if (syncstatus == 0)
                        {
                            this.Dispatcher.Invoke(() =>
                            {
                                logMessage("Successfully synced world saves from FTP server.", LogType.Success);
                            });
                        }
                        else
                        {
                            this.Dispatcher.Invoke(() =>
                            {
                                logMessage($"Error syncing world saves from FTP server: {SynchronizeDirectories.LastError.Message}", LogType.Error);
                            });
                        }
                        Thread.Sleep(Properties.Settings.Default.BackupMinutes * 60000);
                    }
                }
                catch (Exception ex)
                {
                    this.Dispatcher.Invoke(() =>
                    {
                        logMessage($"Error checking FTP server: {ex.Message}", LogType.Error);
                    });
                }
            });

            try
            {
                if (ftpDirectorySync != null)
                {
                    ftpDirectorySync.Start();
                }
            } 
            catch (Exception ex)
            {
                logMessage($"Error starting FTP sync thread: {ex.Message}");
            }
        }
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                {
                    Hide();
                    if (notifyIcon != null)
                    {
                        if (Properties.Settings.Default.ShowMinimizeMessage)
                        {
                            notifyIcon.ShowBalloonTip(2000);
                            Properties.Settings.Default.ShowMinimizeMessage = false;
                            Properties.Settings.Default.Save();
                        }
                    }
                }
            }
            else
            {
                storedWindowState = WindowState;
            }
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            CheckTrayIcon();
        }
        void CheckTrayIcon()
        {
            ShowTrayIcon(!IsVisible);
        }
        void ShowTrayIcon(bool show)
        {
            if (notifyIcon != null)
            {
                notifyIcon.Visible = show;
            }
        }

        private void menuSavePathOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(saveDirPath))
            {
                logMessage("Save path not found, please select a valid path for your save files.");
                return;
            }
            Process.Start(saveDirPath + "\\");
        }

        private void menuBackupPathOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(backupDirPath))
            {
                logMessage("Backups folder not found, creating...");
                Directory.CreateDirectory(backupDirPath);
            }
            Process.Start(backupDirPath + "\\");
        }

        private void btnReportBug_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://github.com/Razzmatazzz/ValheimSaveShield/issues");
        }
    }
}