﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Security;
using System.Windows.Forms;
using dlech.PageantSharp;
using KeeAgent.Properties;
using KeeAgent.UI;
using KeePass.Plugins;
using KeePassLib;
using KeePassLib.Cryptography;
using KeePassLib.Security;
using KeePassLib.Utility;
using KeePass.UI;
using KeePass.App;
using System.IO;

namespace KeeAgent
{
  public sealed partial class KeeAgentExt : Plugin
  {
    internal IPluginHost pluginHost;
    internal Options options;
    internal bool debug;

    private WinPageant pageant;
    private ToolStripMenuItem keeAgentMenuItem;
    private UIHelper uiHelper;

    private const string pluginName = "KeeAgent";
    private const string notificationOptionName =
        pluginName + ".Notification";
    private const string logginEnabledOptionName
        = pluginName + ".LoggingEnabled";
    private const string logFileNameOptionName
        = pluginName + ".LogFileName";

    public override bool Initialize(IPluginHost host)
    {
      bool result;

      this.pluginHost = host;
      this.uiHelper = new UIHelper(this.pluginHost);
      this.debug = (this.pluginHost
          .CommandLineArgs[AppDefs.CommandLineOptions.Debug] != null);

      loadOptions();

      if (debug) Log("Loading KeeAgent...");

      try {
        // TODO check OS - currently only works on Windows
        this.pageant = new WinPageant(GetPpkKeyList, GetSSH2Key);
        if (debug) Log("Succeeded");
        result = true;
      } catch (Exception) {
        ShowPageantRunningErrorMessage();
        if (debug) Log("Failed");
        result = false;
      }

      AddMenuItems();

      return result;
    }

    public override void Terminate()
    {
      if (debug) Log("Terminating KeeAgent");
      if (this.pageant != null) {
        this.pageant.Dispose();
      }
      RemoveMenuItems();
    }

    public override Image SmallIcon
    {
      get { return Resources.KeeAgentIcon; }
    }

    private void ShowPageantRunningErrorMessage()
    {
      MessageService.ShowWarning(new object[] {
                Translatable.ErrPageantRunning
            });
    }

    private void AddMenuItems()
    {
      /* get Tools menu */
      ToolStripMenuItem toolsMenu = this.pluginHost.MainWindow.ToolsMenu;

      /* create parent menu item */
      keeAgentMenuItem = new ToolStripMenuItem();
      keeAgentMenuItem.Text = Translatable.KeeAgent;

      if (pageant != null) {
        /* create children menu items */
        ToolStripMenuItem keeAgentListPuttyKeysMenuItem =
            new ToolStripMenuItem();
        keeAgentListPuttyKeysMenuItem.Text =
            Translatable.ShowPuttyKeysMenuItem;
        keeAgentListPuttyKeysMenuItem.ToolTipText =
            Translatable.ShowPuttyKeysMenuItemToolTip;
        keeAgentListPuttyKeysMenuItem.Click +=
            new EventHandler(keeAgentListPuttyKeysMenuItem_Click);

        ToolStripMenuItem keeAgentOptionsMenuItem =
            new ToolStripMenuItem();
        keeAgentOptionsMenuItem.Text = Translatable.OptionsMenuItem;
        keeAgentOptionsMenuItem.Click +=
            new EventHandler(keeAgentOptionsMenuItem_Click);

        /* add children to parent */
        keeAgentMenuItem.DropDownItems
            .Add(keeAgentListPuttyKeysMenuItem);
        keeAgentMenuItem.DropDownItems.Add(keeAgentOptionsMenuItem);
      } else {
        keeAgentMenuItem.Enabled = false;
      }

      /* add new items to tools menu */
      toolsMenu.DropDownItems.Add(keeAgentMenuItem);

    }

    private void RemoveMenuItems()
    {
      if (this.pluginHost != null &&
          this.pluginHost.MainWindow != null &&
          this.keeAgentMenuItem != null) {

        /* get Tools menu */
        ToolStripMenuItem toolsMenu =
            this.pluginHost.MainWindow.ToolsMenu;
        /* remove items from tools menu */
        toolsMenu.DropDownItems.Remove(keeAgentMenuItem);
      }
    }

    private void keeAgentListPuttyKeysMenuItem_Click(
        object source, EventArgs e)
    {
      KeyListDialog dialog = new KeyListDialog(this);
      DialogResult result = dialog.ShowDialog(pluginHost.MainWindow);
      dialog.Dispose();
    }

