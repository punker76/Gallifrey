﻿using Gallifrey.AppTracking;
using Gallifrey.Comparers;
using Gallifrey.Exceptions.JiraIntegration;
using Gallifrey.Jira;
using Gallifrey.Jira.Enum;
using Gallifrey.Jira.Model;
using Gallifrey.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Gallifrey.JiraIntegration
{
    public interface IJiraConnection
    {
        bool DoesJiraExist(string jiraRef);

        Issue GetJiraIssue(string jiraRef);

        IEnumerable<string> GetJiraFilters();

        IEnumerable<Issue> GetJiraIssuesFromFilter(string filterName);

        IEnumerable<Issue> GetJiraIssuesFromSearchText(string searchText);

        IEnumerable<Issue> GetJiraIssuesFromSearchTextLimitedToKeys(string searchText, IEnumerable<string> jiraKeys);

        void LogTime(string jiraRef, DateTime exportTimeStamp, TimeSpan exportTime, WorkLogStrategy strategy, bool addStandardComment, string comment = "", TimeSpan? remainingTime = null);

        IEnumerable<Issue> GetJiraCurrentUserOpenIssues();

        IEnumerable<StandardWorkLog> GetWorkLoggedForDatesFilteredIssues(IEnumerable<DateTime> queryDates, IEnumerable<string> issueRefs);

        void AssignToCurrentUser(string jiraRef);

        User CurrentUser { get; }
        bool IsConnected { get; }

        void TransitionIssue(string jiraRef, string transition);

        IEnumerable<Status> GetTransitions(string jiraRef);

        event EventHandler LoggedIn;
    }

    public class JiraConnection : IJiraConnection
    {
        private readonly ITrackUsage trackUsage;
        private readonly IRecentJiraCollection recentJiraCollection;
        private readonly List<string> jiraProjectCodeCache;
        private IJiraConnectionSettings jiraConnectionSettings;
        private IExportSettings exportSettings;
        private IJiraClient jira;
        private DateTime lastCacheUpdate;

        public User CurrentUser { get; private set; }
        public bool IsConnected => jira != null;

        public event EventHandler LoggedIn;

        public JiraConnection(ITrackUsage trackUsage, IRecentJiraCollection recentJiraCollection)
        {
            this.trackUsage = trackUsage;
            this.recentJiraCollection = recentJiraCollection;
            jiraProjectCodeCache = new List<string>();
            lastCacheUpdate = DateTime.MinValue;
        }

        public void ReConnect(IJiraConnectionSettings newJiraConnectionSettings, IExportSettings newExportSettings)
        {
            exportSettings = newExportSettings;
            jiraConnectionSettings = newJiraConnectionSettings;
            jira = null;
            CheckAndConnectJira();
            Task.Run(UpdateJiraProjectCache);
        }

        private void CheckAndConnectJira()
        {
            if (jira == null)
            {
                try
                {
                    jira = JiraClientFactory.BuildJiraClient(jiraConnectionSettings.JiraUrl, jiraConnectionSettings.JiraUsername, jiraConnectionSettings.JiraPassword, jiraConnectionSettings.UseTempo, jiraConnectionSettings.TempoToken);

                    CurrentUser = jira.GetCurrentUser();
                    LoggedIn?.Invoke(this, null);

                    TrackingType trackingType;
                    if (jiraConnectionSettings.UseTempo)
                    {
                        trackingType = jiraConnectionSettings.JiraUrl.Contains(".atlassian.net") ? TrackingType.JiraConnectCloudRestWithTempo : TrackingType.JiraConnectSelfhostRestWithTempo;
                    }
                    else
                    {
                        trackingType = jiraConnectionSettings.JiraUrl.Contains(".atlassian.net") ? TrackingType.JiraConnectCloudRest : TrackingType.JiraConnectSelfhostRest;
                    }

                    trackUsage.TrackAppUsage(trackingType);
                }
                catch (InvalidCredentialException)
                {
                    throw new MissingJiraConfigException("Required settings to create connection to jira are missing");
                }
                catch (ConnectionError ex) when (ex.ConnectionType == ConnectionError.Type.Tempo)
                {
                    throw new TempoConnectionException("Error creating instance of Tempo", ex);
                }
                catch (Exception ex)
                {
                    throw new JiraConnectionException("Error creating instance of Jira", ex);
                }
            }
        }

        public bool DoesJiraExist(string jiraRef)
        {
            try
            {
                CheckAndConnectJira();
                var issues = jira.GetIssuesFromJql($"key = \"{jiraRef}\"").ToList();

                if (issues.Any())
                {
                    recentJiraCollection.AddRecentJiras(issues);
                    return true;
                }
                else
                {
                    recentJiraCollection.Remove(jiraRef);
                    return false;
                }
            }
            catch (Exception)
            {
                recentJiraCollection.Remove(jiraRef);
                return false;
            }
        }

        public Issue GetJiraIssue(string jiraRef)
        {
            try
            {
                CheckAndConnectJira();
                var issue = jira.GetIssue(jiraRef);

                recentJiraCollection.AddRecentJira(issue);
                return issue;
            }
            catch (ConnectionError ex)
            {
                recentJiraCollection.Remove(jiraRef);
                throw new NoResultsFoundException($"Unable to locate Jira {jiraRef}", ex);
            }
        }

        public IEnumerable<string> GetJiraFilters()
        {
            try
            {
                CheckAndConnectJira();
                trackUsage.TrackAppUsage(TrackingType.SearchLoad);
                var returnedFilters = jira.GetFilters();
                return returnedFilters.Select(returned => returned.name);
            }
            catch (Exception ex)
            {
                throw new NoResultsFoundException("Error loading filters", ex);
            }
        }

        public IEnumerable<Issue> GetJiraIssuesFromFilter(string filterName)
        {
            try
            {
                CheckAndConnectJira();
                trackUsage.TrackAppUsage(TrackingType.SearchFilter);
                var filterJql = jira.GetJqlForFilter(filterName);
                var issues = jira.GetIssuesFromFilter(filterName).ToList();
                recentJiraCollection.AddRecentJiras(issues);

                if (filterJql.ToLower().Contains("order by"))
                {
                    return issues;
                }

                return issues.OrderBy(x => x.key, new JiraReferenceComparer());
            }
            catch (Exception ex)
            {
                throw new NoResultsFoundException("Error loading jiras from filter", ex);
            }
        }

        public IEnumerable<Issue> GetJiraIssuesFromSearchText(string searchText)
        {
            try
            {
                CheckAndConnectJira();
                trackUsage.TrackAppUsage(TrackingType.SearchText);
                List<Issue> issues;
                try
                {
                    issues = jira.GetIssuesFromJql(GetJql(searchText, true)).ToList();
                }
                catch (Exception)
                {
                    issues = jira.GetIssuesFromJql(GetJql(searchText, false)).ToList();
                }
                recentJiraCollection.AddRecentJiras(issues);
                return issues.OrderBy(x => x.key, new JiraReferenceComparer());
            }
            catch (Exception ex)
            {
                throw new NoResultsFoundException("Error loading jiras from search text", ex);
            }
        }

        public IEnumerable<Issue> GetJiraIssuesFromSearchTextLimitedToKeys(string searchText, IEnumerable<string> jiraKeys)
        {
            try
            {
                CheckAndConnectJira();
                trackUsage.TrackAppUsage(TrackingType.SearchText);
                List<Issue> issues;
                var keysJql = $"key in (\"{string.Join("\",\"", jiraKeys)}\")";
                try
                {
                    var searchTextJql = GetJql(searchText, true);
                    issues = jira.GetIssuesFromJql($"({keysJql}) AND ({searchTextJql})").ToList();
                }
                catch (Exception)
                {
                    var searchTextJql = GetJql(searchText, false);
                    issues = jira.GetIssuesFromJql($"({keysJql}) AND ({searchTextJql})").ToList();
                }

                recentJiraCollection.AddRecentJiras(issues);
                return issues.OrderBy(x => x.key, new JiraReferenceComparer());
            }
            catch (Exception ex)
            {
                throw new NoResultsFoundException("Error loading jiras from search text", ex);
            }
        }

        public IEnumerable<Issue> GetJiraCurrentUserOpenIssues()
        {
            try
            {
                CheckAndConnectJira();
                var issues = jira.GetIssuesFromJql("assignee in (currentUser()) AND resolved is EMPTY").ToList();
                recentJiraCollection.AddRecentJiras(issues);
                return issues.OrderBy(x => x.key, new JiraReferenceComparer());
            }
            catch (Exception ex)
            {
                throw new NoResultsFoundException("Error loading jiras from search text", ex);
            }
        }

        private void UpdateJiraProjectCache()
        {
            if (lastCacheUpdate < DateTime.UtcNow.AddHours(-2))
            {
                try
                {
                    lastCacheUpdate = DateTime.UtcNow;
                    CheckAndConnectJira();
                    var projects = jira.GetProjects();
                    jiraProjectCodeCache.Clear();
                    jiraProjectCodeCache.AddRange(projects.Select(project => project.key));
                }
                catch (Exception) { lastCacheUpdate = DateTime.MinValue; }
            }
        }

        public IEnumerable<StandardWorkLog> GetWorkLoggedForDates(IEnumerable<DateTime> queryDates)
        {
            return jira.GetWorkLoggedForDatesFilteredIssues(queryDates.ToList(), null);
        }

        public IEnumerable<StandardWorkLog> GetWorkLoggedForDatesFilteredIssues(IEnumerable<DateTime> queryDates, IEnumerable<string> issueRefs)
        {
            return jira.GetWorkLoggedForDatesFilteredIssues(queryDates.ToList(), issueRefs.ToList());
        }

        public void UpdateCache()
        {
            recentJiraCollection.RemoveExpiredCache();
            UpdateJiraProjectCache();
        }

        public void AssignToCurrentUser(string jiraRef)
        {
            try
            {
                CheckAndConnectJira();
                jira.AssignIssue(jiraRef, CurrentUser);
            }
            catch (Exception ex)
            {
                throw new JiraConnectionException("Unable to assign issue.", ex);
            }
        }

        public void LogTime(string jiraRef, DateTime exportTimeStamp, TimeSpan exportTime, WorkLogStrategy strategy, bool addStandardComment, string comment = "", TimeSpan? remainingTime = null)
        {
            trackUsage.TrackAppUsage(TrackingType.ExportOccurred);

            if (string.IsNullOrWhiteSpace(comment))
            {
                comment = exportSettings.EmptyExportComment;
            }

            if (!string.IsNullOrWhiteSpace(exportSettings.ExportCommentPrefix))
            {
                comment = $"{exportSettings.ExportCommentPrefix}: {comment}";
            }

            try
            {
                jira.AddWorkLog(jiraRef, strategy, comment, exportTime, DateTime.SpecifyKind(exportTimeStamp, DateTimeKind.Local), remainingTime);
            }
            catch (Exception ex)
            {
                throw new WorkLogException("Error logging work", ex);
            }

            if (addStandardComment)
            {
                try
                {
                    jira.AddComment(jiraRef, comment);
                }
                catch (Exception ex)
                {
                    throw new CommentException("Comment was not added", ex);
                }
            }
        }

        public IEnumerable<Status> GetTransitions(string jiraRef)
        {
            try
            {
                return jira.GetIssueTransitions(jiraRef).transitions;
            }
            catch (Exception ex)
            {
                throw new JiraConnectionException("Unable to get transitions.", ex);
            }
        }

        public void TransitionIssue(string jiraRef, string transition)
        {
            try
            {
                jira.TransitionIssue(jiraRef, transition);
            }
            catch (Exception ex)
            {
                throw new StateChangedException("Unable to change issue state.", ex);
            }
        }

        private string GetJql(string searchText, bool incProjects)
        {
            var projectQuery = string.Empty;
            var nonProjectText = string.Empty;
            foreach (var keyword in searchText.Split(' '))
            {
                if (incProjects)
                {
                    var firstProjectMatch = jiraProjectCodeCache.FirstOrDefault(x => x == keyword);
                    if (firstProjectMatch != null)
                    {
                        if (!string.IsNullOrWhiteSpace(projectQuery))
                        {
                            projectQuery += " OR ";
                        }
                        projectQuery += $"project = \"{firstProjectMatch}\"";
                    }
                    else
                    {
                        nonProjectText += $" {keyword}";
                    }
                }
                else
                {
                    nonProjectText += $" {keyword}";
                }
            }
            nonProjectText = nonProjectText.Trim();

            var keyQuery = string.Empty;
            try
            {
                if (searchText.Contains("-") && (recentJiraCollection.GetRecentJiraCollection().Any(x => x.JiraReference == searchText) || jira.GetIssue(searchText) != null))
                {
                    keyQuery = $"(key = \"{searchText}\")";
                }
            }
            catch { /*ignored*/ }

            var jql = string.Empty;
            if (!string.IsNullOrWhiteSpace(nonProjectText))
            {
                jql = string.IsNullOrWhiteSpace(jql) ? $"(Summary ~ \"{nonProjectText}\" OR Description ~ \"{nonProjectText}\")" : $"({jql}) AND (Summary ~ \"{nonProjectText}\" OR Description ~ \"{nonProjectText}\")";
            }
            if (!string.IsNullOrWhiteSpace(projectQuery))
            {
                jql = string.IsNullOrWhiteSpace(jql) ? $"({projectQuery})" : $"({jql}) AND ({projectQuery})";
            }

            if (!string.IsNullOrWhiteSpace(keyQuery))
            {
                jql = string.IsNullOrWhiteSpace(jql) ? $"({keyQuery})" : $"({jql}) OR ({keyQuery})";
            }

            return jql;
        }
    }
}
