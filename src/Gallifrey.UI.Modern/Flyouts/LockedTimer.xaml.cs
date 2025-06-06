﻿using Gallifrey.ExtensionMethods;
using Gallifrey.JiraTimers;
using Gallifrey.UI.Modern.Helpers;
using Gallifrey.UI.Modern.Models;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Gallifrey.UI.Modern.Flyouts
{
    public partial class LockedTimer
    {
        private readonly ModelHelpers modelHelpers;
        private TimerDisplayModel selectedTimerSelectorModel;
        private LockedTimerCollectionModel DataModel => (LockedTimerCollectionModel)DataContext;

        public LockedTimer(ModelHelpers modelHelpers)
        {
            this.modelHelpers = modelHelpers;
            InitializeComponent();

            var idleTimers = modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers().ToList();

            DataContext = new LockedTimerCollectionModel(idleTimers);
        }

        private async void LockedTimer_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!DataModel.LockedTimers.Any())
            {
                await modelHelpers.ShowMessageAsync("No Timers To Show", "You Have No Locked Timers\nThere is Nothing To Show Here!");
                modelHelpers.CloseFlyout(this);
            }
        }

        private async void AddButton(object sender, RoutedEventArgs e)
        {
            var selected = DataModel.LockedTimers.Where(x => x.IsSelected).ToList();

            if (selected.Count == 0)
            {
                await modelHelpers.ShowMessageAsync("Nothing Selected", "You Have Not Selected Any Locked Time To Add");
                Focus();
                return;
            }

            var selectedTimers = selected.Select(x => modelHelpers.Gallifrey.IdleTimerCollection.GetTimer(x.UniqueId)).Where(x => x != null).ToList();
            if (selected.Count != selectedTimers.Count)
            {
                await modelHelpers.ShowMessageAsync("Out Of Date", "The Timer Window Is Out Of Date And Needs To Be Refreshed");
                DataModel.RefreshLockedTimers(modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers());
                Focus();
                return;
            }

            var selectedTime = TimeSpan.Zero;
            var lockedTimerDate = DateTime.MinValue;
            foreach (var lockedTimerModel in selected)
            {
                if (lockedTimerDate == DateTime.MinValue || lockedTimerDate.Date == lockedTimerModel.DateForTimer)
                {
                    lockedTimerDate = lockedTimerModel.DateForTimer;
                    selectedTime = selectedTime.Add(lockedTimerModel.IdleTime);
                }
                else
                {
                    await modelHelpers.ShowMessageAsync("Invalid Selection", "All Timers Must Be On The Same Date!");
                    Focus();
                    return;
                }
            }

            JiraTimer runningTimer = null;
            var runningTimerId = modelHelpers.Gallifrey.JiraTimerCollection.GetRunningTimerId();
            if (runningTimerId.HasValue)
            {
                runningTimer = modelHelpers.Gallifrey.JiraTimerCollection.GetTimer(runningTimerId.Value);
                if (runningTimer.DateStarted.Date != lockedTimerDate.Date)
                {
                    runningTimer = null;
                }
            }

            var dialog = (BaseMetroDialog)Resources["TimeLocation"];
            await modelHelpers.ShowDialogAsync(dialog);

            var message = dialog.FindChild<TextBlock>("TimeLocateMessage");
            var runningTimerButton = dialog.FindChild<Button>("RunningTimerButton");

            if (runningTimer != null)
            {
                message.Text = $"Where Would You Like To Add The Time Worth {selectedTime.FormatAsString(false)}?\n\nNote:- Running Timer Is\n{runningTimer.JiraReference} - {runningTimer.JiraName}";
                runningTimerButton.Visibility = Visibility.Visible;
            }
            else
            {
                message.Text = $"Where Would You Like To Add The Time Worth {selectedTime.FormatAsString(false)}?";
                runningTimerButton.Visibility = Visibility.Collapsed;
            }

            await dialog.WaitUntilUnloadedAsync();
        }

        private async void DeleteButton(object sender, RoutedEventArgs e)
        {
            var selected = DataModel.LockedTimers.Where(x => x.IsSelected).ToList();

            if (selected.Count == 0)
            {
                await modelHelpers.ShowMessageAsync("Nothing Selected", "You Have Not Selected Any Locked Time To Delete");
                Focus();
                return;
            }

            var selectedTime = new TimeSpan();
            selectedTime = selected.Aggregate(selectedTime, (current, lockedTimerModel) => current.Add(lockedTimerModel.IdleTime));

            var result = await modelHelpers.ShowMessageAsync("Are You Sure?", $"Are you Sure You Want To Delete Locked Timers Worth {selectedTime.FormatAsString(false)}?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

            if (result == MessageDialogResult.Affirmative)
            {
                foreach (var lockedTimerModel in selected)
                {
                    modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(lockedTimerModel.UniqueId);
                }

                if (!modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers().Any())
                {
                    modelHelpers.CloseFlyout(this);
                    return;
                }

                DataModel.RefreshLockedTimers(modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers());
            }

            Focus();
        }

        private async void AddToNewTimer(object sender, RoutedEventArgs e)
        {
            var dialog = (BaseMetroDialog)Resources["TimeLocation"];
            await modelHelpers.HideDialogAsync(dialog);

            ShowAddTimer();
        }

        private async void AddToRunningTimer(object sender, RoutedEventArgs e)
        {
            var dialog = (BaseMetroDialog)Resources["TimeLocation"];
            await modelHelpers.HideDialogAsync(dialog);

            var selected = DataModel.LockedTimers.Where(x => x.IsSelected).ToList();
            var selectedTimers = selected.Select(x => modelHelpers.Gallifrey.IdleTimerCollection.GetTimer(x.UniqueId)).Where(x => x != null).ToList();
            var runningTimerId = modelHelpers.Gallifrey.JiraTimerCollection.GetRunningTimerId();

            if (!runningTimerId.HasValue)
            {
                await modelHelpers.ShowMessageAsync("Error Adding Time", "Something Went Wrong Adding To Running Timer");
                Focus();
                return;
            }

            modelHelpers.Gallifrey.JiraTimerCollection.AddIdleTimer(runningTimerId.Value, selectedTimers);

            foreach (var lockedTimerModel in selected)
            {
                modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(lockedTimerModel.UniqueId);
            }

            if (modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers().Any())
            {
                DataModel.RefreshLockedTimers(modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers());
            }
            else
            {
                modelHelpers.CloseFlyout(this);
            }
        }

        private async void AddToExistingTimer(object sender, RoutedEventArgs e)
        {
            var dialog = (BaseMetroDialog)Resources["TimeLocation"];
            await modelHelpers.HideDialogAsync(dialog);

            var selected = DataModel.LockedTimers.Where(x => x.IsSelected).ToList();
            var selectedTimers = selected.Select(x => modelHelpers.Gallifrey.IdleTimerCollection.GetTimer(x.UniqueId)).Where(x => x != null).ToList();
            var lockedTimerDate = selected.First().DateForTimer;

            var items = modelHelpers.Gallifrey.JiraTimerCollection.GetTimersForADate(lockedTimerDate).Select(x => new TimerDisplayModel(x)).ToList();

            var timeSelectorDialog = (BaseMetroDialog)Resources["TimerSelector"];
            await modelHelpers.ShowDialogAsync(timeSelectorDialog);

            var comboBox = timeSelectorDialog.FindChild<ComboBox>("Items");
            comboBox.ItemsSource = items;

            var message = timeSelectorDialog.FindChild<TextBlock>("TimerSelectorMessage");
            message.Text = "Please Select Which Timer";

            await timeSelectorDialog.WaitUntilUnloadedAsync();

            if (selectedTimerSelectorModel != null)
            {
                modelHelpers.Gallifrey.JiraTimerCollection.AddIdleTimer(selectedTimerSelectorModel.Timer.UniqueId, selectedTimers);

                foreach (var lockedTimerModel in selected)
                {
                    modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(lockedTimerModel.UniqueId);
                }
            }

            if (modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers().Any())
            {
                DataModel.RefreshLockedTimers(modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers());
            }
            else
            {
                modelHelpers.CloseFlyout(this);
            }
        }

        private async void AddToRecentTimer(object sender, RoutedEventArgs e)
        {
            var dialog = (BaseMetroDialog)Resources["TimeLocation"];
            await modelHelpers.HideDialogAsync(dialog);

            var items = modelHelpers.Gallifrey.JiraTimerCollection.GetJiraReferencesForLastDays(999).Select(x => new TimerDisplayModel(x)).ToList();

            var timeSelectorDialog = (BaseMetroDialog)Resources["TimerSelector"];
            await modelHelpers.ShowDialogAsync(timeSelectorDialog);

            var comboBox = timeSelectorDialog.FindChild<ComboBox>("Items");
            comboBox.ItemsSource = items;

            var message = timeSelectorDialog.FindChild<TextBlock>("TimerSelectorMessage");
            message.Text = "Please Select Which Jira";

            await timeSelectorDialog.WaitUntilUnloadedAsync();

            var selected = DataModel.LockedTimers.Where(x => x.IsSelected).ToList();

            if (selectedTimerSelectorModel != null)
            {
                var lockedTimerDate = selected.First().DateForTimer;
                var timersAlreadyExisting = modelHelpers.Gallifrey.JiraTimerCollection.GetTimersForADate(lockedTimerDate).FirstOrDefault(x => x.JiraReference == selectedTimerSelectorModel.Reference);

                if (timersAlreadyExisting == null)
                {
                    ShowAddTimer(selectedTimerSelectorModel.Reference);
                }
                else
                {
                    var selectedTimers = selected.Select(x => modelHelpers.Gallifrey.IdleTimerCollection.GetTimer(x.UniqueId)).Where(x => x != null).ToList();
                    modelHelpers.Gallifrey.JiraTimerCollection.AddIdleTimer(timersAlreadyExisting.UniqueId, selectedTimers);

                    foreach (var lockedTimerModel in selected)
                    {
                        modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(lockedTimerModel.UniqueId);
                    }

                    if (modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers().Any())
                    {
                        DataModel.RefreshLockedTimers(modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers());
                    }
                    else
                    {
                        modelHelpers.CloseFlyout(this);
                    }
                }
            }
        }

        private async void ShowAddTimer(string preLoadJiraRef = null)
        {
            var selected = DataModel.LockedTimers.Where(x => x.IsSelected).ToList();
            var selectedTimers = selected.Select(x => modelHelpers.Gallifrey.IdleTimerCollection.GetTimer(x.UniqueId)).Where(x => x != null).ToList();
            var lockedTimerDate = selected.First().DateForTimer;
            modelHelpers.HideFlyout(this);
            var addFlyout = new AddTimer(modelHelpers, startDate: lockedTimerDate, enableDateChange: false, idleTimers: selectedTimers, jiraRef: preLoadJiraRef);
            await modelHelpers.OpenFlyout(addFlyout);

            if (addFlyout.AddedTimer)
            {
                foreach (var lockedTimerModel in selected)
                {
                    modelHelpers.Gallifrey.IdleTimerCollection.RemoveTimer(lockedTimerModel.UniqueId);
                }

                if (modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers().Any())
                {
                    await modelHelpers.OpenFlyout(this);
                    DataModel.RefreshLockedTimers(modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers());
                }
                else
                {
                    modelHelpers.CloseFlyout(this);
                    modelHelpers.SetSelectedTimer(addFlyout.NewTimerId);
                }
            }
            else
            {
                await modelHelpers.OpenFlyout(this);
            }
        }

        private async void CancelAddTimer(object sender, RoutedEventArgs e)
        {
            Focus();
            var dialog = (BaseMetroDialog)Resources["TimeLocation"];
            await modelHelpers.HideDialogAsync(dialog);
        }

        private async void ConfirmTimerSelector(object sender, RoutedEventArgs e)
        {
            var dialog = (BaseMetroDialog)Resources["TimerSelector"];
            var comboBox = dialog.FindChild<ComboBox>("Items");

            selectedTimerSelectorModel = (TimerDisplayModel)comboBox.SelectedItem;

            await modelHelpers.HideDialogAsync(dialog);
        }

        private async void CancelTimerSelector(object sender, RoutedEventArgs e)
        {
            var dialog = (BaseMetroDialog)Resources["TimerSelector"];

            selectedTimerSelectorModel = null;

            await modelHelpers.HideDialogAsync(dialog);
        }

        private void LockedTimer_OpenChange(object sender, RoutedEventArgs e)
        {
            DataModel.RefreshLockedTimers(modelHelpers.Gallifrey.IdleTimerCollection.GetUnusedIdleTimers());
        }
    }
}