    private void keeAgentOptionsMenuItem_Click(object source, EventArgs e)
    {
      OptionsDialog dialog = new OptionsDialog(this);
      DialogResult result = dialog.ShowDialog(pluginHost.MainWindow);
      dialog.Dispose();
    }

    internal IEnumerable<PpkKey> GetPpkKeyList()
    {
      return (IEnumerable<PpkKey>)GetKeeAgentKeyList(true);
    }

    internal IEnumerable<KeeAgentKey> GetKeeAgentKeyList()
    {
      return GetKeeAgentKeyList(false);
    }

    internal IEnumerable<KeeAgentKey> GetKeeAgentKeyList(
        bool suppressErrorMessage)
    {
      if (debug) Log("Getting Key List...");
      if (debug) Log("Called from " + new StackTrace().GetFrame(2)
          .GetMethod().Name);
      pluginHost.MainWindow.NotifyUserActivity();

      List<KeeAgentKey> keyList = new List<KeeAgentKey>();
      List<PwDatabase> databases;
      databases = this.pluginHost.MainWindow.DocumentManager
          .GetOpenDatabases();

      foreach (PwDatabase database in databases) {
        foreach (PwEntry entry in database.RootGroup
            .GetEntries(true)) {

          if (database.RecycleBinEnabled) {
            bool skipEntry = false;
            PwGroup testGroup = entry.ParentGroup;
            while (testGroup != null) {
              if (testGroup.Uuid.EqualsValue(
                  database.RecycleBinUuid)) {
                // ignore entries in recycle bin
                skipEntry = true;
                break;
              }
              testGroup = testGroup.ParentGroup;
            }
            if (skipEntry) {
              continue;
            }
          }

          foreach (KeyValuePair<string, ProtectedBinary> bin
              in entry.Binaries) {

            /* handle PuTTY Private Key files */

            if (bin.Key.EndsWith(".ppk")) {
              try {
                SecureString ssPassphrase = null;
                ProtectedString psPassphrase =
                    entry.Strings.Get(PwDefs.PasswordField);
                if (psPassphrase != null) {
                  byte[] passphraseBytes =
                      psPassphrase.ReadUtf8();
                  /* convert passphrase from KeePass
                   * protected format to .NET protected
                   * format */
                  ssPassphrase = new SecureString();
                  for (int i = 0; i < passphraseBytes.Length; i++) {
                    ssPassphrase.AppendChar((char)(passphraseBytes[i]));
                  }
                  Array.Clear(passphraseBytes, 0, passphraseBytes.Length);
                }

                string dbPath = database.IOConnectionInfo.Path;

                PpkFile.GetPassphraseCallback getPassphrase = delegate()
                {
                  return ssPassphrase;
                };

                PpkFile.WarnOldFileFormatCallback warnUser = delegate()
                {
                  // we will warn user a different way... won't we???
                };

                PpkKey ppkKey = PpkFile.ParseData(bin.Value.ReadData(),
                  getPassphrase, warnUser);
                KeeAgentKey key = new KeeAgentKey(ppkKey, dbPath, entry.Uuid,
                  bin.Key);
                keyList.Add(key);
                if (debug) Log("Found " + PSUtil.ToHex(key.GetFingerprint()));
              } catch (Exception ex) {
                if (!suppressErrorMessage || debug) {
                  string errorMessage = string.Format(
                    Translatable.ErrParsingKey,
                      entry.Strings.Get(PwDefs.TitleField).ReadString(),
                      entry.ParentGroup.GetFullPath(
                      Path.DirectorySeparatorChar.ToString(), false),
                      database.IOConnectionInfo.GetDisplayName());
                  string details = Translatable.ErrUnknown;
                  if (ex is PpkFileException) {
                    PpkFileException ppkFileEx = (PpkFileException)ex;
                    details = string.Format(Translatable.ErrPpkFileException,
                        ppkFileEx.Error.ToString(),
                        bin.Key);
                  }
                  string debugInfo = null;
                  if (debug) {
                    debugInfo = ex.ToString();
                  }
                  MessageService.ShowWarning(errorMessage, details, debugInfo);
                  if (debug) Log(errorMessage);
                  if (debug) Log(details);
                  if (debugInfo != null) Log(debugInfo);
                }
              }
            } // end .ppk file
          }
        }
      }
      return keyList;
    }

