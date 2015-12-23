﻿using System;
using System.Linq;
using System.Windows.Input;
using Gallifrey.Exceptions.JiraTimers;
using Gallifrey.UI.Modern.Flyouts;
using Gallifrey.UI.Modern.Helpers;
using Gallifrey.UI.Modern.Models;
using MahApps.Metro.Controls.Dialogs;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace Gallifrey.UI.Modern.MainViews
{
    public partial class TimerTabs
    {
        private MainViewModel ViewModel => (MainViewModel)DataContext;
        private ModelHelpers ModelHelpers => ViewModel.ModelHelpers;

        public TimerTabs()
        {
            InitializeComponent();
        }

        private void TimerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var timerId = ViewModel.GetSelectedTimerId();

            if (timerId.HasValue)
            {
                var runningTimer = ModelHelpers.Gallifrey.JiraTimerCollection.GetRunningTimerId();

                if (runningTimer.HasValue && runningTimer.Value == timerId.Value)
                {
                    ModelHelpers.Gallifrey.JiraTimerCollection.StopTimer(timerId.Value, false);
                }
                else
                {
                    try
                    {
                        ModelHelpers.Gallifrey.JiraTimerCollection.StartTimer(timerId.Value);
                        ModelHelpers.RefreshModel();
                        ModelHelpers.SelectRunningTimer();
                    }
                    catch (DuplicateTimerException)
                    {
                        DialogCoordinator.Instance.ShowMessageAsync(ModelHelpers.DialogContext, "Wrong Day!", "Use The Version Of This Timer For Today!");
                    }
                }
            }
        }

        private void TabDragOver(object sender, DragEventArgs e)
        {
            var url = GetUrl(e);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var uriDrag = new Uri(url);
                var jiraUri = new Uri(ModelHelpers.Gallifrey.Settings.JiraConnectionSettings.JiraUrl);
                if (uriDrag.Host == jiraUri.Host)
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            }

            if (!e.Handled)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private async void TagDragDrop(object sender, DragEventArgs e)
        {
            var url = GetUrl(e);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var uriDrag = new Uri(url).AbsolutePath;
                var jiraRef = uriDrag.Substring(uriDrag.LastIndexOf("/") + 1);
                var todaysDate = DateTime.Now.Date;
                var dayTimers = ModelHelpers.Gallifrey.JiraTimerCollection.GetTimersForADate(todaysDate).ToList();

                if (dayTimers.Any(x => x.JiraReference == jiraRef))
                {
                    ModelHelpers.Gallifrey.JiraTimerCollection.StartTimer(dayTimers.First(x => x.JiraReference == jiraRef).UniqueId);
                    ModelHelpers.RefreshModel();
                    ModelHelpers.SelectRunningTimer();
                }
                else
                {
                    //Validate jira is real
                    try
                    {
                        ModelHelpers.Gallifrey.JiraConnection.GetJiraIssue(jiraRef);
                    }
                    catch (Exception)
                    {
                        await DialogCoordinator.Instance.ShowMessageAsync(ModelHelpers.DialogContext, "Invalid Jira", $"Unable To Locate That Jira.\n\nJira Ref Dropped: '{jiraRef}'");
                        return;
                    }

                    //show add form, we know it's a real jira & valid
                    await ModelHelpers.OpenFlyout(new AddTimer(ModelHelpers, startDate: todaysDate, jiraRef: jiraRef, startNow: true));
                }
            }
        }

        private static string GetUrl(DragEventArgs e)
        {
            if ((e.Effects & DragDropEffects.Copy) == DragDropEffects.Copy)
            {
                if (e.Data.GetDataPresent("Text"))
                {
                    return (string)e.Data.GetData("Text");
                }
            }

            return string.Empty;
        }
    }
}
