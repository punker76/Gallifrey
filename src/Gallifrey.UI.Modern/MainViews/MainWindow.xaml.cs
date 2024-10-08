﻿using Exceptionless;
using Gallifrey.AppTracking;
using Gallifrey.Exceptions;
using Gallifrey.Exceptions.JiraIntegration;
using Gallifrey.ExtensionMethods;
using Gallifrey.JiraTimers;
using Gallifrey.UI.Modern.Extensions;
using Gallifrey.UI.Modern.Flyouts;
using Gallifrey.UI.Modern.Helpers;
using Gallifrey.UI.Modern.Models;
using Gallifrey.Versions;
using MahApps.Metro.Controls.Dialogs;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;

namespace Gallifrey.UI.Modern.MainViews
{
    public partial class MainWindow
    {
        private readonly ModelHelpers modelHelpers;
        private readonly ExceptionlessHelper exceptionlessHelper;
        private readonly ProgressDialogHelper progressDialogHelper;
        private readonly Timer flyoutOpenCheck;
        private readonly Timer updateHeartbeat;
        private readonly Timer idleDetectionHeartbeat;
        private MainViewModel ViewModel => (MainViewModel)DataContext;
        private bool machineLocked;
        private bool machineIdle;
        private bool forceClosed;

        public MainWindow(InstanceType instance)
        {
            InitializeComponent();

            var gallifrey = new Backend(instance);
            modelHelpers = new ModelHelpers(gallifrey, FlyoutsControl);

            exceptionlessHelper = new ExceptionlessHelper(modelHelpers);
            exceptionlessHelper.RegisterExceptionless();

            progressDialogHelper = new ProgressDialogHelper(modelHelpers);

            var viewModel = new MainViewModel(modelHelpers);
            DataContext = viewModel;

            Height = gallifrey.Settings.UiSettings.Height;
            Width = gallifrey.Settings.UiSettings.Width;
            ThemeHelper.ChangeTheme(gallifrey.Settings.UiSettings.Theme);

            updateHeartbeat = new Timer(TimeSpan.FromHours(1).TotalMilliseconds);
            updateHeartbeat.Elapsed += AutoUpdateCheck;

            idleDetectionHeartbeat = new Timer(TimeSpan.FromSeconds(30).TotalMilliseconds);
            idleDetectionHeartbeat.Elapsed += IdleDetectionCheck;

            flyoutOpenCheck = new Timer(100);
            flyoutOpenCheck.Elapsed += FlyoutOpenCheck;
        }

        private enum InitialiseResult
        {
            MultipleGallifreyRunning,
            DebuggerNotAttached,
            NoInternetConnection,
            MissingConfig,
            JiraConnectionError,
            TempoConnectionError,
            Ok,
            NewUser
        }

        private InitialiseResult Initialise()
        {
            if (Process.GetProcesses().Count(process => process.ProcessName.Contains("Gallifrey")) > 1)
            {
                return InitialiseResult.MultipleGallifreyRunning;
            }

            if (!modelHelpers.Gallifrey.VersionControl.IsAutomatedDeploy && !Debugger.IsAttached)
            {
                return InitialiseResult.DebuggerNotAttached;
            }

            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add(HttpRequestHeader.UserAgent, $"Gallifrey-{modelHelpers.Gallifrey.VersionControl.VersionName}");
                    client.DownloadData("https://gallifrey-releases.blyth.me.uk");
                }
            }
            catch
            {
                return InitialiseResult.NoInternetConnection;
            }

            try
            {
                modelHelpers.Gallifrey.Initialise();
            }
            catch (MissingConfigException)
            {
                return InitialiseResult.NewUser;
            }
            catch (MissingJiraConfigException)
            {
                return InitialiseResult.MissingConfig;
            }
            catch (JiraConnectionException)
            {
                return InitialiseResult.JiraConnectionError;
            }
            catch (TempoConnectionException)
            {
                return InitialiseResult.TempoConnectionError;
            }

            modelHelpers.RefreshModel();
            modelHelpers.SelectRunningTimer();