    internal PpkKey GetSSH2Key(byte[] fingerprint)
    {
      if (debug) Log("External program requested key " +
        PSUtil.ToHex(fingerprint));
      pluginHost.MainWindow.NotifyUserActivity();

      /* TODO it would probably be better if we cached the fingerprints and
       * mapped them to the database path and the PwEntry Uuid rather than
       * regenerating the full list to get a single key as we are doing here.
       * 
       * Also, there is the problem of duplicate fingerprints. We are currently
       * just selecting the first match.
       */

      IEnumerable<KeeAgentKey> ppkKeyList = GetKeeAgentKeyList();
      KeeAgentKey result = null;
      foreach (KeeAgentKey ppkKey in ppkKeyList) {
        if (result == null) {
          try {
            byte[] testFingerprint = ppkKey.GetFingerprint();
            if (testFingerprint.Length == fingerprint.Length) {
              bool match = true;
              for (int i = 0; i < testFingerprint.Length; i++) {
                if (testFingerprint[i] != fingerprint[i]) {
                  match = false;
                  break;
                }
              }
              if (match) {
                result = ppkKey;
              }
            }
          } catch (Exception ex) {
            Debug.Fail(ex.ToString());
          }
        }
        // dispose all keys except for the one match
        if (ppkKey != result) {
          ppkKey.Dispose();
        }
      }
      if (debug && result != null) Log("Match found");
      if (debug && result == null) Log("Match not found");
      if (result != null && confirmKeyRequest(result)) {
        return result;
      } else {
        result.Dispose();
        return null;
      }
    }

    /// <summary>
    /// Asks for confirmation or notifies user of key request 
    /// depending on option selected
    /// </summary>
    /// <param name="key">The key being requested</param>
    /// <returns>true if the request was allowed by the user</returns>
    public bool confirmKeyRequest(KeeAgentKey key)
    {
      switch (this.options.Notification) {
        case NotificationOptions.AlwaysAsk:
        case NotificationOptions.AskOnce:
          if (this.options.Notification == NotificationOptions.AskOnce) {
            // TODO implement request memory
          }
          // trick to make sure dialog shows in front of other applications
          this.pluginHost.MainWindow.TopMost = true;
          this.pluginHost.MainWindow.TopMost = false;
          DialogResult result = MessageBox.Show(
              Translatable.ConfirmKeyFetch,
              string.Empty,
              MessageBoxButtons.YesNo, MessageBoxIcon.Question);
          return result == DialogResult.Yes;
        case NotificationOptions.Balloon:
          string notifyText = string.Format(
              Translatable.NotifyKeyFetched,
              key.Comment);
          this.uiHelper.ShowBalloonNotification(notifyText);
          return true;
        case NotificationOptions.Never:
          return true;
        default:
          Debug.Fail("Unsupported option");
          return false;
      }
    }

    internal void saveOptions()
    {
      this.pluginHost.CustomConfig.SetString(
          KeeAgentExt.notificationOptionName,
          this.options.Notification.ToString());
      this.pluginHost.CustomConfig.SetBool(
          KeeAgentExt.logginEnabledOptionName,
          this.options.LoggingEnabled);
      this.pluginHost.CustomConfig.SetString(
          KeeAgentExt.logFileNameOptionName,
          this.options.LogFileName);
    }

    private void loadOptions()
    {
      this.options = new Options();

      /* Notification Option */

      NotificationOptions defaultNotificationValue =
          NotificationOptions.Balloon;
      NotificationOptions configFileNotificationValue;
      if (Enum.TryParse<NotificationOptions>(
          this.pluginHost.CustomConfig.GetString(
          KeeAgentExt.notificationOptionName,
          defaultNotificationValue.ToString()),
          out configFileNotificationValue)) {
        this.options.Notification = configFileNotificationValue;
      } else {
        this.options.Notification = defaultNotificationValue;
      }

      /* Log File Options */

      bool defaultLoggingEnabledValue = false;
      bool configFileLoggingEnabledValue =
          this.pluginHost.CustomConfig.GetBool(
          KeeAgentExt.logginEnabledOptionName,
          defaultLoggingEnabledValue);
      this.options.LoggingEnabled = configFileLoggingEnabledValue;

      string defaultLogFileNameValue = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
          "KeeAgent.log");
      string configFileLogFileNameValue =
          this.pluginHost.CustomConfig.GetString(
          KeeAgentExt.logFileNameOptionName);
      if (string.IsNullOrEmpty(configFileLogFileNameValue)) {
        this.options.LogFileName = defaultLogFileNameValue;
      } else {
        this.options.LogFileName = configFileLogFileNameValue;
      }
    }

    internal void Log(string message)
    {
      if (this.options.LoggingEnabled) {
        try {
          File.AppendAllText(options.LogFileName,
              DateTime.Now + ": " + message + "\n");
        } catch { }
      }
    }

  } // class
} // namespace
