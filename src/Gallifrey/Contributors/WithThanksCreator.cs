﻿using System.Collections.Generic;

namespace Gallifrey.Contributors
{
    public class WithThanksCreator
    {
        public List<WithThanksDefinition> WithThanksDefinitions => new List<WithThanksDefinition>
        {
            new WithThanksDefinition{Name = "Mark Harrison", TwitterHandle = "@HarrisonMeister", GitHubHandle = "@HarrisonMeister", ThanksReason = "Code Contribution"},
            new WithThanksDefinition{Name = "Shaun Breach", TwitterHandle = "@mrshauneeb", GitHubHandle = "@mrshauneeb", ThanksReason = "QA Testing"},
            new WithThanksDefinition{Name = "Rich Green", TwitterHandle = "", GitHubHandle = "@richard-green", ThanksReason = "Code Contribution"},
            new WithThanksDefinition{Name = "15below", TwitterHandle = "@15below_travel", GitHubHandle = "@15below", ThanksReason = "Giving Developer Time"}
        };
    }
}