            return InitialiseResult.Ok;
        }

        private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            await MainWindow_OnLoaded();
        }

        private async Task MainWindow_OnLoaded()
        {
            var result = await progressDialogHelper.Do(Initialise, "Initialising Gallifrey", true, true);
            if (result.Status == ProgressResult.JiraHelperStatus.Cancelled)
            {
                await modelHelpers.ShowMessageAsync("Gallifrey Not Initialised", "Gallifrey Initialisation Was Cancelled", MessageDialogStyle.Affirmative, new MetroDialogSettings { AffirmativeButtonText = "Close Gallifrey" });
                forceClosed = true;
                modelHelpers.CloseApp();
            }

            var checkedUpdate = false;
            if (result.RetVal != InitialiseResult.DebuggerNotAttached && result.RetVal != InitialiseResult.MultipleGallifreyRunning && result.RetVal != InitialiseResult.NoInternetConnection)
            {
                await PerformUpdate(UpdateType.StartUp);
                checkedUpdate = true;
            }

            if (result.RetVal == InitialiseResult.DebuggerNotAttached)
            {
                await modelHelpers.ShowMessageAsync("Debugger Not Running", "It Looks Like Your Running Without Auto-Update\nPlease Use The Installed Shortcut To Start Gallifrey Or Download Again From gallifrey.blyth.me.uk", MessageDialogStyle.Affirmative, new MetroDialogSettings { AffirmativeButtonText = "Close Gallifrey" });
                forceClosed = true;
                modelHelpers.CloseApp();
            }
            else if (result.RetVal == InitialiseResult.MultipleGallifreyRunning)
            {
                modelHelpers.Gallifrey.TrackEvent(TrackingType.MultipleInstancesRunning);
                await modelHelpers.ShowMessageAsync("Multiple Instances", "You Can Only Have One Instance Of Gallifrey Running At A Time", MessageDialogStyle.Affirmative, new MetroDialogSettings { AffirmativeButtonText = "Close Gallifrey" });
                forceClosed = true;
                modelHelpers.CloseApp();
            }
            else if (result.RetVal == InitialiseResult.NoInternetConnection)
            {
                modelHelpers.Gallifrey.TrackEvent(TrackingType.NoInternet);
                var userChoice = await modelHelpers.ShowMessageAsync("No Internet Connection", "Gallifrey Requires An Active Internet Connection To Work.", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Check Again", NegativeButtonText = "Close Now" });
                if (userChoice == MessageDialogResult.Affirmative)
                {
                    await MainWindow_OnLoaded();
                    return;
                }

                forceClosed = true;
                modelHelpers.CloseApp();
            }
            else if (result.RetVal == InitialiseResult.NewUser)
            {
                modelHelpers.Gallifrey.TrackEvent(TrackingType.SettingsMissing);
                await modelHelpers.ShowMessageAsync("Welcome To Gallifrey", "You Have No Settings In Gallifrey\n\nTo Get Started, We Need Your Jira Details");

                await NewUserOnBoarding();
                modelHelpers.Gallifrey.Settings.InternalSettings.SetNewUser(false);
                modelHelpers.Gallifrey.SaveSettings(false, false);
                await MainWindow_OnLoaded();
                return;
            }
            else if (result.RetVal == InitialiseResult.JiraConnectionError || result.RetVal == InitialiseResult.TempoConnectionError || result.RetVal == InitialiseResult.MissingConfig)
            {
                modelHelpers.Gallifrey.TrackEvent(TrackingType.ConnectionError);
                var message = string.Empty;
                switch (result.RetVal)
                {
                    case InitialiseResult.JiraConnectionError:
                        message = "We Were Unable To Authenticate To Jira, Please Confirm Login Details";
                        break;

                    case InitialiseResult.TempoConnectionError:
                        message = "We Were Unable To Authenticate To Tempo, Please Confirm Login Details";
                        break;

                    case InitialiseResult.MissingConfig:
                        message = "There Are Missing Configuration Items For Jira/Tempo, Please Confirm Login Details";
                        break;
                }

                var userUpdate = await modelHelpers.ShowMessageAsync("Login Failure", message, MessageDialogStyle.AffirmativeAndNegativeAndSingleAuxiliary, new MetroDialogSettings { AffirmativeButtonText = "Update Details", NegativeButtonText = "Cancel", FirstAuxiliaryButtonText = "Retry" });

                switch (userUpdate)
                {
                    case MessageDialogResult.Negative:
                        await modelHelpers.ShowMessageAsync("Come Back Soon", "Without A Correctly Configured Jira Connection Gallifrey Will Close, Please Come Back Soon!");
                        modelHelpers.CloseApp();
                        break;

                    case MessageDialogResult.FirstAuxiliary:
                        await MainWindow_OnLoaded();
                        return;

                    default:
                        await UserLoginFailure(result.RetVal == InitialiseResult.TempoConnectionError);
                        await MainWindow_OnLoaded();
                        return;
                }
            }

            if (!checkedUpdate)
            {
                await PerformUpdate(UpdateType.StartUp);
            }

            if (modelHelpers.Gallifrey.VersionControl.IsAutomatedDeploy && modelHelpers.Gallifrey.VersionControl.IsFirstRun)
            {
                var changeLog = modelHelpers.Gallifrey.GetChangeLog(XDocument.Parse(Properties.Resources.ChangeLog)).Where(x => x.NewVersion).ToList();

                if (changeLog.Any())
                {
                    await modelHelpers.OpenFlyout(new Flyouts.ChangeLog(changeLog));
                }
            }

            exceptionlessHelper.RegisterExceptionless();
            modelHelpers.Gallifrey.TrackEvent(TrackingType.AppLoad);
            updateHeartbeat.Enabled = true;
            idleDetectionHeartbeat.Enabled = true;
            flyoutOpenCheck.Enabled = true;
            modelHelpers.Gallifrey.NoActivityEvent += GallifreyOnNoActivityEvent;
            modelHelpers.Gallifrey.ExportPromptEvent += GallifreyOnExportPromptEvent;
            modelHelpers.Gallifrey.SettingsChanged += GallifreyOnSettingsChanged;
            SystemEvents.SessionSwitch += SessionSwitchHandler;
        }

        private async Task NewUserOnBoarding()
        {
            await UserLoginFailure();

            var viewSettings = await modelHelpers.ShowMessageAsync("More Settings", "Gallifrey Has A Vast Range Of Settings, Would You Like To See Them Now?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No" });

            if (viewSettings == MessageDialogResult.Affirmative)
            {
                await modelHelpers.OpenFlyout(new Flyouts.Settings(modelHelpers));
                if (!modelHelpers.Gallifrey.JiraConnection.IsConnected)
                {
                    var userUpdate = await modelHelpers.ShowMessageAsync("Lost Jira Connection", "We Seem To Have Lost Jira/Tempo Connection\nWould You Like To Update Your Details?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No" });

                    if (userUpdate == MessageDialogResult.Negative)
                    {
                        await modelHelpers.ShowMessageAsync("Come Back Soon", "Without A Correctly Configured Jira Connection Gallifrey Will Close, Please Come Back Soon!");
                        modelHelpers.CloseApp();
                    }

                    await UserLoginFailure();
                }
            }
        }

        private async Task UserLoginFailure(bool tempoOnly = false)
        {
            var loggedIn = false;

            while (!loggedIn)
            {
                try
                {
                    var jiraSettings = modelHelpers.Gallifrey.Settings.JiraConnectionSettings;

                    if (!tempoOnly)
                    {
                        jiraSettings.JiraUrl = await modelHelpers.ShowInputAsync("Jira URL", "Please Enter Your Jira Instance URL\nThis Is The URL You Go To When You Login Using A Browser\ne.g. https://MyCompany.atlassian.net", new MetroDialogSettings { DefaultText = jiraSettings.JiraUrl, AffirmativeButtonText = "Save", NegativeButtonText = "Cancel" });

                        if (string.IsNullOrWhiteSpace(jiraSettings.JiraUrl))
                        {
                            throw new MissingJiraConfigException("Missing URL");
                        }

                        var jiraDetails = await modelHelpers.ShowLoginAsync("Jira Authentication", "Please Enter Your Username/Email Address & Password You Use To Login To Jira.\nOn Jira Cloud Instances You Should Generate An API Key From Your Atlassian ID Profile (Under Security Settings) And Use This As Your Password.", new LoginDialogSettings { EnablePasswordPreview = true, InitialUsername = jiraSettings.JiraUsername, InitialPassword = jiraSettings.JiraPassword, RememberCheckBoxVisibility = Visibility.Collapsed, UsernameWatermark = "", PasswordWatermark = "", AffirmativeButtonText = "Save", NegativeButtonVisibility = Visibility.Visible, NegativeButtonText = "Cancel" });
                        jiraSettings.JiraUsername = jiraDetails.Username;
                        jiraSettings.JiraPassword = jiraDetails.Password;

                        if (string.IsNullOrWhiteSpace(jiraSettings.JiraUsername) || string.IsNullOrWhiteSpace(jiraSettings.JiraPassword))
                        {
                            throw new MissingJiraConfigException("Missing user or password");
                        }
                    }

                    var useTempoChoice = await modelHelpers.ShowMessageAsync("Tempo", "Do You Want To Use Tempo To Record Timesheets?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No" });
                    jiraSettings.UseTempo = useTempoChoice == MessageDialogResult.Affirmative;

                    if (jiraSettings.UseTempo)
                    {
                        var tempoDetails = await modelHelpers.ShowLoginAsync("Tempo Authentication", "Please Enter Your Tempo Api Token\nThe Tempo API Token Can Be Found In Your Tempo Settings Under API Integration.  Tokens Can Have A 5000 Day Expiration.  Only Worklog & Approval Scope Required, But Full Access Also Works.", new LoginDialogSettings { EnablePasswordPreview = true, InitialPassword = jiraSettings.TempoToken, ShouldHideUsername = true, RememberCheckBoxVisibility = Visibility.Collapsed, PasswordWatermark = "", AffirmativeButtonText = "Save", NegativeButtonVisibility = Visibility.Visible, NegativeButtonText = "Cancel" });
                        jiraSettings.TempoToken = tempoDetails.Password;
                    }
                    else
                    {
                        jiraSettings.TempoToken = string.Empty;
                    }

                    await progressDialogHelper.Do(() => modelHelpers.Gallifrey.SaveSettings(true, false), "Checking Jira Credentials", false, true);

                    loggedIn = true;
                }
                catch (MissingJiraConfigException)
                {
                    var result = await modelHelpers.ShowMessageAsync("Missing Information", "Some Of The Jira/Tempo Information We Requested Was Missing, Try Again?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No" });
                    if (result == MessageDialogResult.Negative)
                    {
                        await modelHelpers.ShowMessageAsync("Come Back Soon", "Without A Correctly Configured Jira/Tempo Connection Gallifrey Will Close, Please Come Back Soon!");
                        modelHelpers.CloseApp();
                    }
                }
                catch (JiraConnectionException)
                {
                    var result = await modelHelpers.ShowMessageAsync("Login Failure", "We Were Unable To Authenticate To Jira, Try Again?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No" });
                    if (result == MessageDialogResult.Negative)
                    {
                        await modelHelpers.ShowMessageAsync("Come Back Soon", "Without A Correctly Configured Jira/Tempo Connection Gallifrey Will Close, Please Come Back Soon!");
                        modelHelpers.CloseApp();
                    }
                }
                catch (TempoConnectionException)
                {
                    var result = await modelHelpers.ShowMessageAsync("Login Failure", "We Were Unable To Authenticate To Tempo, Try Again?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No" });
                    if (result == MessageDialogResult.Negative)
                    {
                        await modelHelpers.ShowMessageAsync("Come Back Soon", "Without A Correctly Configured Jira/Tempo Connection Gallifrey Will Close, Please Come Back Soon!");
                        modelHelpers.CloseApp();
                    }
                }
            }
        }

        private async void GallifreyOnExportPromptEvent(object sender, ExportPromptDetail e)
        {
            var timer = modelHelpers.Gallifrey.JiraTimerCollection.GetTimer(e.TimerId);
            if (timer != null)
            {
                var exportTime = e.ExportTime;
                var message = $"Do You Want To Export '{timer.JiraReference}'?\n";
                if (modelHelpers.Gallifrey.Settings.ExportSettings.ExportPromptAll || (new TimeSpan(exportTime.Ticks - exportTime.Ticks % 600000000) == new TimeSpan(timer.TimeToExport.Ticks - (timer.TimeToExport.Ticks % 600000000))))
                {
                    exportTime = timer.TimeToExport;
                    message += $"You Have '{exportTime.FormatAsString(false)}' To Export";
                }
                else
                {
                    message += $"You Have '{exportTime.FormatAsString(false)}' To Export For This Change";
                }

                var messageResult = await modelHelpers.ShowMessageAsync("Do You Want To Export?", message, MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

                if (messageResult == MessageDialogResult.Affirmative)
                {
                    if (modelHelpers.Gallifrey.Settings.ExportSettings.ExportPromptAll)
                    {
                        await modelHelpers.OpenFlyout(new Export(modelHelpers, e.TimerId, null));
                    }
                    else
                    {
                        await modelHelpers.OpenFlyout(new Export(modelHelpers, e.TimerId, e.ExportTime));
                    }
                }
            }
        }

        private void GallifreyOnNoActivityEvent(object sender, int millisecondsSinceActivity)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    ViewModel.SetNoActivityMilliseconds(millisecondsSinceActivity);

                    if (millisecondsSinceActivity == 0 || ViewModel.TimerRunning)
                    {
                        this.StopFlashingWindow();
                    }
                    else
                    {
                        this.FlashWindow();
                    }
                }
                catch (Exception)
                {
                    //Ignored
                }
            });
        }

        private void GallifreyOnSettingsChanged(object sender, EventArgs e)
        {
            exceptionlessHelper.RegisterExceptionless();
        }

        private void SessionSwitchHandler(object sender, SessionSwitchEventArgs e)
        {
            if (!modelHelpers.Gallifrey.Settings.AppSettings.TrackLockTime || !modelHelpers.Gallifrey.IsInitialised)
            {
                return;
            }

            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.SessionLogoff:
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.ConsoleDisconnect:

                    machineLocked = true;
                    if (machineIdle)
                    {
                        machineIdle = false;
                    }
                    else
                    {
                        modelHelpers.Gallifrey.StartLockTimer();
                    }
                    break;

                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.SessionLogon:
                case SessionSwitchReason.RemoteConnect:
                case SessionSwitchReason.ConsoleConnect:

                    machineLocked = false;
                    machineIdle = false;
                    StopLockTimer(modelHelpers.Gallifrey.Settings.AppSettings.LockTimeThresholdMilliseconds);
                    break;
            }

            modelHelpers.RefreshModel();
        }

        private void IdleDetectionCheck(object sender, ElapsedEventArgs e)
        {
            if (!modelHelpers.Gallifrey.Settings.AppSettings.TrackIdleTime || !modelHelpers.Gallifrey.IsInitialised)
            {
                return;
            }

            if (machineLocked)
            {
                machineIdle = false;
                return;
            }

            var idleTimeInfo = IdleTimeDetector.GetIdleTimeInfo();
            if (idleTimeInfo.IdleTime.TotalMilliseconds >= modelHelpers.Gallifrey.Settings.AppSettings.IdleTimeThresholdMilliseconds && !machineIdle)
            {
                machineIdle = true;
                modelHelpers.Gallifrey.StartLockTimer(idleTimeInfo.IdleTime);
            }
            else if (idleTimeInfo.IdleTime.TotalMilliseconds < modelHelpers.Gallifrey.Settings.AppSettings.IdleTimeThresholdMilliseconds && machineIdle)
            {
                machineIdle = false;
                Dispatcher.Invoke(() => StopLockTimer(modelHelpers.Gallifrey.Settings.AppSettings.IdleTimeThresholdMilliseconds));
            }
        }

        private void FlyoutOpenCheck(object sender, ElapsedEventArgs e)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (modelHelpers.Gallifrey.Settings.UiSettings.TopMostOnFlyoutOpen)
                    {
                        if (modelHelpers.FlyoutOpenOrDialogShowing)
                        {
                            if (!Topmost)
                            {
                                this.FlashWindow();
                                Topmost = true;
                                Activate();
                            }

                            if (WindowState == WindowState.Minimized)
                            {
                                WindowState = WindowState.Normal;
                            }
                        }
                        else
                        {
                            this.StopFlashingWindow();
                            Topmost = false;
                        }
                    }
                    else
                    {
                        Topmost = false;
                    }
                });
            }
            catch (TaskCanceledException)
            {
                //Suppress Exception
            }
        }

        private async void StopLockTimer(int threshold)
        {
            var idleTimerId = modelHelpers.Gallifrey.StopLockTimer();
            if (!idleTimerId.HasValue)
            {
                return;
            }

            var idleTimer = modelHelpers.Gallifrey.IdleTimerCollection.GetTimer(idleTimerId.Value);
            if (idleTimer == null)
            {
                return;
            }

            if (modelHelpers.Gallifrey.Settings.AppSettings.TargetLogPerDay == TimeSpan.Zero && idleTimer.IdleTimeValue > TimeSpan.FromHours(10))
            {
                modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(idleTimerId.Value);
            }
            else if (idleTimer.IdleTimeValue > modelHelpers.Gallifrey.Settings.AppSettings.TargetLogPerDay)
            {
                modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(idleTimerId.Value);
            }
            else if (idleTimer.IdleTimeValue.TotalMilliseconds < threshold)
            {
                modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(idleTimerId.Value);
                var runningId = modelHelpers.Gallifrey.JiraTimerCollection.GetRunningTimerId();
                if (runningId.HasValue)
                {
                    modelHelpers.Gallifrey.JiraTimerCollection.GetTimer(runningId.Value).AddIdleTimer(idleTimer);
                }
            }
            else
            {
                if (modelHelpers.FlyoutOpenOrDialogShowing)
                {
                    modelHelpers.HideAllFlyouts();
                }

                await modelHelpers.OpenFlyout(new LockedTimer(modelHelpers));

                foreach (var hiddenFlyout in modelHelpers.GetHiddenFlyouts())
                {
                    await modelHelpers.OpenFlyout(hiddenFlyout);
                }
            }
        }

        private async void ManualUpdateCheck(object sender, RoutedEventArgs e)
        {
            await PerformUpdate(UpdateType.Manual);
        }

        private void AutoUpdateCheck(object sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(async () =>
            {
                if (modelHelpers.Gallifrey.VersionControl.IsAutomatedDeploy &&
                    !modelHelpers.Gallifrey.VersionControl.UpdateError &&
                    !modelHelpers.Gallifrey.VersionControl.UpdateReinstallNeeded)
                {
                    await PerformUpdate(UpdateType.Auto);
                }
            });
        }

        private enum UpdateType
        {
            Manual,
            Auto,
            StartUp
        }

        private async Task PerformUpdate(UpdateType updateType)
        {
            try
            {
                if (!modelHelpers.Gallifrey.IsInitialised && updateType != UpdateType.StartUp)
                {
                    return;
                }

                //Do Update
                UpdateResult updateResult;
                if (updateType == UpdateType.Manual || updateType == UpdateType.StartUp)
                {
                    var controller = await modelHelpers.ShowIndeterminateProgressAsync("Please Wait", "Checking For Updates");
                    controller.SetIndeterminate();
                    updateResult = await modelHelpers.Gallifrey.VersionControl.CheckForUpdates(true);
                    await controller.CloseAsync();
                }
                else
                {
                    updateResult = await modelHelpers.Gallifrey.VersionControl.CheckForUpdates(false);
                }

                //Handle Update Result
                if (updateResult == UpdateResult.Updated && (updateType == UpdateType.Manual || updateType == UpdateType.StartUp))
                {
                    var messageResult = await modelHelpers.ShowMessageAsync("Update Found", "Restart Now To Install Update?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

                    if (messageResult == MessageDialogResult.Affirmative)
                    {
                        modelHelpers.Gallifrey.TrackEvent(TrackingType.ManualUpdateRestart);
                        modelHelpers.CloseApp(true);
                    }
                }
                else if (updateResult == UpdateResult.Updated && modelHelpers.Gallifrey.Settings.AppSettings.AutoUpdate && !machineLocked && !modelHelpers.FlyoutOpenOrDialogShowing)
                {
                    modelHelpers.Gallifrey.TrackEvent(TrackingType.AutoUpdateInstalled);
                    modelHelpers.CloseApp(true);
                }
                else if (updateResult == UpdateResult.Updated)
                {
                    modelHelpers.ShowNotification("Update Found, Restart Now To Install!");
                }
                else if (updateResult == UpdateResult.NoInternet && updateType == UpdateType.Manual)
                {
                    await modelHelpers.ShowMessageAsync("Unable To Update", "Unable To Access https://gallifrey-releases.blyth.me.uk To Check For Updates");
                }
                else if (updateResult == UpdateResult.NotDeployable && updateType == UpdateType.Manual)
                {
                    await modelHelpers.ShowMessageAsync("Unable To Update", "You Cannot Auto Update This Version Of Gallifrey");
                }
                else if ((updateResult == UpdateResult.NoUpdate || updateResult == UpdateResult.TooSoon) && updateType == UpdateType.Manual)
                {
                    await modelHelpers.ShowMessageAsync("No Update Found", "There Are No Updates At This Time, Check Back Soon!");
                }
                else if (updateResult == UpdateResult.ReinstallNeeded && (updateType == UpdateType.Manual || updateType == UpdateType.StartUp))
                {
                    var messageResult = await modelHelpers.ShowMessageAsync("Update Error", "To Update An Uninstall/Reinstall Is Required.\nThis Can Happen Automatically & No Timers Will Be Lost\nAll You Need To Do Is Press The \"Install\" Button When Prompted\n\nDo You Want To Update Now?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

                    if (messageResult == MessageDialogResult.Affirmative)
                    {
                        try
                        {
                            modelHelpers.Gallifrey.VersionControl.ManualReinstall();
                            await Task.Delay(TimeSpan.FromSeconds(2));
                        }
                        catch (Exception)
                        {
                            await modelHelpers.ShowMessageAsync("Update Error", "There Was An Error Trying To Update Gallifrey, You May Need To Re-Download The App");
                        }
                        finally
                        {
                            modelHelpers.CloseApp();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (updateType == UpdateType.Manual || updateType == UpdateType.StartUp)
                {
                    await modelHelpers.ShowMessageAsync("Update Error", "There Was An Error Trying To Update Gallifrey, If This Problem Persists Please Contact Support");
                    modelHelpers.CloseApp(true);
                }
                else
                {
                    ExceptionlessClient.Default.CreateEvent().SetException(ex).AddTags("Update").Submit();
                }
            }
        }

        private void GetBeta(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://gallifrey.blyth.me.uk/downloads/beta"));
        }

        protected override void OnClosed(EventArgs e)
        {
            exceptionlessHelper.CloseExceptionless();
            base.OnClosed(e);
            if (!forceClosed)
            {
                flyoutOpenCheck.Stop();
                modelHelpers.Gallifrey.Settings.UiSettings.Height = (int)Height;
                modelHelpers.Gallifrey.Settings.UiSettings.Width = (int)Width;
                modelHelpers.Gallifrey.Close();
            }
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (modelHelpers.FlyoutOpenOrDialogShowing)
            {
                return;
            }

            var key = e.Key;
            RemoteButtonTrigger trigger;

            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                switch (key)
                {
                    case Key.A: trigger = RemoteButtonTrigger.Add; break;
                    case Key.D: trigger = RemoteButtonTrigger.Delete; break;
                    case Key.F: trigger = RemoteButtonTrigger.Search; break;
                    case Key.E: trigger = RemoteButtonTrigger.Edit; break;
                    case Key.U: trigger = RemoteButtonTrigger.Export; break;
                    case Key.L: trigger = RemoteButtonTrigger.LockTimer; break;
                    case Key.S: trigger = RemoteButtonTrigger.Settings; break;
                    case Key.B: trigger = RemoteButtonTrigger.Save; break;
                    case Key.C: trigger = RemoteButtonTrigger.Copy; break;
                    case Key.V: trigger = RemoteButtonTrigger.Paste; break;
                    case Key.F1: trigger = RemoteButtonTrigger.Info; break;
                    case Key.F2: trigger = RemoteButtonTrigger.Email; break;
                    case Key.F3: trigger = RemoteButtonTrigger.GitHub; break;
                    case Key.F4: trigger = RemoteButtonTrigger.Donate; break;
                    default: return;
                }
            }
            else
            {
                switch (key)
                {
                    case Key.F1: trigger = RemoteButtonTrigger.Add; break;
                    case Key.F2: trigger = RemoteButtonTrigger.Delete; break;
                    case Key.F3: trigger = RemoteButtonTrigger.Search; break;
                    case Key.F4: trigger = RemoteButtonTrigger.Edit; break;
                    case Key.F5: trigger = RemoteButtonTrigger.Export; break;
                    case Key.F6: trigger = RemoteButtonTrigger.Save; break;
                    case Key.F7: trigger = RemoteButtonTrigger.LockTimer; break;
                    case Key.F8: trigger = RemoteButtonTrigger.Settings; break;
                    default: return;
                }
            }

            modelHelpers.TriggerRemoteButtonPress(trigger);
        }

        private void MainWindow_Activated(object sender, EventArgs e)
        {
            this.StopFlashingWindow();
        }

        private void MainWindow_StateChange(object sender, EventArgs e)
        {
            if (modelHelpers.Gallifrey.Settings.UiSettings.TopMostOnFlyoutOpen && modelHelpers.FlyoutOpenOrDialogShowing && WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
        }
    }
}
