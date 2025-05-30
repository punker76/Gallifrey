﻿using Gallifrey.Exceptions.JiraIntegration;
using Gallifrey.Exceptions.JiraTimers;
using Gallifrey.IdleTimers;
using Gallifrey.Jira.Model;
using Gallifrey.UI.Modern.Helpers;
using Gallifrey.UI.Modern.Models;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Gallifrey.UI.Modern.Flyouts
{
    public partial class AddTimer
    {
        private readonly ModelHelpers modelHelpers;
        private readonly ProgressDialogHelper progressDialogHelper;
        private AddTimerModel DataModel => (AddTimerModel)DataContext;
        public bool AddedTimer { get; set; }
        public Guid NewTimerId { get; set; }

        public AddTimer(ModelHelpers modelHelpers, string jiraRef = null, DateTime? startDate = null, bool? enableDateChange = null, List<IdleTimer> idleTimers = null, bool? startNow = null)
        {
            this.modelHelpers = modelHelpers;
            InitializeComponent();
            progressDialogHelper = new ProgressDialogHelper(modelHelpers);

            if (startNow.HasValue && startNow.Value)
            {
                startNow = false;
            }

            DataContext = new AddTimerModel(modelHelpers.Gallifrey, jiraRef, startDate, enableDateChange, idleTimers, startNow);
            AddedTimer = false;
        }

        private async void AddButton(object sender, RoutedEventArgs e)
        {
            if (!DataModel.StartDate.HasValue)
            {
                await modelHelpers.ShowMessageAsync("Missing Date", "You Must Enter A Start Date");
                Focus();
                return;
            }

            if (DataModel.StartDate.Value.Date < DataModel.MinDate.Date || DataModel.StartDate.Value.Date > DataModel.MaxDate.Date)
            {
                await modelHelpers.ShowMessageAsync("Invalid Date", $"You Must Enter A Start Date Between {DataModel.MinDate.ToShortDateString()} And {DataModel.MaxDate.ToShortDateString()}");
                Focus();
                return;
            }

            var seedTime = DataModel.TimeEditable ? new TimeSpan(DataModel.StartHours ?? 0, DataModel.StartMinutes ?? 0, 0) : TimeSpan.Zero;

            Issue jiraIssue = null;
            var jiraRef = string.Empty;

            if (!DataModel.LocalTimer)
            {
                jiraRef = DataModel.JiraReference;

                void GetIssue()
                {
                    if (modelHelpers.Gallifrey.JiraConnection.DoesJiraExist(jiraRef))
                    {
                        jiraIssue = modelHelpers.Gallifrey.JiraConnection.GetJiraIssue(jiraRef);
                    }
                }

                var jiraDownloadResult = await progressDialogHelper.Do(GetIssue, "Searching For Jira Issue", true, false);

                if (jiraDownloadResult.Status == ProgressResult.JiraHelperStatus.Cancelled)
                {
                    Focus();
                    return;
                }

                if (jiraIssue == null)
                {
                    await modelHelpers.ShowMessageAsync("Invalid Jira", $"Unable To Locate The Jira '{jiraRef}'");
                    Focus();
                    return;
                }

                if (DataModel.JiraReferenceEditable)
                {
                    var result = await modelHelpers.ShowMessageAsync("Correct Jira?", $"Jira found!\n\nRef: {jiraIssue.key}\nName: {jiraIssue.fields.summary}\n\nIs that correct?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

                    if (result == MessageDialogResult.Negative)
                    {
                        Focus();
                        return;
                    }
                }
            }

            if (DataModel.DatePeriod)
            {
                AddedTimer = await AddPeriodTimer(jiraIssue, seedTime);
            }
            else
            {
                AddedTimer = await AddSingleTimer(jiraIssue, seedTime, DataModel.StartDate.Value);
            }

            if (!AddedTimer)
            {
                Focus();
                return;
            }

            var stillDoingThings = false;

            if (!DataModel.LocalTimer)
            {
                if (DataModel.AssignToMe)
                {
                    try
                    {
                        await progressDialogHelper.Do(() => modelHelpers.Gallifrey.JiraConnection.AssignToCurrentUser(jiraRef), "Assigning Issue", false, true);
                    }
                    catch (JiraConnectionException)
                    {
                        await modelHelpers.ShowMessageAsync("Assign Jira Error", "Unable To Locate Assign Jira To Current User");
                    }
                }

                if (DataModel.ChangeStatus)
                {
                    try
                    {
                        var transitionResult = await progressDialogHelper.Do(() => modelHelpers.Gallifrey.JiraConnection.GetTransitions(jiraRef), "Getting Transitions To Change Status", false, true);

                        if (transitionResult.Status == ProgressResult.JiraHelperStatus.Success)
                        {
                            var transitionsAvailable = transitionResult.RetVal;

                            var timeSelectorDialog = (BaseMetroDialog)Resources["TransitionSelector"];
                            await modelHelpers.ShowDialogAsync(timeSelectorDialog);

                            var comboBox = timeSelectorDialog.FindChild<ComboBox>("Items");
                            comboBox.ItemsSource = transitionsAvailable.Select(x => x.name).ToList();

                            var messageBox = timeSelectorDialog.FindChild<TextBlock>("Message");
                            messageBox.Text = $"Please Select The Status Update You Would Like To Perform To {DataModel.JiraReference}";

                            stillDoingThings = true;
                        }
                    }
                    catch (Exception)
                    {
                        await modelHelpers.ShowMessageAsync("Status Update Error", "Unable To Change The Status Of This Issue");
                    }
                }
            }

            if (!stillDoingThings)
            {
                modelHelpers.CloseFlyout(this);
                modelHelpers.RefreshModel();
            }
        }

        private async Task<bool> AddPeriodTimer(Issue jiraIssue, TimeSpan seedTime)
        {
            if (!DataModel.StartDate.HasValue || !DataModel.EndDate.HasValue)
            {
                await modelHelpers.ShowMessageAsync("Missing Start/End Date", "The Start Or End Date Is Not Valid, Please Reselect");
                return false;
            }

            var workingDate = DataModel.StartDate.Value;

            while (workingDate <= DataModel.EndDate.Value)
            {
                var addTimer = true;
                if (!modelHelpers.Gallifrey.Settings.AppSettings.ExportDays.Contains(workingDate.DayOfWeek))
                {
                    var result = await modelHelpers.ShowMessageAsync("Add For Non-Working Day?", $"The Date {workingDate:ddd, dd MMM} Is Not A Working Day.\nWould You Still Like To Add A Timer For This Date?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

                    if (result == MessageDialogResult.Negative)
                    {
                        addTimer = false;
                    }
                }

                if (addTimer)
                {
                    var added = await AddSingleTimer(jiraIssue, seedTime, workingDate);
                    if (!added && workingDate < DataModel.EndDate.Value)
                    {
                        var result = await modelHelpers.ShowMessageAsync("Continue Adding?", $"The Timer For {workingDate:ddd, dd MMM} Was Not Added.\nWould You Like To Carry On Adding For The Remaining Dates?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

                        if (result == MessageDialogResult.Negative)
                        {
                            return false;
                        }
                    }
                }

                workingDate = workingDate.AddDays(1);
            }

            return true;
        }

        private async Task<bool> AddSingleTimer(Issue jiraIssue, TimeSpan seedTime, DateTime startDate)
        {
            try
            {
                NewTimerId = DataModel.LocalTimer ? modelHelpers.Gallifrey.JiraTimerCollection.AddLocalTimer(DataModel.LocalTimerDescription, startDate, seedTime, DataModel.StartNow) : modelHelpers.Gallifrey.JiraTimerCollection.AddTimer(jiraIssue, startDate, seedTime, DataModel.StartNow);
                AddedTimer = true;
                if (!DataModel.TimeEditable)
                {
                    modelHelpers.Gallifrey.JiraTimerCollection.AddIdleTimer(NewTimerId, DataModel.IdleTimers);
                }
            }
            catch (DuplicateTimerException ex)
            {
                var doneSomething = false;
                if (seedTime.TotalMinutes > 0 || !DataModel.TimeEditable)
                {
                    var result = await modelHelpers.ShowMessageAsync("Duplicate Timer", $"The Timer Already Exists On {startDate:ddd, dd MMM}, Would You Like To Add The Time?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "Yes", NegativeButtonText = "No", DefaultButtonFocus = MessageDialogResult.Affirmative });

                    if (result == MessageDialogResult.Affirmative)
                    {
                        if (DataModel.TimeEditable)
                        {
                            modelHelpers.Gallifrey.JiraTimerCollection.AdjustTime(ex.TimerId, seedTime.Hours, seedTime.Minutes, true);
                        }
                        else
                        {
                            modelHelpers.Gallifrey.JiraTimerCollection.AddIdleTimer(ex.TimerId, DataModel.IdleTimers);
                        }

                        doneSomething = true;
                    }
                    else
                    {
                        return false;
                    }
                }

                if (DataModel.StartNow)
                {
                    modelHelpers.Gallifrey.JiraTimerCollection.StartTimer(ex.TimerId);
                    doneSomething = true;
                }

                if (!doneSomething)
                {
                    await modelHelpers.ShowMessageAsync("Duplicate Timer", "This Timer Already Exists!");
                    return false;
                }

                NewTimerId = ex.TimerId;
            }

            return true;
        }

        private async void SearchButton(object sender, RoutedEventArgs e)
        {
            modelHelpers.HideFlyout(this);
            var searchFlyout = new Search(modelHelpers, openFromAdd: true);
            await modelHelpers.OpenFlyout(searchFlyout);
            if (searchFlyout.SelectedJira != null)
            {
                DataModel.SetJiraReference(searchFlyout.SelectedJira.Reference);
            }
            await modelHelpers.OpenFlyout(this);
        }

        private async void TransitionSelected(object sender, RoutedEventArgs e)
        {
            var selectedTransition = string.Empty;

            try
            {
                var dialog = (BaseMetroDialog)Resources["TransitionSelector"];
                var comboBox = dialog.FindChild<ComboBox>("Items");

                selectedTransition = (string)comboBox.SelectedItem;
                var jiraRef = DataModel.JiraReference;

                await modelHelpers.HideDialogAsync(dialog);

                await progressDialogHelper.Do(() => modelHelpers.Gallifrey.JiraConnection.TransitionIssue(jiraRef, selectedTransition), "Changing Status", false, true);
            }
            catch (StateChangedException)
            {
                if (string.IsNullOrWhiteSpace(selectedTransition))
                {
                    await modelHelpers.ShowMessageAsync("Status Update Error", "Unable To Change The Status Of This Issue");
                }
                else
                {
                    await modelHelpers.ShowMessageAsync("Status Update Error", $"Unable To Change The Status Of This Issue To {selectedTransition}");
                }
            }

            modelHelpers.CloseFlyout(this);
            modelHelpers.RefreshModel();
        }

        private async void CancelTransition(object sender, RoutedEventArgs e)
        {
            var dialog = (BaseMetroDialog)Resources["TransitionSelector"];
            await modelHelpers.HideDialogAsync(dialog);

            modelHelpers.CloseFlyout(this);
            modelHelpers.RefreshModel();
        }
    }
}
