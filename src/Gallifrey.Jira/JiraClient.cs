﻿using Gallifrey.Jira.Enum;
using Gallifrey.Jira.Model;
using Gallifrey.Rest;
using Gallifrey.Rest.Exception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Gallifrey.Jira
{
    public class JiraClient : IJiraClient
    {
        private readonly ISimpleRestClient jiraClient;
        private readonly ISimpleRestClient tempoClient;
        private readonly User myUser;

        public JiraClient(string baseUrl, string username, string password, bool useTempo, string tempoToken)
        {
            var url = baseUrl + (baseUrl.EndsWith("/") ? "" : "/") + "rest/api/2";
            jiraClient = SimpleRestClient.WithBasicAuthentication(url, username, password, GetJiraErrorMessages);

            try
            {
                myUser = GetCurrentUser();
            }
            catch (Exception e)
            {
                throw new ConnectionError(ConnectionError.Type.Jira, "Error connecting to jira", e);
            }

            if (useTempo)
            {
                tempoClient = SimpleRestClient.WithBearerAuthentication("https://api.tempo.io/4", tempoToken, GetTempoErrorMessages);

                try
                {
                    var queryDate = DateTime.UtcNow;
                    tempoClient.Get<TempoWorkLogSearch>(HttpStatusCode.OK, $"worklogs/user/{myUser.accountId}?from={queryDate:yyyy-MM-dd}&to={queryDate:yyyy-MM-dd}");
                }
                catch (Exception e)
                {
                    throw new ConnectionError(ConnectionError.Type.Tempo, "Error connecting to tempo", e);
                }
            }
            else
            {
                tempoClient = null;
            }
        }

        public User GetCurrentUser()
        {
            return jiraClient.Get<User>(HttpStatusCode.OK, "myself");
        }

        public Issue GetIssue(string issueRef)
        {
            return jiraClient.Get<Issue>(HttpStatusCode.OK, $"issue/{issueRef}");
        }

        public string GetJqlForFilter(string filterName)
        {
            var filters = jiraClient.Get<List<Filter>>(HttpStatusCode.OK, "filter/favourite");

            var selectedFilter = filters.FirstOrDefault(f => f.name == filterName);

            if (selectedFilter != null)
            {
                return selectedFilter.jql;
            }

            return string.Empty;
        }

        public IEnumerable<Issue> GetIssuesFromFilter(string filterName)
        {
            var jql = GetJqlForFilter(filterName);

            if (string.IsNullOrWhiteSpace(jql))
            {
                return new List<Issue>();
            }

            return GetIssuesFromJql(jql);
        }

        public IEnumerable<Issue> GetIssuesFromJql(string jql)
        {
            var returnIssues = new List<Issue>();

            if (string.IsNullOrWhiteSpace(jql))
            {
                return returnIssues;
            }

            var moreToGet = true;
            var startAt = 0;

            while (moreToGet)
            {
                var searchResult = jiraClient.Get<SearchResult>(HttpStatusCode.OK, $"search?jql={jql}&maxResults=999&startAt={startAt}&fields=summary,project,parent");

                returnIssues.AddRange(searchResult.issues);

                if (searchResult.total > returnIssues.Count)
                {
                    startAt += searchResult.maxResults;
                }
                else
                {
                    moreToGet = false;
                }
            }

            return returnIssues;
        }

        public IEnumerable<Project> GetProjects()
        {
            return jiraClient.Get<List<Project>>(HttpStatusCode.OK, "project");
        }

        public IEnumerable<Filter> GetFilters()
        {
            return jiraClient.Get<List<Filter>>(HttpStatusCode.OK, "filter/favourite");
        }

        public IEnumerable<StandardWorkLog> GetWorkLoggedForDatesFilteredIssues(List<DateTime> queryDates, List<string> issueRefs)
        {
            var workLogs = new List<StandardWorkLog>();
            var issues = issueRefs?.Select(GetIssue).ToList() ?? new List<Issue>();

            if (tempoClient != null)
            {
                foreach (var queryDate in queryDates)
                {
                    var logs = tempoClient.Get<TempoWorkLogSearch>(HttpStatusCode.OK, $"worklogs/user/{myUser.accountId}?from={queryDate:yyyy-MM-dd}&to={queryDate:yyyy-MM-dd}&limit=200").results;

                    // Make sure we have all the jira issues
                    issues.AddRange(logs.Where(x => !issues.Any(i => i.id == x.issue.id))
                                        .Select(x =>
                                                {
                                                    try
                                                    {
                                                        return GetIssue(x.issue.id.ToString());
                                                    }
                                                    catch (Exception)
                                                    {
                                                        return null;
                                                    }
                                                })
                                        .Where(x => x != null)
                                        .Distinct());

                    foreach (var tempoWorkLog in logs)
                    {
                        var issue = issues.FirstOrDefault(x => x.id == tempoWorkLog.issue.id);
                        if (issue != null && (issueRefs == null || !issueRefs.Any() || issueRefs.Contains(issue.key, StringComparer.InvariantCultureIgnoreCase)))
                        {
                            var workLogReturn = workLogs.FirstOrDefault(x => x.JiraRef == issue.key && x.LoggedDate.Date == queryDate.Date);
                            if (workLogReturn != null)
                            {
                                workLogReturn.AddTime(tempoWorkLog.timeSpentSeconds);
                            }
                            else
                            {
                                workLogs.Add(new StandardWorkLog(issue.key, queryDate.Date, tempoWorkLog.timeSpentSeconds));
                            }
                        }
                    }
                }
            }
            else
            {
                var workLogCache = new Dictionary<string, WorkLogs>();
                foreach (var queryDate in queryDates)
                {
                    var issuesExportedTo = GetIssuesFromJql($"worklogAuthor = currentUser() and worklogDate = {queryDate:yyyy-MM-dd}");
                    foreach (var issue in issuesExportedTo)
                    {
                        if (issueRefs == null || !issueRefs.Any() || issueRefs.Contains(issue.key, StringComparer.InvariantCultureIgnoreCase))
                        {
                            WorkLogs logs;
                            if (workLogCache.TryGetValue(issue.key, out var value))
                            {
                                logs = value;
                            }
                            else
                            {
                                logs = jiraClient.Get(HttpStatusCode.OK, $"issue/{issue.key}/worklog", customDeserialize: s => FilterWorklogsToUser(s, myUser));
                                workLogCache.Add(issue.key, logs);
                            }

                            foreach (var workLog in logs.worklogs.Where(x => x.started.Date == queryDate.Date))
                            {
                                var workLogReturn = workLogs.FirstOrDefault(x => x.JiraRef == issue.key && x.LoggedDate.Date == queryDate.Date);
                                if (workLogReturn != null)
                                {
                                    workLogReturn.AddTime(workLog.timeSpentSeconds);
                                }
                                else
                                {
                                    workLogs.Add(new StandardWorkLog(issue.key, queryDate.Date, workLog.timeSpentSeconds));
                                }
                            }
                        }
                    }
                }
            }

            return workLogs;
        }

        public Transitions GetIssueTransitions(string issueRef)
        {
            return jiraClient.Get<Transitions>(HttpStatusCode.OK, $"issue/{issueRef}/transitions?expand=transitions.fields");
        }

        public void TransitionIssue(string issueRef, string transitionName)
        {
            if (transitionName == null)
            {
                throw new ArgumentNullException(nameof(transitionName));
            }

            var transitions = GetIssueTransitions(issueRef);
            var transition = transitions.transitions.FirstOrDefault(t => t.name == transitionName) ?? throw new ClientException($"Unable to locate transition '{transitionName}'");

            var postData = new Dictionary<string, object>
            {
                { "transition", new { transition.id } }
            };

            jiraClient.Post(HttpStatusCode.NoContent, $"issue/{issueRef}/transitions", postData);
        }

        public void AddWorkLog(string issueRef, WorkLogStrategy workLogStrategy, string comment, TimeSpan timeSpent, DateTime logDate, TimeSpan? remainingTime = null)
        {
            if (logDate.Kind != DateTimeKind.Local)
            {
                logDate = DateTime.SpecifyKind(logDate, DateTimeKind.Local);
            }

            timeSpent = new TimeSpan(timeSpent.Hours, timeSpent.Minutes, 0);

            if (tempoClient != null)
            {
                if (string.IsNullOrWhiteSpace(comment))
                {
                    comment = "N/A";
                }

                var issue = GetIssue(issueRef);
                var remaining = 0d;
                switch (workLogStrategy)
                {
                    case WorkLogStrategy.Automatic:
                        remaining = issue.fields.timetracking.remainingEstimateSeconds - timeSpent.TotalSeconds;
                        if (remaining < 0)
                        {
                            remaining = 0;
                        }
                        break;

                    case WorkLogStrategy.LeaveRemaining:
                        remaining = issue.fields.timetracking.remainingEstimateSeconds;
                        break;

                    case WorkLogStrategy.SetValue:
                        remaining = remainingTime?.TotalSeconds ?? 0;
                        break;
                }

                var tempoWorkLog = new TempoWorkLogUpload { issueId = issue.id, timeSpentSeconds = timeSpent.TotalSeconds, startDate = $"{logDate:yyyy-MM-dd}", startTime = $"{logDate:hh:mm:ss}", description = comment, authorAccountId = myUser.accountId, remainingEstimateSeconds = remaining };
                tempoClient.Post(HttpStatusCode.OK, "worklogs", tempoWorkLog);
            }
            else
            {
                var postData = new Dictionary<string, object>
                {
                    { "started", $"{logDate:yyyy-MM-ddTHH:mm:ss.fff}{logDate.ToString("zzz").Replace(":", "")}"},
                    { "comment", comment },
                    { "timeSpent", $"{timeSpent.Hours}h {timeSpent.Minutes}m"},
                };
                var adjustmentMethod = string.Empty;
                var newEstimate = string.Empty;

                if (remainingTime.HasValue)
                {
                    newEstimate = $"{remainingTime.Value.Hours}h {remainingTime.Value.Minutes}m";
                }

                switch (workLogStrategy)
                {
                    case WorkLogStrategy.Automatic:
                        adjustmentMethod = "auto";
                        break;

                    case WorkLogStrategy.LeaveRemaining:
                        adjustmentMethod = "leave";
                        break;

                    case WorkLogStrategy.SetValue:
                        adjustmentMethod = "new";
                        break;
                }

                jiraClient.Post(HttpStatusCode.Created, $"issue/{issueRef}/worklog?adjustEstimate={adjustmentMethod}&newEstimate={newEstimate}&reduceBy=", postData);
            }
        }

        public void AssignIssue(string issueRef, User user)
        {
            var postData = new Dictionary<string, object>
            {
                { "accountId", user.accountId }
            };

            jiraClient.Put(HttpStatusCode.NoContent, $"issue/{issueRef}/assignee", postData);
        }

        public void AddComment(string issueRef, string comment)
        {
            var postData = new Dictionary<string, object>
            {
                { "body", comment }
            };

            jiraClient.Post(HttpStatusCode.Created, $"issue/{issueRef}/comment", postData);
        }

        private static WorkLogs FilterWorklogsToUser(string rawJson, User user)
        {
            var jsonObject = JObject.Parse(rawJson);
            var worklogs = jsonObject["worklogs"];
            if (worklogs != null)
            {
                var filtered = worklogs.Children().Where(x =>
                {
                    var logAccountId = x["author"]?["accountId"];
                    var logName = x["author"]?["name"];

                    var matchingAccountId = false;
                    var matchingUserName = false;
                    var matchingUserKey = false;

                    if (logAccountId != null)
                    {
                        var logAccountIdString = (string)logAccountId;
                        if (!string.IsNullOrWhiteSpace(logAccountIdString) && !string.IsNullOrWhiteSpace(user.accountId))
                        {
                            matchingAccountId = logAccountIdString.Equals(user.accountId, StringComparison.InvariantCultureIgnoreCase);
                        }
                    }

                    if (logName != null)
                    {
                        var logNameString = (string)logName;
                        if (!string.IsNullOrWhiteSpace(logNameString) && !string.IsNullOrWhiteSpace(user.name))
                        {
                            matchingUserName = logNameString.Equals(user.name, StringComparison.InvariantCultureIgnoreCase);
                        }

                        if (!string.IsNullOrWhiteSpace(logNameString) && !string.IsNullOrWhiteSpace(user.key))
                        {
                            matchingUserKey = logNameString.Equals(user.key, StringComparison.InvariantCultureIgnoreCase);
                        }
                    }

                    return matchingAccountId || matchingUserKey || matchingUserName;
                });

                jsonObject["worklogs"] = new JArray(filtered);
            }

            return jsonObject.ToObject<WorkLogs>();
        }

        private static List<string> GetJiraErrorMessages(string jsonString)
        {
            var errors = JsonConvert.DeserializeObject<JiraError>(jsonString);
            if (errors.errorMessages == null)
            {
                return new List<string>();
            }

            return errors.errorMessages;
        }

        private static List<string> GetTempoErrorMessages(string jsonString)
        {
            var errors = JsonConvert.DeserializeObject<TempoError>(jsonString);
            if (errors.errors == null)
            {
                return new List<string>();
            }

            return errors.errors.Select(x => x.message).ToList();
        }
    }
}
