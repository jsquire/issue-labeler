﻿using Hubbup.MikLabelModel;
using IssueLabeler.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace IssueLabeler
{
    internal class EventProcessor
    {
        private static Func<LabelSuggestion, Issue, float, bool> shouldLabel;

        static EventProcessor()
        {
            shouldLabel = (labelSuggestion, issue, threshold) =>
            {
                var topChoice = labelSuggestion.LabelScores.OrderByDescending(x => x.Score).First();
                return topChoice.Score >= threshold;
            };
        }

        public static void ProcessEvent(string eventBody, ILabelerLite labeler, ILogger _logger, IConfiguration _config)
        {
            if (eventBody == "This is an event body")
            {
                _logger.LogWarning(eventBody);
                return;
            }
            string eventType = null;
            string decoded = string.Empty;
            Models.IssueEvent payload = null;
            var elementMap = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(eventBody);


            foreach (var element in elementMap)
            {
                try
                {
                    if (element.Key == "headers")
                    {
                        eventType = element.Value.EnumerateObject().First(v => v.Name == "X-GitHub-Event").Value[0].GetString();
                        _logger.LogInformation($"Received event: '{eventType}'");
                    }
                    if (element.Key == "content")
                    {
                        decoded = Encoding.UTF8.GetString(Convert.FromBase64String(element.Value.GetString()));
                        _logger.LogTrace(decoded);
                        Type webhookType = eventType switch
                        {
                            "issues" => typeof(Models.IssueEvent),
                            _ => null,
                        };
                        if (webhookType == null)
                        {
                            _logger.LogError($"Unexpected webhook type: '{eventType}'");
                            continue;
                        }
                        payload = JsonSerializer.Deserialize<Models.IssueEvent>(decoded);
                        var repoInfo = payload.Repository.FullName.Split('/', 2);

                        // In order to avoid competing with other bots, we only want to respond to 'labeled' events where 
                        // where the label is the configured trigger (and default to "customer-reported").
                        _config.TryGetConfigValue($"IssueModel:{repoInfo[1]}:TriggerLabel", out var triggerLabel, "customer-reported");

                        if (payload.Action == "labeled" && payload.Label?.Name == triggerLabel)
                        {
                            // Process the issue

                            labeler.ApplyLabelPrediction(repoInfo[0], repoInfo[1], payload.Issue.Number, shouldLabel);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                    _logger.LogError(ex.StackTrace);
                    _logger.LogError(decoded);
                }
            }
        }
    }
}
